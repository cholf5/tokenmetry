using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.Core.Services;

/// <summary>
/// 页面数据面门面（I-002）：CLI 数据源的"每日一行"形状与 facts 存储之间的存取。
/// Dashboard 汇总不再单独持久化快照，由 facts 按需重建（DashboardViewModel 负责）。
/// </summary>
public class CacheService
{
    private readonly IUsageStore _store;

    public CacheService(IUsageStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 把每日汇总分解为 facts 并按日替换写入（同一批重复写入幂等；
    /// 日内消失的 model / agent / project 分解行不残留）。
    /// </summary>
    public async Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default)
    {
        var facts = UsageFactsMapper.FromDailySummaries(rows);
        await _store.ReplaceUsageFactsByDayAsync(facts, ct);
    }

    /// <summary>读取 facts 并按日聚合回每日汇总（按日期降序，与旧缓存读取口径一致）。</summary>
    public async Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default)
    {
        var facts = await _store.GetUsageFactsAsync(ct);
        return UsageFactsMapper.ToDailySummaries(facts);
    }
}
