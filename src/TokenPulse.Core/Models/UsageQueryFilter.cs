using System.Globalization;

namespace TokenPulse.Core.Models;

/// <summary>
/// 聚合查询的统一过滤参数（I-028，为 D-003 全局筛选联动准备）：时间范围 + Agent。
/// 日期为本地时区自然日（facts 的 day 列即本地时区分桶的 yyyy-MM-dd 文本），含端点；
/// null 表示不设界（"全部时间"）。
/// </summary>
public sealed class UsageQueryFilter
{
    /// <summary>
    /// 日序列补零的范围上限（天，含端点；10 年含闰日余量）。双端有界的日序列最多物化这么多点，
    /// 超出抛 <see cref="ArgumentOutOfRangeException"/>（审查 P2：无上限的极端 filter 可物化数百万点）。
    /// </summary>
    public const int MaxDailySeriesDays = 3653;

    /// <summary>起始自然日（含），yyyy-MM-dd；null = 不设下界。</summary>
    public string? FromDay { get; init; }

    /// <summary>结束自然日（含），yyyy-MM-dd；null = 不设上界。</summary>
    public string? ToDay { get; init; }

    /// <summary>Agent 精确过滤（采集来源名，如 "Claude Code"）；null/空白 = 所有 Agent。</summary>
    public string? Agent { get; init; }

    /// <summary>范围是否合法（FromDay ≤ ToDay；无界视为合法）。</summary>
    public bool IsRangeValid =>
        FromDay is null || ToDay is null
        || string.CompareOrdinal(FromDay, ToDay) <= 0;

    /// <summary>解析自然日文本（yyyy-MM-dd，序数校验失败的输入返回 null）。</summary>
    public static DateOnly? ParseDay(string? day)
    {
        if (string.IsNullOrWhiteSpace(day))
        {
            return null;
        }

        return DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>自然日 → facts 的 day 文本（yyyy-MM-dd）。</summary>
    public static string FormatDay(DateOnly day) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
