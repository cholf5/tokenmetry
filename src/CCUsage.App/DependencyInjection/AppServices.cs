using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.Antigravity;
using CCUsage.Infrastructure.Amp;
using CCUsage.Infrastructure.CCUsageCli;
using CCUsage.Infrastructure.Cache;
using CCUsage.Infrastructure.ClaudeCodeJsonl;
using CCUsage.Infrastructure.Codebuff;
using CCUsage.Infrastructure.CodexJsonl;
using CCUsage.Infrastructure.CopilotSession;
using CCUsage.Infrastructure.DshSession;
using CCUsage.Infrastructure.Droid;
using CCUsage.Infrastructure.GeminiCli;
using CCUsage.Infrastructure.Grok;
using CCUsage.Infrastructure.GooseSqlite;
using CCUsage.Infrastructure.HermesSqlite;
using CCUsage.Infrastructure.KiloSqlite;
using CCUsage.Infrastructure.KimiCode;
using CCUsage.Infrastructure.LocalIngestion;
using CCUsage.Infrastructure.OpenClaw;
using CCUsage.Infrastructure.OpenCodeSqlite;
using CCUsage.Infrastructure.PiAgent;
using CCUsage.Infrastructure.QwenCode;
using CCUsage.Infrastructure.ZCodeSqlite;
using CCUsage.Infrastructure.ModelsDev;
using CCUsage.Infrastructure.Notifications;
using CCUsage.App.Services;
using CCUsage.App.ViewModels;

namespace CCUsage.App.DependencyInjection;

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
        var settings = settingsStore.LoadAsync().GetAwaiter().GetResult();

        services.AddSingleton(paths);
        services.AddSingleton<AppSettings>(settings);
        services.AddSingleton<IAppSettingsStore>(settingsStore);
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IToastService, ToastService>();
        // D-003 全局筛选状态（时间范围 + Agent）：单例，顶部栏写入、各页 VM 订阅
        services.AddSingleton<FilterStateService>();

        // Core services
        services.AddSingleton<DateRangeService>();
        services.AddSingleton<DashboardService>();
        services.AddSingleton<AnomalyDetector>();

        // 定价引擎（I-003）：models.dev 客户端单例，后台刷新由本地采集数据源的刷新入口触发；
        // 用户覆盖规则来自设置（引擎支持运行时 UpdateOverrides）。
        var pricingClient = new ModelsDevPricingClient();
        services.AddSingleton(pricingClient);
        services.AddSingleton<IRemotePricingSource>(pricingClient);
        services.AddSingleton(_ => new PricingEngine(pricingClient, settings.PricingOverrides));

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

        // Facts 存储（I-002）：启动时执行顺序迁移
        Directory.CreateDirectory(paths.AppDataDirectory);
        var store = new SqliteUsageStore(paths.CacheDbPath);
        store.InitializeAsync().GetAwaiter().GetResult();
        services.AddSingleton<IUsageStore>(store);
        services.AddSingleton<CacheService>();

        // UI 聚合查询服务（I-028/I-029）：facts 存储之上的只读聚合面（日序列 / KPI / 维度 / 会话 / 小时分布），
        // 时区用本机（与采集侧 facts 分桶默认一致）。
        services.AddSingleton<IUsageQueryService>(new UsageQueryService(store));

        // D-008 筛选选项目录（Agent/模型/项目清单）：I-029 维度查询 + 服务层缓存，
        // 全局 Agent 下拉（FilterStateService）与右栏快速筛选共用同一份（单一真相源）。
        services.AddSingleton<AgentCatalogService>();

        // Notifications
        services.AddSingleton<ISystemNotification, NullSystemNotification>();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        // Q-002：DashboardViewModel 为后台采集刷新锚点（无视图挂载，headless VM——定案见其类型注释）；
        // 旧 ReportsViewModel / ModelBreakdownViewModel / TrendSpikeViewModel 随旧视图一并移除。
        services.AddTransient<DashboardViewModel>();
        // S-002：设置页 VM 仍为单例生命周期（MainWindowViewModel 持有同一实例，外壳 CreatePage 复用，
        // 主题选中态跨导航保持）；数据源表刷新经 INavigationAware.OnNavigatedTo（重进刷新，定案见卡 Dev Log）。
        services.AddSingleton<SettingsViewModel>();
        // D-003 右栏最近会话骨架（D-008 接真实数据）
        services.AddTransient<SessionsPanelViewModel>();

        // S-002 审查修复（P0）：后台刷新服务在容器内正常注册——设置页 VM 的嵌套构造注入与顶层
        // Get<T> 解析到同一实例。此前"late map（RegisterInstance 只拦截顶层 Get<T>，嵌套构造注入
        // 不经过它）+ 兜底空转实例"的设计使真机重扫命中兜底实例成假成功，两者均已删除（late map
        // 无其余调用方，兜底实例由 BackgroundRefreshWiring 的"未接线如实失败"语义取代）。
        // 刷新动作经 BackgroundRefreshWiring 执行：dashboard 与外壳 VM 在 App 启动序列就绪后 Attach；
        // headless 测试宿主不执行启动序列，其下未接线重扫得到明确的"扫描失败"而非假成功。
        var refreshWiring = new BackgroundRefreshWiring();
        services.AddSingleton(refreshWiring);
        services.AddSingleton(new BackgroundRefreshService(refreshWiring.RunRefreshAsync, settings.RefreshMinutes));

        _provider = services.BuildServiceProvider();

        // S-002：启动即物化本地采集源（构造侧向扫描注册表登记管道组合——"已识别的数据源"清单），
        // 不依赖 DashboardViewModel 的解析时机；真正的扫描仍只在刷新时发生。
        _provider.GetRequiredService<LocalIngestionDataSource>();
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
