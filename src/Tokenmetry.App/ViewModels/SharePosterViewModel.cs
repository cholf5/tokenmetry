using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
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
        // 总览基底页头的日期行：单日范围写「日期 · 周几」（两端同一天没有信息量）；
        // 多日补「N 个有记录的天」——范围天数会夸大活跃度（大段没开工具的日子也在范围里）
        RangeText = data.Request.IsSingleDay
            ? SingleDayDateText
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
        // 打卡热力图（样式 A）：窗口/格子/色阶是纯函数，构造时算好（视图 code-behind 只负责摆格子）
        Heat = SharePosterHeatmap.Build(data.Request.RangeStart, data.Request.RangeEnd, data.Daily);
        HeatActiveDaysText = DayCount.ToString("N0", CultureInfo.CurrentCulture);
        HeatPeakText = Heat.PeakDay is { } peak
            ? TrendChart.FormatCompactTokens(Heat.PeakTokens) + " · "
              + peak.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)
            : "—";
        // 时段节律（样式 B）：Core 的星期 × 小时矩阵（I-029 同源）→ 海报格子；查询失败为 null 时
        // 如实空场。峰值文案「周X HH:00 · 紧凑值」，unknown 归因小注复用时间页的同一份口径实现。
        Rhythm = SharePosterRhythm.Build(data.WeeklyRhythm);
        RhythmUnknownNote = HourlyAttribution.BuildUnknownNote(
            data.WeeklyRhythm?.UnknownSessionCount ?? 0,
            data.WeeklyRhythm?.Cells.Sum(c => c.SessionCount) ?? 0,
            data.WeeklyRhythm?.UnknownTotalTokens ?? 0);
        if (Rhythm.PeakWeekday is { } peakWeekday && Rhythm.PeakHour is { } peakHour)
        {
            RhythmPeakText = string.Format(
                CultureInfo.CurrentCulture,
                Translations.Instance.SharePosterRhythmPeakFormat,
                SharePosterHeatmap.WeekdayNames()[peakWeekday],
                peakHour.ToString("00", CultureInfo.InvariantCulture) + ":00",
                TrendChart.FormatCompactTokens(Rhythm.PeakTokens));
        }
        else
        {
            RhythmPeakText = "—";
        }
        // 里程碑（样式 C，设计修订九照 HTML 稿重排）：全部取自快照已有数据，零新查询——最活跃日
        // 扫完整范围（不受热力图 52 周开窗限制），note 行拆「日期 / 蓝色值」两段；最长连续从补零
        // 日序列里数 HasData 连段；Top 模型行给紧凑值 + 占比，徽标吃模型品牌链。
        var milestonePeak = data.Daily
            .Where(d => d.TotalTokens > 0)
            .OrderByDescending(d => d.TotalTokens)
            .FirstOrDefault();
        MilestonePeakDateText = milestonePeak is null
            ? "—"
            : DateOnly.ParseExact(milestonePeak.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                .ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        MilestonePeakValueText = milestonePeak is null
            ? string.Empty
            : "/ " + TrendChart.FormatCompactTokens(milestonePeak.TotalTokens);
        var streak = 0;
        var run = 0;
        foreach (var day in data.Daily)
        {
            run = day.HasData ? run + 1 : 0;
            streak = Math.Max(streak, run);
        }

        MilestoneStreakText = string.Format(
            CultureInfo.CurrentCulture,
            Translations.Instance.SharePosterMilestoneStreakFormat,
            streak.ToString("N0", CultureInfo.CurrentCulture));
        var topModel = data.Models.Count > 0 ? data.Models[0] : null;
        MilestoneTopModelName = topModel?.Name ?? "—";
        MilestoneTopModelCountText = topModel is null
            ? "—"
            : string.Format(
                CultureInfo.CurrentCulture,
                Translations.Instance.SharePosterMilestoneModelCountFormat,
                TrendChart.FormatCompactTokens(topModel.Tokens),
                Percent(topModel.Share));
        var resolveModelIcon = modelIconResolver ?? ModelAvatarVisual.FromName;
        MilestoneTopModelAvatar = topModel is null ? null : resolveModelIcon(topModel.Name);
        // 英雄锁定值与完整数字行（HTML 稿的 number/unit 与 exact 行）：紧凑串拆「尾数 + 单位」，
        // 单位单独绿色小字号；活跃天数卡的小字分母是范围天数（不是有记录天数——「7 / 7」里
        // 两个数字各说各的事）。
        (MilestoneHeroValueText, MilestoneHeroUnitText) = SplitCompactUnit(TotalTokensText);
        MilestoneExactTokensText = string.Format(
            CultureInfo.CurrentCulture,
            Translations.Instance.SharePosterMilestoneExactFormat,
            TotalTokensRawText);
        MilestoneRangeDayCountText = "/ " + (data.Request.RangeEnd.DayNumber - data.Request.RangeStart.DayNumber + 1)
            .ToString("N0", CultureInfo.CurrentCulture);
        // 模型行带品牌 logo（与总览页同一条链：ModelAvatarVisual → ModelChip 伪类 → 令牌画刷）；
        // 解析器来自模型目录（用户自定义头像也生效），未注入时回落内置名字推断。
        ModelRows = BuildRows(data.Models, modelIcons: resolveModelIcon);
        ProjectRows = BuildRows(data.Projects, data.Request.ProjectMask);
    }

    public SharePosterData Data { get; }

    /// <summary>
    /// 信息卡样式 = 托盘基底（设计 §3.1，固定竖版）；总览样式走总览页基底；打卡热力图样式走
    /// 热力基底（仅横版）。只看样式，与范围无关——样式与时间范围解耦后，多日范围也能出信息卡、
    /// 单日范围也能出总览。
    /// </summary>
    public bool IsTrayStyle => Data.Request.Style == SharePosterStyle.Tray;

    /// <summary>打卡热力图样式 = 热力基底（GitHub 贡献图式，仅横版）。</summary>
    public bool IsHeatmapStyle => Data.Request.Style == SharePosterStyle.Heatmap;

    /// <summary>时段节律样式 = 节律基底（星期 × 小时矩阵，仅横版）。</summary>
    public bool IsRhythmStyle => Data.Request.Style == SharePosterStyle.Rhythm;

    /// <summary>总览样式 = 总览页基底（版式竖/横皆可）。</summary>
    public bool IsOverviewStyle => Data.Request.Style == SharePosterStyle.Overview;

    /// <summary>
    /// 打卡热力图的窗口与格子（周为列/星期为行，最多 52 周；纯计算见 <see cref="SharePosterHeatmap"/>）。
    /// 其他样式也算好备用——代价只是窗口天数的纯计算，换样式时无需重跑。
    /// </summary>
    public SharePosterHeatGrid Heat { get; }

    /// <summary>热力图脚注：窗口内活跃天数（有记录的天，同 RangeText 的口径）。</summary>
    public string HeatActiveDaysText { get; }

    /// <summary>热力图脚注：单日峰值「紧凑值 · 日期」；全空窗口为「—」。</summary>
    public string HeatPeakText { get; }

    /// <summary>
    /// 时段节律（样式 B）的 7 × 24 格子与峰值（Core 查询失败/无数据 → 全零格、无峰值）。
    /// 其他样式也算好备用——代价只是 168 格的纯计算。
    /// </summary>
    public SharePosterRhythmGrid Rhythm { get; }

    /// <summary>节律峰值文案「周X HH:00 · 紧凑值」（PeakFormat 占位符在语言字典里）；全空矩阵为「—」。</summary>
    public string RhythmPeakText { get; }

    /// <summary>unknown 归因小注（与时间页同一份口径实现）；无粗精度会话时为 null（不显示）。</summary>
    public string? RhythmUnknownNote { get; }

    /// <summary>是否显示节律 unknown 小注。</summary>
    public bool ShowRhythmUnknownNote => RhythmUnknownNote is not null;

    /// <summary>里程碑样式 = 叙事基底（Wrapped 式大数字，仅竖版）。</summary>
    public bool IsMilestoneStyle => Data.Request.Style == SharePosterStyle.Milestone;

    /// <summary>里程碑英雄锁定值的尾数（拆自 <see cref="TotalTokensText"/>，单位单独绿色小字号）。</summary>
    public string MilestoneHeroValueText { get; }

    /// <summary>里程碑英雄锁定值的单位（如「亿」/" M"；未达首档全量数字时为空串，单位块隐藏）。</summary>
    public string MilestoneHeroUnitText { get; }

    /// <summary>单位块是否显示（空串不摆一个空位）。</summary>
    public bool HasMilestoneHeroUnit => MilestoneHeroUnitText.Length > 0;

    /// <summary>里程碑：英雄下完整数字行（千分位 + tokens 单位词）。</summary>
    public string MilestoneExactTokensText { get; }

    /// <summary>里程碑 note 最活跃日的日期段（yyyy.MM.dd）；无数据为「—」。</summary>
    public string MilestonePeakDateText { get; }

    /// <summary>里程碑 note 最活跃日的蓝色值段（「/ 紧凑值」）；无数据为空串（整段隐藏）。</summary>
    public string MilestonePeakValueText { get; }

    /// <summary>蓝色值段是否显示（全空范围不给「/ —」占位）。</summary>
    public bool HasMilestonePeakValue => MilestonePeakValueText.Length > 0;

    /// <summary>里程碑：最长连续使用天数（日序列 HasData 连段的最大长度）。</summary>
    public string MilestoneStreakText { get; }

    /// <summary>里程碑：活跃天数卡的小字分母「/ 范围天数」（有记录天数是另一个数字，各说各的事）。</summary>
    public string MilestoneRangeDayCountText { get; }

    /// <summary>里程碑：Top 模型名（模型排行首行）；无数据为「—」。</summary>
    public string MilestoneTopModelName { get; }

    /// <summary>里程碑：Top 模型行「紧凑 tokens · 占总量 x%」；无数据为「—」。</summary>
    public string MilestoneTopModelCountText { get; }

    /// <summary>里程碑：Top 模型的品牌徽标头像（解析失败为 null，徽标隐藏、名字照常）。</summary>
    public ModelAvatarVisual? MilestoneTopModelAvatar { get; }

    /// <summary>模型徽标是否显示（头像解析出来了才摆）。</summary>
    public bool HasMilestoneModelBadge => MilestoneTopModelAvatar is not null;

    /// <summary>是否单日窗口（决定趋势图形态与日期文案，与样式无关）。</summary>
    public bool IsSingleDay => Data.Request.IsSingleDay;

    /// <summary>单日海报页头的日期（带本地化星期：海报上写「周六」比 ISO 周几可读）。</summary>
    public string SingleDayDateText =>
        Data.Request.RangeStart.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)
        + " · " + Data.Request.RangeStart.ToString("ddd", CultureInfo.CurrentCulture);

    /// <summary>范围两端的「yyyy.MM.dd — yyyy.MM.dd」（信息卡多日头部的日期行用）。</summary>
    private string RangeDaysText =>
        Data.Request.RangeStart.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)
        + " — " +
        Data.Request.RangeEnd.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);

    /// <summary>信息卡头部的日期行：单日写「日期 · 周几」，多日写范围。</summary>
    public string TrayDateText => IsSingleDay ? SingleDayDateText : RangeDaysText;

    /// <summary>有记录的天数（不是范围天数——范围天数会夸大"活跃"）。</summary>
    public int DayCount => Data.Daily.Count(d => d.HasData);

    public string RangeText { get; }

    /// <summary>Token 趋势形态（D-027 同源）：单日范围且小时分布可用 → 小时折线，其余 → 日序列。</summary>
    public TrendChartMode TokenTrendMode =>
        Data.Request.IsSingleDay && Data.Hourly is not null
            ? TrendChartMode.HourlyTokens
            : TrendChartMode.DailyTokens;

    /// <summary>成本趋势形态（D-027 同源）：单日范围且小时分布可用 → 小时成本折线，其余 → 日成本折线。</summary>
    public TrendChartMode CostTrendMode =>
        Data.Request.IsSingleDay && Data.Hourly is not null
            ? TrendChartMode.HourlyCost
            : TrendChartMode.CostUsd;

    /// <summary>小时形态数据源与「已发生」边界（非单日范围 / 查询失败为 null，两张图回日序列形态）。</summary>
    public HourlyDistributionResult? Hourly => Data.Hourly;

    public int? HourlyCutoffHour => Data.HourlyCutoffHour;

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

    /// <summary>
    /// 紧凑串拆「尾数 + 单位」：<see cref="TrendChart.FormatCompactTokens"/> 恒为单位后缀（数字在前），
    /// 尾部连续非数字即单位——西文单位带前导不换行空格、俄语带句点，均非数字；未达首档的全量数字
    /// 尾部是数字，单位为空。各语种数字只含 ASCII 数字与分组/小数符，从尾部回扫不会切进尾数。
    /// </summary>
    private static (string Value, string Unit) SplitCompactUnit(string text)
    {
        var valueEnd = text.Length;
        while (valueEnd > 0 && !char.IsDigit(text[valueEnd - 1]))
        {
            valueEnd--;
        }

        return (text[..valueEnd], text[valueEnd..]);
    }

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
