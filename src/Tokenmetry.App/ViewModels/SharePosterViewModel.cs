using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 分享海报视图的展示模型：把 <see cref="SharePosterData"/> 的不可变切片转成已格式化的字符串 + 图表入参。
/// 快照本身就是不可变的，本类型不需要变更通知——海报是静态产物，没有交互。
/// </summary>
public sealed class SharePosterViewModel
{
    public SharePosterViewModel(SharePosterData data, Func<string, ModelAvatarVisual?>? modelIconResolver = null)
    {
        Data = data;
        var totals = data.Totals;

        var range = data.Request.RangeStart.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)
                    + " — " +
                    data.Request.RangeEnd.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        // 多日窗口补「N 个有记录的天」：范围天数会夸大活跃度（大段没开工具的日子也在范围里）
        RangeText = data.Request.IsSingleDay
            ? range
            : range + " · " + string.Format(
                Translations.Instance.SharePosterDayCountFormat, data.Daily.Count(d => d.HasData));

        TotalTokensText = TrendChart.FormatCompactTokens(totals.TotalTokens);
        // 托盘基底在紧凑值下面再给一行完整数字（信息窗的近距离阅读尺度，不怕长）
        TotalTokensRawText = totals.TotalTokens.ToString("N0", CultureInfo.CurrentCulture);
        CacheShareText = Percent(totals.CacheShare);
        CostText = CostLabel(totals.Cost);
        SessionsText = totals.SessionCount.ToString("N0", CultureInfo.CurrentCulture);

