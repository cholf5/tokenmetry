namespace CCUsage.Core.Models;

/// <summary>环比方向（I-028 KPI 区间对比）。</summary>
public enum ChangeDirection
{
    /// <summary>无变化，或上一周期无数据 / 百分比不可定义。</summary>
    Neutral,

    /// <summary>当前周期高于上一周期。</summary>
    Increase,

    /// <summary>当前周期低于上一周期。</summary>
    Decrease,
}

/// <summary>
/// 单指标环比结果：方向 + 百分比。百分比口径：(current - previous) / |previous| × 100，保留 2 位小数。
/// <list type="bullet">
/// <item>previous 为 null（上一周期无数据 / 无界范围没有上一周期）→ <see cref="Neutral"/>，百分比 null。</item>
/// <item>current 为 null（如当前周期成本全未知）而 previous 有值 → <see cref="Neutral"/>，百分比 null
/// （不臆造方向，审查 P3 补记）。</item>
/// <item>previous 为 0、current 非 0 → 方向仍给出（Increase/Decrease），百分比 null（除数为 0 不可定义，
/// 不伪造数字）。</item>
/// <item>current == previous → Neutral，百分比 0。</item>
/// </list>
/// </summary>
/// <param name="Direction">变化方向。</param>
/// <param name="PercentChange">百分比变化（0.01 = 1%）；null 表示不可定义（见类型说明）。</param>
public sealed record MetricChange(ChangeDirection Direction, decimal? PercentChange)
{
    /// <summary>无对比基准时的中性结果。</summary>
    public static MetricChange Neutral { get; } = new(ChangeDirection.Neutral, null);

    /// <summary>按契约计算环比（previous 为 null 表示上一周期无该指标数据）。</summary>
    public static MetricChange From(decimal? current, decimal? previous)
    {
        if (previous is not { } baseline)
        {
            return Neutral;
        }

        if (current is not { } value)
        {
            // 当前无已知值（如成本全未知）而上一周期有已知值：不臆造方向。
            return Neutral;
        }

        var direction = value > baseline ? ChangeDirection.Increase
            : value < baseline ? ChangeDirection.Decrease
            : ChangeDirection.Neutral;
        decimal? percent;
        if (direction == ChangeDirection.Neutral)
        {
            percent = 0m;
        }
        else if (baseline == 0m)
        {
            percent = null;
        }
        else
        {
            percent = Math.Round((value - baseline) / Math.Abs(baseline) * 100m, 2);
        }

        return new MetricChange(direction, percent);
    }
}

/// <summary>
/// KPI 区间对比的"上一周期"聚合值（I-028）：窗口定义为<b>等长回看</b>——当前窗口 [FromDay, ToDay]
/// 的自然日天数 N（含端点），上一周期为 [FromDay − N, FromDay − 1]，即紧邻当前窗口、长度相等的
/// 前一个窗口；过滤参数（Agent 等）与当前窗口一致。
/// </summary>
public sealed class KpiPeriodComparison
{
    /// <summary>上一周期起始自然日（含，yyyy-MM-dd）。</summary>
    public string FromDay { get; init; } = string.Empty;

    /// <summary>上一周期结束自然日（含，yyyy-MM-dd；恒为当前窗口 FromDay 的前一天）。</summary>
    public string ToDay { get; init; } = string.Empty;

    public long TotalTokens { get; init; }

    /// <summary>上一周期成本（未知成本契约同 <see cref="AggregatedCost"/>；与当前值的对比只覆盖
    /// 已知成本部分，两侧的 HasUnknownCost 由各自的 Cost 字段可见）。</summary>
    public AggregatedCost Cost { get; init; } = AggregatedCost.Empty;

    public long SessionCount { get; init; }

    /// <summary>上一周期缓存读取占比（0..1；总 token 为 0 时 null）。</summary>
    public decimal? CacheReadShare { get; init; }

    public MetricChange TotalTokensChange { get; init; } = MetricChange.Neutral;

    public MetricChange CostChange { get; init; } = MetricChange.Neutral;

    public MetricChange SessionCountChange { get; init; } = MetricChange.Neutral;

    public MetricChange CacheReadShareChange { get; init; } = MetricChange.Neutral;
}

/// <summary>
/// KPI 汇总（I-028）：区间总 token / 估算成本 / 会话数 / 缓存读取占比 + 上一周期环比。
/// 成本遵循未知成本契约（<see cref="AggregatedCost"/>：null ≠ 0，未知不折 0 混入合计）。
/// </summary>
public sealed class UsageKpiResult
{
    public long TotalTokens { get; init; }

    public long CacheReadTokens { get; init; }

    /// <summary>区间估算成本（已知成本 + 未知标记）。</summary>
    public AggregatedCost Cost { get; init; } = AggregatedCost.Empty;

    /// <summary>区间会话数（会话按开始日 <b>first_seen_at</b> 的本地自然日落入区间计；
    /// 去重口径 = Agent + Session ID，见 I-029 Development Log）。</summary>
    public long SessionCount { get; init; }

    /// <summary>缓存读取占比 = cache_read / total（0..1）；区间总 token 为 0 时 null（无基数，不伪造 0）。</summary>
    public decimal? CacheReadShare { get; init; }

    /// <summary>
    /// 上一周期（等长回看窗口）对比；null 表示无上一周期——范围无界（FromDay/ToDay 任一未设）
    /// 或范围非法。对比覆盖同样的四项指标。
    /// </summary>
    public KpiPeriodComparison? PreviousPeriod { get; init; }
}
