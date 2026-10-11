using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.CCUsageCli;
using CCUsage.Infrastructure.Cache;
using CCUsage.Infrastructure.ClaudeCodeJsonl;
using CCUsage.Infrastructure.CodexJsonl;
using CCUsage.Infrastructure.LocalIngestion;
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

        // 本地采集适配器（I-005 接线）：单例——实例持有增量扫描状态（文件 mtime/大小记账 + 消息级
        // 去重），跨刷新只重解析变更文件；并发进入由 LocalIngestionDataSource 的管道信号量串行化。
        services.AddSingleton<ClaudeCodeJsonlAdapter>();
        services.AddSingleton<IClaudeCodeJsonlAdapter>(sp => sp.GetRequiredService<ClaudeCodeJsonlAdapter>());
        services.AddSingleton<CodexJsonlAdapter>();
        services.AddSingleton<ICodexJsonlAdapter>(sp => sp.GetRequiredService<CodexJsonlAdapter>());

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
