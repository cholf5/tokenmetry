using System.Collections.Generic;
using System.Globalization;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Tray;

/// <summary>
/// 托盘 tooltip 摘要行格式化（纯函数，接线在 TrayService）：首行产品名（<see cref="Translations.TrayTooltip"/>），
/// 次行今日摘要 = tokens（<see cref="TrendChart.FormatCompactTokens"/> 按当前语言取万/亿或 K/M 口径，
/// 语言切换即随之重译）+ 成本（<see cref="TrayStatusText.FormatCost"/>，恒 ASCII）+ 缓存命中
/// （share 0~1 → x.x%，null 显 "—"，同弹层 CacheShareText 口径）。无数据（<c>hasData=false</c>）
/// 第二行为诚实的无数据文案（TrayTodayNoData），不冒充 0。
/// </summary>
internal static class TrayTooltipBuilder
{
    public static IReadOnlyList<string> BuildLines(
        bool hasData,
        long todayTokens,
        decimal todayCostUsd,
        decimal? todayCacheShare)
    {
        if (!hasData)
        {
            return [Translations.Instance.TrayTooltip, Translations.Instance.TrayTodayNoData];
        }

        var summary = string.Format(
            CultureInfo.InvariantCulture,
            Translations.Instance.TrayTodaySummaryFormat,
            TrendChart.FormatCompactTokens(todayTokens),
            TrayStatusText.FormatCost(todayCostUsd),
            OverviewViewModel.FormatShare(todayCacheShare));
        return [Translations.Instance.TrayTooltip, summary];
    }
}
