using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.CCUsageCli;
using CCUsage.Infrastructure.Cache;
using CCUsage.Infrastructure.Notifications;
using CCUsage.App.ViewModels;

namespace CCUsage.App.DependencyInjection;

public static class AppServices
{
    private static IServiceProvider? _provider;

    public static void Initialize()
    {
        var services = new ServiceCollection();

        // Core services
        services.AddSingleton<DateRangeService>();
        services.AddSingleton<DashboardService>();
        services.AddSingleton<AnomalyDetector>();

        // Data sources - 默认使用模拟数据以便于测试，生产环境可切换到 CCUsageCliDataSource
        services.AddSingleton<IUsageDataSource>(new MockUsageDataSource(generateAnomalies: true));

        // Cache
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CCUsage",
            "ccusage.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var cache = new SqliteUsageCache(dbPath);
        cache.InitializeAsync().GetAwaiter().GetResult();
        services.AddSingleton<IUsageCache>(cache);
        services.AddSingleton<CacheService>();

        // Notifications
        services.AddSingleton<ISystemNotification, NullSystemNotification>();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<DashboardViewModel>();

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
