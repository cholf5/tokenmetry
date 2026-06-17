namespace CCUsage.Core.Tests.Services;

using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

public class CacheServiceTests
{
    private readonly InMemoryUsageCache _cache;
    private readonly CacheService _service;

    public CacheServiceTests()
    {
        _cache = new InMemoryUsageCache();
        _service = new CacheService(_cache);
    }

    [Fact]
    public async Task Empty_cache_returns_null_dashboard()
    {
        var dashboard = await _service.GetLatestDashboardAsync();
        dashboard.Should().BeNull();
    }

    [Fact]
    public async Task Save_and_get_roundtrip()
    {
        var summary = new DashboardSummary { TodayCostUsd = 1.23m, TodayTokens = 1000 };

        await _service.SaveDashboardAsync(summary);
        var result = await _service.GetLatestDashboardAsync();

        result.Should().NotBeNull();
        result!.TodayCostUsd.Should().Be(1.23m);
        result.TodayTokens.Should().Be(1000);
    }
}

public class InMemoryUsageCache : IUsageCache
{
    private DashboardSummary? _dashboard;
    private List<DailyUsageSummary> _dailyRows = new();

    public Task SaveDashboardAsync(DashboardSummary summary, CancellationToken ct = default)
    {
        _dashboard = summary;
        return Task.CompletedTask;
    }

    public Task<DashboardSummary?> GetLatestDashboardAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_dashboard);
    }

    public Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default)
    {
        _dailyRows = rows;
        return Task.CompletedTask;
    }

    public Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_dailyRows);
    }
}
