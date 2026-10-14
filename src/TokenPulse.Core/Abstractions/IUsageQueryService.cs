using TokenPulse.Core.Models;

namespace TokenPulse.Core.Abstractions;

/// <summary>
/// UI 聚合查询服务（I-028 日趋势 + KPI + 区间对比；I-029 维度聚合 + 会话明细 + 小时分布）：
/// facts 存储（<see cref="IUsageStore"/>）之上的只读聚合面，ViewModel 不直接写 SQL 的接缝层。
/// <paramref name="filter"/> 参数统一含时间范围（本地自然日，含端点）与 Agent 过滤，为 D-003
/// 全局筛选联动准备。
/// <b>未知成本契约（两张卡共用的纪律红线）</b>：缺价模型的成本以 <see cref="AggregatedCost"/> 显式
/// 区分 null 与 0（<see cref="AggregatedCost.KnownCostUsd"/>=null 表示"无一已知"，0 是"已知为零"），
/// <see cref="AggregatedCost.HasUnknownCost"/> 标记存在未知贡献；未知成本绝不折算为 0 混入合计，
/// 对齐 I-024/I-025 的 UnpricedModels 纪律。
/// </summary>
public interface IUsageQueryService
{
    /// <summary>
    /// 日粒度序列（I-028）：任意日期范围返回 in/out/cacheRead/cacheWrite/total/cost 按日聚合。
    /// 日历窗口语义（D-002 定案）：<see cref="UsageQueryFilter.FromDay"/> 与 <see cref="UsageQueryFilter.ToDay"/>
    /// 均设置时，范围内每个自然日都有点——无数据的日子补零并以 <see cref="DailyUsagePoint.HasData"/>=false
    /// 标记（UI 图层可辨识）；任一端无界时只返回实际有数据的日期（无界范围无从补零）。
    /// 双端有界的跨度上限为 <see cref="UsageQueryFilter.MaxDailySeriesDays"/> 天（含端点），超出抛
    /// <see cref="ArgumentOutOfRangeException"/>（补零物化点数受控）。范围非法 / 日期文本不可解析抛
    /// <see cref="ArgumentException"/>。
    /// 同日混源时以 full 粒度行为准（CLI 分解行不重复计数，对齐 <c>UsageFactsMapper.ToDailySummaries</c> 口径）。
    /// </summary>
    Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default);

    /// <summary>
    /// KPI 汇总（I-028）：区间总 token / 估算成本 / 会话数 / 缓存读取占比 + 上一周期环比
    /// （等长回看窗口：<see cref="UsageQueryFilter.FromDay"/>/ToDay 均设置时才计算，否则 PreviousPeriod 为 null）。
    /// </summary>
    Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default);

    /// <summary>Agent 维度聚合（I-029）：会话数、输入/输出/总 token、成本、占比。</summary>
    Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default);

    /// <summary>
    /// 模型维度聚合（I-029）：会话数按 dominant_model 归属；空模型键跳过（对齐既有页面口径）。
    /// D-010 契约演进：额外填充 <see cref="DimensionUsageRow.DominantAgent"/> / <see cref="DimensionUsageRow.AgentCount"/>
    /// （同一批 session_facts 按 Agent 计数派生的"关联 Agent"，仅本维度非 null）。
    /// </summary>
    Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default);

    /// <summary>项目维度聚合（I-029）：Key 为原始路径，DisplayName 为归一化项目名（歧义守卫见类型说明）。</summary>
    Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default);

    /// <summary>
    /// 会话明细列表（I-029）：搜索 + 组合筛选 + 排序 + 分页（契约见 <see cref="SessionListQuery"/>；
    /// <see cref="SessionListQuery.PageSize"/>&lt;= 0 抛 <see cref="ArgumentException"/>，null 才是不分页）。
    /// </summary>
    Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default);

    /// <summary>小时级分布（I-029）：仅纳入有分钟级时间精度的会话；粗粒度来源计入 unknown 计数并可见，
    /// 不伪造分布（判定规则见 <see cref="HourlyDistributionResult"/> 说明：UTC 午夜锚定与本地日锚定
    /// 都判为无精度，与查询时区无关地覆盖日期级来源）。</summary>
    Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default);
}
