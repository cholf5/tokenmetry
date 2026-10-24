using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Antigravity;
using Tokenmetry.Infrastructure.Amp;
using Tokenmetry.Infrastructure.CCUsageCli;
using Tokenmetry.Infrastructure.Cache;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.Codebuff;
using Tokenmetry.Infrastructure.CodexJsonl;
using Tokenmetry.Infrastructure.CopilotSession;
using Tokenmetry.Infrastructure.DshSession;
using Tokenmetry.Infrastructure.Droid;
using Tokenmetry.Infrastructure.GeminiCli;
using Tokenmetry.Infrastructure.Grok;
using Tokenmetry.Infrastructure.GooseSqlite;
using Tokenmetry.Infrastructure.HermesSqlite;
using Tokenmetry.Infrastructure.KiloSqlite;
using Tokenmetry.Infrastructure.KimiCode;
using Tokenmetry.Infrastructure.LocalIngestion;
using Tokenmetry.Infrastructure.OpenClaw;
using Tokenmetry.Infrastructure.OpenCodeSqlite;
using Tokenmetry.Infrastructure.PiAgent;
using Tokenmetry.Infrastructure.QwenCode;
using Tokenmetry.Infrastructure.ZCodeSqlite;
using Tokenmetry.Infrastructure.ModelsDev;
using Tokenmetry.Infrastructure.Notifications;
using Tokenmetry.App.Services;
using Tokenmetry.App.Update;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.DependencyInjection;

public static class AppServices
{
    private static IServiceProvider? _provider;

    /// <summary>
    /// 测试宿主的 AppData 根覆盖（A-2）：headless 测试进程启动真实 App（OnFrameworkInitializationCompleted
    /// → Initialize）时，store / settings 落到测试临时目录，<b>禁止触碰真实用户数据</b>
    /// （此前无条件 CreateDefault() 使测试读写本机 LocalApplicationData 下的 ccusage.db）。
    /// 须在首次 <see cref="Initialize"/> 前设置（SemiTestAppBuilder.BuildAvaloniaApp 内）。
    /// </summary>
    public static AppPaths? PathsOverride { get; set; }

