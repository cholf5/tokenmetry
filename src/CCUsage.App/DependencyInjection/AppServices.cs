using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.Amp;
using CCUsage.Infrastructure.CCUsageCli;
using CCUsage.Infrastructure.Cache;
using CCUsage.Infrastructure.ClaudeCodeJsonl;
using CCUsage.Infrastructure.CodexJsonl;
using CCUsage.Infrastructure.CopilotSession;
using CCUsage.Infrastructure.DshSession;
using CCUsage.Infrastructure.Droid;
using CCUsage.Infrastructure.GeminiCli;
using CCUsage.Infrastructure.GooseSqlite;
using CCUsage.Infrastructure.HermesSqlite;
using CCUsage.Infrastructure.KiloSqlite;
using CCUsage.Infrastructure.KimiCode;
using CCUsage.Infrastructure.LocalIngestion;
using CCUsage.Infrastructure.OpenCodeSqlite;
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

    public static void Initialize()
    {
        var services = new ServiceCollection();
        var paths = AppPaths.CreateDefault();
        var settingsStore = new JsonAppSettingsStore(paths);
        var settings = settingsStore.LoadAsync().GetAwaiter().GetResult();

        services.AddSingleton(paths);
        services.AddSingleton<AppSettings>(settings);
        services.AddSingleton<IAppSettingsStore>(settingsStore);

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

        // 数据源：默认本地采集（I-005，无 ccusage CLI、断网可出数）；
        // CLI 数据源保留注册用于对账与手动导入（Reconciliation 测试套件 / 后续接线），不再是默认。
        services.AddSingleton<LocalIngestionDataSource>();
        services.AddSingleton<IUsageDataSource>(sp => sp.GetRequiredService<LocalIngestionDataSource>());
        services.AddSingleton(_ => new CCUsageCliDataSource(settings.CcusageCommand));

        // Facts 存储（I-002）：启动时执行顺序迁移
        Directory.CreateDirectory(paths.AppDataDirectory);
        var store = new SqliteUsageStore(paths.CacheDbPath);
        store.InitializeAsync().GetAwaiter().GetResult();
        services.AddSingleton<IUsageStore>(store);
        services.AddSingleton<CacheService>();

        // Notifications
        services.AddSingleton<ISystemNotification, NullSystemNotification>();

        // Export
        services.AddSingleton<ReportCsvFormatter>();
        services.AddSingleton<IReportCsvExporter, ReportCsvExporter>();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<ReportsViewModel>();
        services.AddTransient<ModelBreakdownViewModel>();
        services.AddTransient<SettingsViewModel>();

        _provider = services.BuildServiceProvider();
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
