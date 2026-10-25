using System;
using Tokenmetry.App.Localization;

namespace Tokenmetry.App.Services;

/// <summary>
/// 相对时间范围词表种类（D-020）。封闭词表：种类与偏移域收口才能自动生成标签
/// （用户预设不自由命名——i18n 唯一出处 Translations.cs 的前提），新种类属于语义扩展、
/// 需同步 IsValid / ToLabel / TimeRangePresetService 的投影往返。
/// </summary>
public enum RelativeRangeKind
{
    /// <summary>滚动窗口：最近 N 天（含今天），Days ∈ [2, 365]，Offset 恒 0。</summary>
    RollingDays,

    /// <summary>单日：今日(0) / 昨天(-1)。</summary>
    SingleDay,

    /// <summary>周：本周(0) / 上周(-1)，锚点固定周一（ISO / 中文习惯，有意不随系统区域浮动）。</summary>
    Week,

    /// <summary>月：本月(0) / 上月(-1)。</summary>
    Month,
}

/// <summary>
/// 相对时间范围统一模型（D-020）：内置档与用户自定义预设共用同一形状——内置档只是预定义
/// Spec 实例（Today = SingleDay(0)、本月 = Month(0)、最近 7 天 = RollingDays(7)…），
/// 持久化投影 / 相等性 / 标签 / 映射单点维护。
/// 口径：本地自然日含端点（同 <see cref="GlobalFilterMapper"/> 既有契约）；终点规则 =
/// 偏移 0 的未完周期收口今天（本月/本周随日生长），其余为该周期自然末日（上月末 / 上周日）。
/// </summary>
public sealed record RelativeRangeSpec(RelativeRangeKind Kind, int Offset = 0, int Days = 7)
{
    /// <summary>滚动天数合法域下界（1 天语义与 SingleDay 重叠，词表不收）。</summary>
    public const int MinRollingDays = 2;

    /// <summary>滚动天数合法域上界（远小于日序列物化上限 3653，防预设放大物化窗口）。</summary>
    public const int MaxRollingDays = 365;

    /// <summary>周期偏移合法域：仅 当前(0) / 上一(-1)——上上月/前天等语义留给自定义日期档。</summary>
    public const int MinOffset = -1;
    public const int MaxOffset = 0;

    public static RelativeRangeSpec Rolling(int days) => new(RelativeRangeKind.RollingDays, 0, days);
    public static RelativeRangeSpec SingleDay(int offset) => new(RelativeRangeKind.SingleDay, offset);
    public static RelativeRangeSpec Week(int offset) => new(RelativeRangeKind.Week, offset);
    public static RelativeRangeSpec Month(int offset) => new(RelativeRangeKind.Month, offset);

    /// <summary>
    /// 词表校验（添加预设的入口守卫）：RollingDays 天数 ∈ [<see cref="MinRollingDays"/>,
    /// <see cref="MaxRollingDays"/>] 且偏移恒 0；其余种类偏移 ∈ [<see cref="MinOffset"/>, <see cref="MaxOffset"/>]。
    /// </summary>
    public bool IsValid => Kind switch
    {
        RelativeRangeKind.RollingDays => Offset == 0 && Days is >= MinRollingDays and <= MaxRollingDays,
        RelativeRangeKind.SingleDay or RelativeRangeKind.Week or RelativeRangeKind.Month
            => Offset is >= MinOffset and <= MaxOffset,
        _ => false,
    };

    /// <summary>去重键：非滚动种类忽略 Days（Week(0,任意Days) 与 Week(0,7) 同语义）。</summary>
    public (RelativeRangeKind Kind, int Offset, int Days) DedupKey =>
        (Kind, Offset, Kind == RelativeRangeKind.RollingDays ? Days : 0);

    /// <summary>窗口起点（含）。周锚点固定周一；月为当月 1 日 + Offset 月。</summary>
    public DateOnly FromDay(DateOnly today) => Kind switch
    {
        RelativeRangeKind.RollingDays => today.AddDays(-(Days - 1)),
        RelativeRangeKind.SingleDay => today.AddDays(Offset),
        RelativeRangeKind.Week => StartOfWeek(today).AddDays(7 * Offset),
        RelativeRangeKind.Month => new DateOnly(today.Year, today.Month, 1).AddMonths(Offset),
        _ => today,
    };

    /// <summary>窗口终点（含）：偏移 0 的未完周期收口今天，其余为该周期自然末日。</summary>
    public DateOnly ToDay(DateOnly today) => Kind switch
    {
        RelativeRangeKind.RollingDays => today,
        RelativeRangeKind.SingleDay => today.AddDays(Offset),
        RelativeRangeKind.Week => Offset == 0 ? today : StartOfWeek(today).AddDays(7 * Offset + 6),
        RelativeRangeKind.Month => Offset == 0
            ? today
            : new DateOnly(today.Year, today.Month, 1).AddMonths(Offset + 1).AddDays(-1),
        _ => today,
    };

    /// <summary>标签自动生成（实时取串，语言切换重译经 TimeRangeOption.RefreshLabel 通知）。
    /// 滚动档走格式键（"最近 {0} 天"），中英文与旧 TimeRange7/30/90 固定键逐位相同。</summary>
    public string ToLabel() => Kind switch
    {
        RelativeRangeKind.RollingDays => string.Format(Translations.Instance.TimeRangeRollingDays, Days),
        RelativeRangeKind.SingleDay => Offset == 0
            ? Translations.Instance.TimeRangeToday
            : Translations.Instance.TimeRangeYesterday,
        RelativeRangeKind.Week => Offset == 0
            ? Translations.Instance.TimeRangeThisWeek
            : Translations.Instance.TimeRangeLastWeek,
        RelativeRangeKind.Month => Offset == 0
            ? Translations.Instance.TimeRangeThisMonth
            : Translations.Instance.TimeRangeLastMonth,
        _ => Translations.Instance.TimeRangeAll,
    };

    /// <summary>周一锚定周起点（DayOfWeek.Sunday=0，先 +6 折到周一起算再取模）。</summary>
    private static DateOnly StartOfWeek(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));
}
