using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

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
    /// 模型聚合（别名映射）：服务持有 <see cref="IModelNameResolver"/> 时，facts 的模型键与会话的
    /// dominant_model 都先经 <see cref="IModelNameResolver.Resolve"/> 归一——不同原始名（大小写 /
    /// 厂商前缀 / 别名变体）按用户映射合并为规范名一行，会话归属、关联 Agent、token、成本与占比
    /// 全部在规范名口径上聚合；无映射（恒等）时行为与原契约逐位一致。
    /// </summary>
    Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default);

    /// <summary>
    /// 原始模型名候选清单（模型别名映射设置页的候选项）：本地数据（usage_facts 的模型键 + 会话的
    /// dominant_model）中出现过的<b>原始</b>模型名去重——有意绕过别名归一（映射的录入源必须是
    /// 映射前的名字）。按估算权重降序（facts 行与会话的 TotalTokens 之和）、平局按名称序数，
    /// 让常用名排在候选列表前面。
    /// </summary>
    Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default);

    /// <summary>项目维度聚合（I-029）：Key 为原始路径，DisplayName 为归一化项目名（歧义守卫见类型说明）。</summary>
    Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default);

    /// <summary>
    /// 会话明细列表（I-029）：搜索 + 组合筛选 + 排序 + 分页（契约见 <see cref="SessionListQuery"/>；
    /// <see cref="SessionListQuery.PageSize"/>&lt;= 0 抛 <see cref="ArgumentException"/>，null 才是不分页）。
    /// <see cref="SessionListQuery.Model"/> 按模型聚合口径过滤：与会话 dominant_model 的<b>别名归一名</b>
    /// 精确比较（下拉选项来自模型维度聚合，双方同在规范名空间）；搜索词对原始名与归一名都匹配。
    /// </summary>
    Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default);

    /// <summary>小时级分布（I-029）：仅纳入有分钟级时间精度的会话；粗粒度来源计入 unknown 计数并可见，
    /// 不伪造分布（判定规则见 <see cref="HourlyDistributionResult"/> 说明：UTC 午夜锚定与本地日锚定
    /// 都判为无精度，与查询时区无关地覆盖日期级来源）。</summary>
    Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default);

    /// <summary>
    /// 总览页复合快照（P0-2 性能收敛）：一次调用返回 KPI（含上一周期环比）+ 日序列 + Agent / 模型 /
    /// 项目三维聚合。各分量语义与逐方法调用逐位一致；范围校验一致（非法范围抛
    /// <see cref="ArgumentException"/>，双端有界日序列超上限抛 <see cref="ArgumentOutOfRangeException"/>）。
    /// 缺省实现为逐方法组合（仅供测试桩兜底，仍是一页多次全表读）；真实存储实现应覆写为
    /// 单次 facts + 单次 sessions 全表读、内存内一次算完全部分量。
    /// </summary>
    Task<OverviewPageSnapshot> GetOverviewSnapshotAsync(UsageQueryFilter filter, CancellationToken ct = default)
    {
        return ComposeAsync(filter, ct);

        async Task<OverviewPageSnapshot> ComposeAsync(UsageQueryFilter filter, CancellationToken ct)
        {
            var kpi = await GetKpiAsync(filter, ct);
            var daily = await GetDailySeriesAsync(filter, ct);
            var agents = await GetAgentBreakdownAsync(filter, ct);
            var models = await GetModelBreakdownAsync(filter, ct);
            var projects = await GetProjectBreakdownAsync(filter, ct);
            return new OverviewPageSnapshot
            {
                Kpi = kpi,
                Daily = daily,
                Agents = agents,
                Models = models,
                Projects = projects,
            };
        }
    }
}
