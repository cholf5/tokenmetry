using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Services;

/// <summary>
/// 页面数据面门面（I-002）：CLI 数据源的"每日一行"形状与 facts 存储之间的存取。
/// Dashboard 汇总不再单独持久化快照，由 facts 按需重建（DashboardViewModel 负责）。
/// 线程模型同 <see cref="UsageQueryService"/>：调用方（DashboardViewModel 构造初始化）可能在
/// UI 线程，而 <c>Microsoft.Data.Sqlite</c> 的 *Async API 同步完成 + facts 全量读出后还要内存
/// 重聚合（<c>UsageFactsMapper.ToDailySummaries</c>）——公共入口经 <c>Task.Run</c> 把
/// "读 + 聚合"整体移到线程池，避免压在 UI 线程上。
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
    public Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default) =>
        Task.Run(() => SaveDailyRowsCoreAsync(rows, ct));

    private async Task SaveDailyRowsCoreAsync(List<DailyUsageSummary> rows, CancellationToken ct)
    {
        var facts = UsageFactsMapper.FromDailySummaries(rows);
        await _store.ReplaceUsageFactsByDayAsync(facts, ct);
    }

    /// <summary>读取 facts 并按日聚合回每日汇总（按日期降序，与旧缓存读取口径一致）。</summary>
    public Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default) =>
        Task.Run(() => GetDailyRowsCoreAsync(ct));

    private async Task<List<DailyUsageSummary>> GetDailyRowsCoreAsync(CancellationToken ct)
    {
        var facts = await _store.GetUsageFactsAsync(ct);
        return UsageFactsMapper.ToDailySummaries(facts);
    }
}
