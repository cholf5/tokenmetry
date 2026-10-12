namespace CCUsage.Core.Models;

/// <summary>小时分布的单小时桶（本地时区）。空桶 token 为 0、成本为 <see cref="AggregatedCost.Empty"/>。</summary>
public sealed class HourlyUsageBucket
{
    /// <summary>小时（0..23，本地时区）。</summary>
    public int Hour { get; init; }

    /// <summary>落入该小时的会话数（按会话开始时间 first_seen_at 的本地小时）。</summary>
    public long SessionCount { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long TotalTokens { get; init; }

    /// <summary>成本（未知成本契约同 I-028：<see cref="AggregatedCost"/>，null ≠ 0）。</summary>
    public AggregatedCost Cost { get; init; } = AggregatedCost.Empty;
}

/// <summary>
/// 小时级分布（I-029）：仅纳入有分钟级时间精度的会话，粗粒度时间戳的会话计入 unknown 计数
/// 并对调用方可见——<b>不伪造分布</b>。精度判定规则（详见 I-029 Development Log 2026-10-13，
/// 依据是 facts 里实际存储的时间形态，而非来源枚举）：
/// <list type="bullet">
/// <item>UTC 午夜锚定（UNIX 毫秒恰为一天毫秒数的整数倍，含纪元 1970-01-01T00:00:00Z 兜底标记）→
/// 无事件时间，unknown。日期级来源（Goose "{date}T00:00:00Z"）与各 loader 纪元兜底（Kimi /
/// OpenClaw / Grok / Gemini / Amp / Droid）都锚定 UTC 午夜，该判定与查询时区无关。</item>
/// <item>查询时区下时间OfDay恰为 00:00:00.000（本地日锚定值）→ 无法证明携带分钟级精度（日粒度
/// 来源与真实午夜事件不可辨），按"宁排除不伪造"计 unknown。</item>
/// <item>其余 → 视为携带分钟级精度，纳入分布。</item>
/// <item>已知边界：① 真实发生在 UTC 午夜 / 本地午夜的会话被保守排除并计入 unknown（可见而非丢失）；
/// ② 文件 mtime / now() 兜底在 facts 层与真实事件时间不可辨（I-002 不存精度元数据），无法在查询侧
/// 排除，登记为已知限制；权威的逐记录精度判断只能在采集层做（未来如需，走新迁移加列）。</item>
/// </list>
/// </summary>
public sealed class HourlyDistributionResult
{
    /// <summary>24 个小时桶（0..23，含空桶——调用方无需自行补零）。</summary>
    public IReadOnlyList<HourlyUsageBucket> Hours { get; init; } = Array.Empty<HourlyUsageBucket>();

    /// <summary>因无分钟级时间精度被排除的会话数（对调用方可见，页面按占比展示"未知"）。</summary>
    public long UnknownSessionCount { get; init; }

    /// <summary>被排除会话的总 token（可见性辅助：unknown 不只是行数，还有被排除的用量体量）。</summary>
    public long UnknownTotalTokens { get; init; }

    /// <summary>分布所用时区（facts 的 day 分桶与本地小时均按此时区；默认本机时区）。</summary>
    public string TimeZoneId { get; init; } = string.Empty;
}
