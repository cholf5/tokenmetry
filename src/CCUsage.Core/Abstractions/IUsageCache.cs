using CCUsage.Core.Models;

namespace CCUsage.Core.Abstractions;

public interface IUsageCache
{
    Task SaveDashboardAsync(DashboardSummary summary, CancellationToken ct = default);
    Task<DashboardSummary?> GetLatestDashboardAsync(CancellationToken ct = default);
    Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default);
    Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default);
}
