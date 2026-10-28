namespace Tokenmetry.Core.Models;

/// <summary>
/// 总览页复合快照（P0-2 性能收敛）：KPI（含上一周期环比）+ 日序列 + Agent / 模型 / 项目三维聚合
/// 的一次调用产物——单页数据面的原子查询。各分量语义与逐方法调用逐位一致
/// （<see cref="UsageKpiResult"/> / <see cref="DailyUsagePoint"/> / <see cref="DimensionUsageRow"/>
/// 契约不变）；真实实现以"单次 facts 全表读 + 单次 sessions 全表读"算完全部分量，
/// 替代页面加载对存储的十余次全表重读。
/// </summary>
public sealed class OverviewPageSnapshot
{
    public UsageKpiResult Kpi { get; init; } = new();

    /// <summary>日序列（日历窗口语义与 <see cref="IUsageQueryService.GetDailySeriesAsync"/> 一致）。</summary>
    public IReadOnlyList<DailyUsagePoint> Daily { get; init; } = [];

    public IReadOnlyList<DimensionUsageRow> Agents { get; init; } = [];

    public IReadOnlyList<DimensionUsageRow> Models { get; init; } = [];

    public IReadOnlyList<DimensionUsageRow> Projects { get; init; } = [];
}
