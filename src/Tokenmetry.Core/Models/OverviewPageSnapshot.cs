namespace Tokenmetry.Core.Models;

/// <summary>
/// 总览页复合快照（P0-2 性能收敛）：KPI（含上一周期环比）+ 日序列 + Agent / 设备 / 模型 / 项目聚合
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

    /// <summary>
    /// 设备维度聚合（跨端同步合并视图；Key = 设备 id，DisplayName 留空由 App 层按设备清单解析）。
    /// 口径 = KPI 的 (device, day) 粒度兜底：每设备 full 优先/分解兜底；device 非空前提下
    /// Σ设备 = KPI 合计。空设备键跳过（无主历史行不进设备环）。
    /// </summary>
    public IReadOnlyList<DimensionUsageRow> Devices { get; init; } = [];

    public IReadOnlyList<DimensionUsageRow> Models { get; init; } = [];

    public IReadOnlyList<DimensionUsageRow> Projects { get; init; } = [];
}