        // 环形与总览页同源（同一个映射函数）：Agent 走 Top6 + 其他、段色走 AppSeries 令牌；
        // 类型构成走输入/输出/缓存（缓存 = 读取 + 写入）与固定三色令牌。
        AgentSegments = CreateAgentSegments(data.Agents);
        TypeSegments = OverviewViewModel.CreateTokenTypeSegments(data.Daily);
        TrayAgentRows = BuildTrayRows(data.Agents, 4);
        TrayModelRows = BuildTrayRows(data.Models, 3, modelIconResolver ?? ModelAvatarVisual.FromName);
        TrayTypes = BuildTrayTypes(TypeSegments);
        // 模型行带品牌 logo（与总览页同一条链：ModelAvatarVisual → ModelChip 伪类 → 令牌画刷）；
        // 解析器来自模型目录（用户自定义头像也生效），未注入时回落内置名字推断。
        ModelRows = BuildRows(data.Models, modelIcons: modelIconResolver ?? ModelAvatarVisual.FromName);
        ProjectRows = BuildRows(data.Projects, data.Request.ProjectMask);
    }

    public SharePosterData Data { get; }

    /// <summary>单日窗口 = 托盘信息窗基底（设计 §3.1）；版式固定竖版。</summary>
    public bool IsSingleDay => Data.Request.IsSingleDay;

    /// <summary>单日海报页头的日期（带本地化星期：海报上写「周六」比 ISO 周几可读）。</summary>
    public string SingleDayDateText =>
        Data.Request.RangeStart.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)
        + " · " + Data.Request.RangeStart.ToString("ddd", CultureInfo.CurrentCulture);

    /// <summary>有记录的天数（不是范围天数——范围天数会夸大"活跃"）。</summary>
    public int DayCount => Data.Daily.Count(d => d.HasData);

    public string RangeText { get; }

    public string TotalTokensText { get; }

    /// <summary>完整 token 数（千分位；单日托盘的三格 KPI 用）。</summary>
    public string TotalTokensRawText { get; }

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

    /// <summary>单日托盘：主要 Agent 行（Top4，按 token 降序，含成本与品牌标）。</summary>
    public IReadOnlyList<SharePosterTrayRow> TrayAgentRows { get; }

    /// <summary>单日托盘：模型用量行（Top3，按 token 降序，含成本与品牌标）。</summary>
    public IReadOnlyList<SharePosterTrayRow> TrayModelRows { get; }

    /// <summary>单日托盘：构成三卡（输入/输出/缓存，值是 token、占比分母为三段之和）。</summary>
    public IReadOnlyList<SharePosterTypeCard> TrayTypes { get; }

    public bool HasTrayAgents => TrayAgentRows.Count > 0;

    /// <summary>构成三段齐全（无数据时三张卡留空，不摆三个「0」冒充构成）。</summary>
    public bool HasTrayTypes => TrayTypes.Count >= 3;

    public bool HasTrayModels => TrayModelRows.Count > 0;

    /// <summary>模型行有品牌图标（模板用同一个 Bool 驱动芯片显隐）。</summary>
    public bool HasModelIcons => ModelRows.Any(r => r.HasIcon);

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

    /// <summary>
    /// Agent 环形段：Top6 + 「其他」聚合（同 <see cref="OverviewViewModel.CreateAgentSegments"/> 的口径），
    /// 但输入是海报快照的排行切片。段色不显式给——交给 <see cref="DonutChart"/> 走 AppSeries 令牌，
    /// 与应用内环形同色同序。
    /// </summary>
    internal static IReadOnlyList<DonutSegment> CreateAgentSegments(IReadOnlyList<SharePosterRanking> agents)
    {
        var withTokens = agents.Where(a => a.Tokens > 0).OrderByDescending(a => a.Tokens).ToList();
        var segments = withTokens.Take(6).Select(a => new DonutSegment(a.Name, a.Tokens)).ToList();
        var rest = withTokens.Skip(6).ToList();
        if (rest.Count > 0)
        {
            segments.Add(new DonutSegment(Translations.Instance.OthersSegment, rest.Sum(a => a.Tokens)));
        }

        return segments;
    }

    /// <summary>
    /// 托盘基底的行（主要 Agent / 模型用量共用）：TopN、按 token 降序、条宽按本组最大值归一。
    /// <paramref name="modelIcons"/> 为 null 时是 Agent 行（品牌标走 AgentVisualMap → AgentChip），
    /// 非 null 时是模型行（头像走 ModelChip，用户自定义头像也生效）。
    /// </summary>
    private static IReadOnlyList<SharePosterTrayRow> BuildTrayRows(
        IReadOnlyList<SharePosterRanking> rankings,
        int take,
        Func<string, ModelAvatarVisual?>? modelIcons = null)
    {
        var rows = rankings
            .Where(r => r.Tokens > 0)
            .OrderByDescending(r => r.Tokens)
            .Take(take)
            .ToList();
        var max = rows.Count == 0 ? 0 : rows.Max(r => r.Tokens);

        return rows.Select(r =>
        {
            var avatar = modelIcons?.Invoke(r.Name);
            var agent = modelIcons is null ? AgentVisualMap.Resolve(r.Name) : null;
            return new SharePosterTrayRow(
                r.Name,
                TrendChart.FormatCompactTokens(r.Tokens),
                OverviewViewModel.FormatCost(r.Cost ?? AggregatedCost.Empty),
                max == 0 ? 0 : (double)r.Tokens / max * 100,
                agent?.LogoVendor,
                agent?.Glyph.ToString() ?? string.Empty,
                avatar);
        }).ToArray();
    }

    /// <summary>三段构成 → 三张卡（值 + 占比）；占比分母取三段之和（与环形的分母一致）。</summary>
    private static IReadOnlyList<SharePosterTypeCard> BuildTrayTypes(IEnumerable<DonutSegment> segments)
    {
        var list = segments.ToList();
        var total = list.Sum(s => s.Value);
        return list.Select(s => new SharePosterTypeCard(
            s.Name,
            s.Value,
            TrendChart.FormatCompactTokens(s.Value),
            Percent(total == 0 ? null : (decimal)(s.Value / total)))).ToArray();
    }

    private static IReadOnlyList<SharePosterRow> BuildRows(
        IReadOnlyList<SharePosterRanking> rankings,
        SharePosterProjectMask mask = SharePosterProjectMask.Off,
        Func<string, ModelAvatarVisual?>? modelIcons = null)
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
                    masked.Length > 0,
                    modelIcons?.Invoke(r.Name));
            })
            .ToArray();
    }

}

/// <summary>单日托盘的行（主要 Agent / 模型用量）：名称、token、成本、条宽、品牌标。</summary>
/// <param name="AgentVendor">Agent 品牌变体名（模型行为 null；驱动 AgentChip 伪类）。</param>
/// <param name="AgentGlyph">Agent 字形兜底（无 lobe 标的 Agent）。</param>
/// <param name="Avatar">模型头像视觉（Agent 行为 null）。</param>
public sealed record SharePosterTrayRow(
    string Name,
    string TokensText,
    string CostText,
    double BarValue,
    string? AgentVendor = null,
    string AgentGlyph = "",
    ModelAvatarVisual? Avatar = null)
{
    public bool IsAgent => AgentVendor is not null;

    public bool HasModelIcon => Avatar is not null;

    /// <summary>品牌槽是否显示（任一图标存在）。</summary>
    public bool HasIcon => IsAgent || HasModelIcon;
}

/// <summary>单日托盘的构成卡（输入 / 输出 / 缓存）：值、紧凑文案与占比。</summary>
public sealed record SharePosterTypeCard(string Label, double Value, string ValueText, string ShareText);

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
    bool HasMaskedName = false,
    ModelAvatarVisual? Avatar = null)
{
    /// <summary>行首是否用品牌芯片替代（模型行 true；项目行没有品牌标，保持纯文本）。</summary>
    public bool HasIcon => Avatar is not null;
}
