using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.CCUsageCli;
using CCUsage.Infrastructure.Cache;
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

        // Data sources - 使用真实的 ccusage CLI 数据源
        services.AddSingleton<IUsageDataSource>(_ => new CCUsageCliDataSource(settings.CcusageCommand));

        // Cache
        Directory.CreateDirectory(paths.AppDataDirectory);
        var cache = new SqliteUsageCache(paths.CacheDbPath);
        cache.InitializeAsync().GetAwaiter().GetResult();
        services.AddSingleton<IUsageCache>(cache);
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
