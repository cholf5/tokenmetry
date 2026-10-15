using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

public interface IUsageDataSource
{
    Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default);

    /// <summary>
    /// 数据源是否已把本次结果持久化进 facts 存储（I-005 起的本地采集源为 true，
    /// 读取路径直接经 facts 存储完成）；为 false 时（CLI / Mock 源）由调用方经
    /// <see cref="CacheService"/> 把返回的每日汇总落库。
    /// </summary>
    bool PersistsFacts => false;
}
