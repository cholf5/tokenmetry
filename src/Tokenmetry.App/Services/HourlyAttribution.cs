using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Services;

/// <summary>
/// 小时分布（I-029）的共用口径辅助（D-027 抽出）：时间分析页与总览页单日窗口共用同一套
/// unknown 占比文案与入桶会话计数——口径同级不是"看起来差不多"，而是同一份实现。
/// <para>
/// 归因口径提醒：小时分布按会话 <c>first_seen_at</c> 的本地小时归桶（整个会话的 token/成本记到
/// 开始那个小时），粗粒度时间戳的会话剔除并计入 unknown；这与按 <c>fact.Day</c> 归日的日序列/KPI
/// 不同口径——两处页面都必须把差异标注出来（文案见 <c>Translations.AttributionSessionStart</c>）。
/// </para>
/// </summary>
internal static class HourlyAttribution
{
    /// <summary>入桶会话数（= Σ 各桶 SessionCount；I-029 不变式：入桶 + unknown = 全部会话）。</summary>
    public static long SumKnownSessions(IReadOnlyList<HourlyUsageBucket> hours) =>
        hours.Sum(h => h.SessionCount);

    /// <summary>
    /// unknown 占比标注文案：
    /// 「N 个会话缺少精确时间，未计入小时分布（占 X.X%）」，unknown 会话携带 token 时追加
    /// 「，涉及 M tokens 用量」。占比分母 = 入桶 + unknown 会话数（每个会话非入桶即 unknown，
    /// 无第三态）；unknown 为 0 时返回 null（无需标注，页面隐藏）。
    /// </summary>
    public static string? BuildUnknownNote(long unknownSessions, long knownSessions, long unknownTokens)
    {
        if (unknownSessions <= 0)
        {
            return null;
        }

        var total = knownSessions + unknownSessions;
        var share = total > 0 ? (decimal)unknownSessions / total * 100m : 100m;
        var t = Translations.Instance;
        var note = string.Format(
            t.UnknownNoteFormat,
            unknownSessions.ToString("N0", CultureInfo.InvariantCulture),
            share.ToString("F1", CultureInfo.InvariantCulture));
        if (unknownTokens > 0)
        {
            note += string.Format(t.UnknownNoteTokensSuffix, TrendChart.FormatCompactTokens(unknownTokens));
        }

        return note;
    }
}
