using CCUsage.Core.Models;

namespace CCUsage.Core.Abstractions;

public interface IUsageDataSource
{
    Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default);
}
