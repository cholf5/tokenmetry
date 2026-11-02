using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using Tokenmetry.App.Controls;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 分享海报视图的展示模型：把 <see cref="SharePosterData"/> 的不可变切片转成已格式化的字符串 + 图表入参。
/// 快照本身就是不可变的，本类型不需要变更通知——海报是静态产物，没有交互。
/// </summary>
public sealed class SharePosterViewModel
{
    private static readonly Color[] SeriesColors =
    [
        Color.Parse("#4c8dff"), Color.Parse("#12b886"), Color.Parse("#ffad4d"),
        Color.Parse("#a779ff"), Color.Parse("#48bde3"), Color.Parse("#8394ae"),
    ];

    public SharePosterViewModel(SharePosterData data)
    {
        Data = data;
        var totals = data.Totals;

        // 只带范围本身；"有记录的天数"要新 i18n 键，留给 P7 一次补齐（跨 10 语种）
        RangeText = data.Request.RangeStart.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)
                    + " — " +
                    data.Request.RangeEnd.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);

        TotalTokensText = TrendChart.FormatCompactTokens(totals.TotalTokens);
        CacheShareText = Percent(totals.CacheShare);
        CostText = CostLabel(totals.Cost);
        SessionsText = totals.SessionCount.ToString("N0", CultureInfo.CurrentCulture);

        AgentSegments = BuildDonut(data.Agents);
        TypeSegments = BuildDonut(data.Types);
        ModelRows = BuildRows(data.Models);
        ProjectRows = BuildRows(data.Projects, data.Request.ProjectMask);
    }

    public SharePosterData Data { get; }

    /// <summary>有记录的天数（不是范围天数——范围天数会夸大"活跃"）。</summary>
    public int DayCount => Data.Daily.Count(d => d.HasData);

    public string RangeText { get; }

    public string TotalTokensText { get; }

    public string CacheShareText { get; }

    public string CostText { get; }

    public string SessionsText { get; }

    public IReadOnlyList<DailyUsagePoint> Daily => Data.Daily;

    public IEnumerable<DonutSegment> AgentSegments { get; }

    public IEnumerable<DonutSegment> TypeSegments { get; }

    public IReadOnlyList<SharePosterRow> ModelRows { get; }

    public IReadOnlyList<SharePosterRow> ProjectRows { get; }

    public bool HasModels => ModelRows.Count > 0;

    public bool HasProjects => ProjectRows.Count > 0;

    /// <summary>成本四态文案（未知成本不折 0，与总览页同口径）：null → "—"，未知 → 结尾 "+"。</summary>
    private static string CostLabel(AggregatedCost cost)
    {
        if (cost.KnownCostUsd is null)
        {
            return "—";
        }

        var text = "$" + cost.KnownCostUsd.Value.ToString("N2", CultureInfo.CurrentCulture);
        return cost.HasUnknownCost ? text + "+" : text;
    }

    private static string Percent(decimal? value) =>
        value is null ? "—" : (value.Value * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    /// <summary>局部雾化保留的可见字符数（够认出来源，不够读出项目名）。</summary>
    public const int MaskVisibleChars = 4;

    private static IReadOnlyList<SharePosterRow> BuildRows(
        IReadOnlyList<SharePosterRanking> rankings,
        SharePosterProjectMask mask = SharePosterProjectMask.Off)
    {
        // 条宽按本维度最大值归一（与总览页 mini bar 一致：最大行满格）
        var max = rankings.Count == 0 ? 0 : rankings.Max(r => r.Tokens);
        return rankings
            .Select(r =>
            {
                var visible = mask switch
                {
                    SharePosterProjectMask.Full => string.Empty,
                    SharePosterProjectMask.Partial when r.Name.Length > MaskVisibleChars
                        => r.Name[..MaskVisibleChars],
                    _ => r.Name,
                };
                var masked = visible.Length == r.Name.Length ? string.Empty : r.Name[visible.Length..];

                return new SharePosterRow(
                    visible,
                    TrendChart.FormatCompactTokens(r.Tokens),
                    Percent(r.Share),
                    max == 0 ? 0 : (double)r.Tokens / max * 100,
                    masked,
                    masked.Length > 0);
            })
            .ToArray();
    }

    private static IEnumerable<DonutSegment> BuildDonut(IReadOnlyList<SharePosterRanking> rankings) =>
        rankings.Select((r, i) => new DonutSegment(r.Name, r.Tokens, SeriesColors[i % SeriesColors.Length]));
}

/// <summary>
/// 排行表一行（已格式化，条宽 0..100）。<para>
/// 项目名雾化用"可见段 + 待模糊段"两段文本表示：模糊只改变视觉、不改变字符，两段一起布局
/// 才能保证行宽和未雾化时一致（用占位符替换会让排行表左右列错位）。
/// </para>
/// </summary>
public sealed record SharePosterRow(
    string Name,
    string TokensText,
    string ShareText,
    double BarValue,
    string MaskedName = "",
    bool HasMaskedName = false);
