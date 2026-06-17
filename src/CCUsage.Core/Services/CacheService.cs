using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.Core.Services;

public class CacheService
{
    private readonly IUsageCache _cache;

    public CacheService(IUsageCache cache)
    {
        _cache = cache;
    }

    public Task SaveDashboardAsync(DashboardSummary summary, CancellationToken ct = default)
    {
        return _cache.SaveDashboardAsync(summary, ct);
    }

    public Task<DashboardSummary?> GetLatestDashboardAsync(CancellationToken ct = default)
    {
        return _cache.GetLatestDashboardAsync(ct);
    }

    public Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default)
    {
        return _cache.SaveDailyRowsAsync(rows, ct);
    }

    public Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default)
    {
        return _cache.GetDailyRowsAsync(ct);
    }
}