    public static void Initialize()
    {
        var services = new ServiceCollection();
        var paths = PathsOverride ?? AppPaths.CreateDefault();
        var settingsStore = new JsonAppSettingsStore(paths);
        // 启动期 sync-over-async 死锁修复（真机实测启动假死）：本方法在主线程于 Avalonia 启动
        // 序列内执行，此时 Dispatcher 尚未泵消息。直接 GetResult() 等待异步 IO 时，一旦某个
        // await 真异步完成（如 settings.json 存在时 JsonSerializer.DeserializeAsync 的续延），
        // 续延被投递到 AvaloniaSynchronizationContext（= 主线程），而主线程正阻塞在本等待上——
        // 互相等死。Task.Run 把整条异步链搬到线程池（无线程池外的同步上下文，续延就地完成），
        // GetResult 拿到的是已完成的 Task。首例触发：语言自动保存首次写出 settings.json 后，
        // 下一次启动 LoadAsync 走读取路径即卡死（settings.json 不存在时 LoadAsync 同步短路，
        // 从不触发——故此雷长期潜伏）。
        var settings = Task.Run(() => settingsStore.LoadAsync()).GetAwaiter().GetResult();

        services.AddSingleton(paths);
        services.AddSingleton<AppSettings>(settings);
        services.AddSingleton<IAppSettingsStore>(settingsStore);
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IToastService, ToastService>();
        // I-039 更新检查（设置页与托盘菜单共享同一状态；网络只在实际检查/下载时发生）
        services.AddSingleton(sp => new UpdateService(
            new UpdateChecker(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IAppSettingsStore>()));
        // S-006 开机自启动注册（设置页开关的 OS 出口；headless 测试宿主禁止真实调用，见管理器注释）
        services.AddSingleton<IAutoStartManager, AutoStartManager>();
        // D-003 全局筛选状态（时间范围 + Agent）：单例，顶部栏写入、各页 VM 订阅
        services.AddSingleton<FilterStateService>();
        // 时间范围持久化：启动应用上次的档位/自定义起止，此后顶部栏变更即存（FilterStatePersistence）
        services.AddSingleton<FilterStatePersistence>();

        // Core services
        services.AddSingleton<DateRangeService>();
        services.AddSingleton<DashboardService>();
        services.AddSingleton<AnomalyDetector>();

        // 定价引擎（I-003）：models.dev 客户端单例，后台刷新由本地采集数据源的刷新入口触发。
        // I-035 模型目录：价格 / 厂商 / 图标 / 聚合身份的唯一出处，单例共享给计价引擎与查询服务
        // （两份目录会让"设置页改的价"与"统计用的身份"分叉）。用户层来自设置：ModelDefinitions 是
        // 新字段，PricingOverrides 是旧字段（只读桥，装载时折算成目录条目，不丢老配置）——
        // 合并口径在 ModelCatalog.MergeUserDefinitions 一处，设置页热更新也必须走同一份。
        var pricingClient = new ModelsDevPricingClient();
        services.AddSingleton(pricingClient);
        services.AddSingleton<IRemotePricingSource>(pricingClient);
        services.AddSingleton(_ => new ModelCatalog(
            pricingClient,
            ModelCatalog.MergeUserDefinitions(settings),
            settings.ModelAliases));
        services.AddSingleton(sp => new PricingEngine(catalog: sp.GetRequiredService<ModelCatalog>()));

        // 本地采集适配器（I-005 接线）：单例——JSONL 适配器实例持有增量扫描状态（文件 mtime/大小记账 + 消息级
        // 去重），跨刷新只重解析变更文件；ZCode SQLite 适配器无扫描状态（账本单文件持续变化，每次全量重读）；
        // DSH 会话适配器（I-007）与 Copilot CLI 会话适配器（I-008）持有与 Claude/Codex 同一套增量记账状态
        // （变更的日志整文件重解析，差分/fold 状态只在单文件解析内存活）。
        // 并发进入由 LocalIngestionDataSource 的管道信号量串行化。
        services.AddSingleton<ClaudeCodeJsonlAdapter>();
        services.AddSingleton<IClaudeCodeJsonlAdapter>(sp => sp.GetRequiredService<ClaudeCodeJsonlAdapter>());
        services.AddSingleton<CodexJsonlAdapter>();
        services.AddSingleton<ICodexJsonlAdapter>(sp => sp.GetRequiredService<CodexJsonlAdapter>());
        services.AddSingleton<ZCodeSqliteAdapter>();
        services.AddSingleton<IZCodeSqliteAdapter>(sp => sp.GetRequiredService<ZCodeSqliteAdapter>());
        services.AddSingleton<DshSessionAdapter>();
        services.AddSingleton<IDshSessionAdapter>(sp => sp.GetRequiredService<DshSessionAdapter>());
        services.AddSingleton<CopilotSessionAdapter>();
        services.AddSingleton<ICopilotSessionAdapter>(sp => sp.GetRequiredService<CopilotSessionAdapter>());
        services.AddSingleton<OpenCodeSqliteAdapter>();
        services.AddSingleton<IOpenCodeSqliteAdapter>(sp => sp.GetRequiredService<OpenCodeSqliteAdapter>());
        services.AddSingleton<GooseSqliteAdapter>();
        services.AddSingleton<IGooseSqliteAdapter>(sp => sp.GetRequiredService<GooseSqliteAdapter>());
        services.AddSingleton<HermesSqliteAdapter>();
        services.AddSingleton<IHermesSqliteAdapter>(sp => sp.GetRequiredService<HermesSqliteAdapter>());
        services.AddSingleton<KiloSqliteAdapter>();
        services.AddSingleton<IKiloSqliteAdapter>(sp => sp.GetRequiredService<KiloSqliteAdapter>());
        services.AddSingleton<GeminiCliAdapter>();
        services.AddSingleton<IGeminiCliAdapter>(sp => sp.GetRequiredService<GeminiCliAdapter>());
        services.AddSingleton<AmpAdapter>();
        services.AddSingleton<IAmpAdapter>(sp => sp.GetRequiredService<AmpAdapter>());
        services.AddSingleton<DroidAdapter>();
        services.AddSingleton<IDroidAdapter>(sp => sp.GetRequiredService<DroidAdapter>());
        services.AddSingleton<QwenCodeAdapter>();
        services.AddSingleton<IQwenCodeAdapter>(sp => sp.GetRequiredService<QwenCodeAdapter>());
        services.AddSingleton<KimiCodeAdapter>();
        services.AddSingleton<IKimiCodeAdapter>(sp => sp.GetRequiredService<KimiCodeAdapter>());
        services.AddSingleton<CodebuffAdapter>();
        services.AddSingleton<ICodebuffAdapter>(sp => sp.GetRequiredService<CodebuffAdapter>());
        services.AddSingleton<PiAgentAdapter>(sp => new PiAgentAdapter(
            pricingEngine: sp.GetRequiredService<PricingEngine>())); // PI-3（I-024）：identity cost 与主管道同一引擎（用户覆盖 / 远程数据同源）。
        services.AddSingleton<IPiAgentAdapter>(sp => sp.GetRequiredService<PiAgentAdapter>());
        services.AddSingleton<GrokAdapter>();
        services.AddSingleton<IGrokAdapter>(sp => sp.GetRequiredService<GrokAdapter>());
        services.AddSingleton<OpenClawAdapter>();
        services.AddSingleton<IOpenClawAdapter>(sp => sp.GetRequiredService<OpenClawAdapter>());
        services.AddSingleton<AntigravityAdapter>();
        services.AddSingleton<IAntigravityAdapter>(sp => sp.GetRequiredService<AntigravityAdapter>());

        // 数据源：默认本地采集（I-005，无 ccusage CLI、断网可出数）；
        // CLI 数据源保留注册用于对账与手动导入（Reconciliation 测试套件 / 后续接线），不再是默认。
        // S-002：采集扫描状态注册表（单例）随本地采集源装配——管道组合（19 个适配器）在此注册，
        // 每轮刷新逐源登记扫描快照，设置页数据源表读取（"已识别数据源"不手写清单）。
        services.AddSingleton<IngestionScanRegistry>();
        services.AddSingleton<IIngestionScanReader>(sp => sp.GetRequiredService<IngestionScanRegistry>());
        services.AddSingleton<LocalIngestionDataSource>();
        services.AddSingleton<IUsageDataSource>(sp => sp.GetRequiredService<LocalIngestionDataSource>());
        services.AddSingleton(_ => new CCUsageCliDataSource(settings.CcusageCommand));

        // Facts 存储（I-002）：启动时执行顺序迁移（同上：经 Task.Run 隔离异步链，防启动期
        // sync-over-async 死锁——SQLite 迁移内部的 await 同样可能真异步完成）
        Directory.CreateDirectory(paths.AppDataDirectory);
        var store = new SqliteUsageStore(paths.CacheDbPath);
        Task.Run(() => store.InitializeAsync()).GetAwaiter().GetResult();
        services.AddSingleton<IUsageStore>(store);
        services.AddSingleton<CacheService>();

        // 模型名归一解析（I-035 起由模型目录承担）：别名映射是目录的一层，查询服务与计价引擎
        // 共享同一份目录——"设置页关联的名字"因此同时决定聚合与价格（此前只有聚合）。
        services.AddSingleton<IModelNameResolver>(sp => sp.GetRequiredService<ModelCatalog>());

        // UI 聚合查询服务（I-028/I-029）：facts 存储之上的只读聚合面（日序列 / KPI / 维度 / 会话 / 小时分布），
        // 时区用本机（与采集侧 facts 分桶默认一致）；模型维度经模型目录归一（无映射时恒等）。
        services.AddSingleton<IUsageQueryService>(sp =>
            new UsageQueryService(store, modelNameResolver: sp.GetRequiredService<ModelCatalog>()));

        // D-008 筛选选项目录（Agent/模型/项目清单）：I-029 维度查询 + 服务层缓存，
        // 全局 Agent 下拉（FilterStateService）与右栏快速筛选共用同一份（单一真相源）。
        services.AddSingleton<AgentCatalogService>();

        // Notifications
        // A-11：异常检测通知接真实出口（in-app toast）——此前 NullSystemNotification 使
        // 「启用异常通知」成为假开关（检测管线真实运行但通知被吞）。
        services.AddSingleton<ISystemNotification, ToastSystemNotification>();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        // Q-002：DashboardViewModel 为后台采集刷新锚点（无视图挂载，headless VM——定案见其类型注释）；
        // 旧 ReportsViewModel / ModelBreakdownViewModel / TrendSpikeViewModel 随旧视图一并移除。
        services.AddTransient<DashboardViewModel>();
        // S-002/S-004：设置页与数据与计价页 VM 均为单例生命周期（MainWindowViewModel 持有同一实例，
        // 外壳 CreatePage 复用，主题选中态 / 数据源表跨导航保持）；数据与计价页的数据源表刷新经
        // INavigationAware.OnNavigatedTo（重进刷新，定案见 S-002 卡 Dev Log）。
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DataPricingViewModel>();
        // 右栏（SessionsPanelViewModel）已随 2026-10-18 右栏撤除删除：最近会话直接删、
        // 快速筛选并入顶栏全局四维（docs/plans/2026-10-18-right-rail-removal-design.md）。

        // S-002 审查修复（P0）：后台刷新服务在容器内正常注册——设置页 / 数据与计价页 VM 的嵌套
        // 构造注入与顶层 Get<T> 解析到同一实例。此前"late map（RegisterInstance 只拦截顶层 Get<T>，
        // 嵌套构造注入不经过它）+ 兜底空转实例"的设计使真机重扫命中兜底实例成假成功，两者均已删除（late map
        // 无其余调用方，兜底实例由 BackgroundRefreshWiring 的"未接线如实失败"语义取代）。
        // 刷新动作经 BackgroundRefreshWiring 执行：dashboard 与外壳 VM 在 App 启动序列就绪后 Attach；
        // headless 测试宿主不执行启动序列，其下未接线重扫得到明确的"扫描失败"而非假成功。
        var refreshWiring = new BackgroundRefreshWiring();
        services.AddSingleton(refreshWiring);
        // 间隔下限钳到 1 分钟：settings.json 手改可绕过设置页 1–1440 校验，非正数会让
        // Timer 构造（dueTime/period ≤0）抛异常崩启动；默认已在 AppSettings 层改为 1 分钟。
        services.AddSingleton(new BackgroundRefreshService(refreshWiring.RunRefreshAsync, Math.Max(1, settings.RefreshMinutes)));

        _provider = services.BuildServiceProvider();

        // S-002：启动即物化本地采集源（构造侧向扫描注册表登记管道组合——"已识别的数据源"清单），
        // 不依赖 DashboardViewModel 的解析时机；真正的扫描仍只在刷新时发生。
        _provider.GetRequiredService<LocalIngestionDataSource>();

        // 时间范围持久化接线：先于外壳 VM 解析应用上次的档位/自定义起止（各页 VM 构造时读到的
        // 已是恢复后的筛选），此后顶部栏变更即存。设置必须已加载（上方 LoadAsync）。
        _provider.GetRequiredService<FilterStatePersistence>().Apply();
    }

    public static T Get<T>() where T : notnull
    {
        if (_provider == null)
        {
            throw new InvalidOperationException("AppServices not initialized. Call Initialize() first.");
        }

        return _provider.GetRequiredService<T>();
    }
}
