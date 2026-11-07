namespace Tokenmetry.Core.Models;

/// <summary>星期 × 小时矩阵的单格（本地时区；星期一 = 0）。无用量的格 token 为 0、会话数为 0。</summary>
public sealed class WeeklyRhythmCell
{
    /// <summary>星期（0 = 周一 .. 6 = 周日，展示口径与打卡热力图的周一起一致）。</summary>
    public int Weekday { get; init; }

    /// <summary>小时（0..23，本地时区；按会话 first_seen_at 的本地时间归桶）。</summary>
    public int Hour { get; init; }

    /// <summary>落入该格的会话数（口径同小时分布：整个会话记到开始那个小时）。</summary>
    public long SessionCount { get; init; }

    public long TotalTokens { get; init; }
}

/// <summary>
/// 时段节律矩阵（I-029 同源的星期 × 小时分布）：仅纳入有分钟级时间精度的会话（判定与 unknown
/// 语义同 <see cref="HourlyDistributionResult"/>，详见其注释），粗粒度时间戳的会话计入 unknown
/// 并对调用方可见——不伪造分布。168 格全量返回（含空格），调用方无需自行补零。
/// </summary>
public sealed class WeeklyRhythmResult
{
    /// <summary>168 个格子（7 × 24，含空格），顺序为 weekday * 24 + hour。</summary>
    public IReadOnlyList<WeeklyRhythmCell> Cells { get; init; } = Array.Empty<WeeklyRhythmCell>();

    /// <summary>因无分钟级时间精度被排除的会话数（可见性口径同小时分布的 unknown）。</summary>
    public long UnknownSessionCount { get; init; }

    /// <summary>被排除会话的总 token（unknown 不只是行数，还有被排除的用量体量）。</summary>
    public long UnknownTotalTokens { get; init; }

    /// <summary>矩阵所用时区（本地小时与星期均按此时区；默认本机时区）。</summary>
    public string TimeZoneId { get; init; } = string.Empty;
}
