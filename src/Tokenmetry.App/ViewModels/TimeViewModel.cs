using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// D-021 时间 KPI 卡展示模型（高峰时段 / 最活跃日 / 日均 tokens / 活跃天数）：主数值 + 数据脚注
/// （无数据 / 未加载时脚注为 null 隐藏，主数值 "—"）。不可变小对象，由 <see cref="TimeViewModel"/>
/// 整体替换实例触发绑定刷新（同 <see cref="OverviewViewModel.KpiCardViewModel"/> 纪律）。
/// </summary>
public sealed class TimeKpiCardViewModel
{
    public TimeKpiCardViewModel(string value, string? foot)
    {
        Value = value;
        Foot = foot;
    }

    /// <summary>加载完成前 / 无数据占位（诚实：不冒充 0）。</summary>
    public static TimeKpiCardViewModel Pending { get; } = new("—", null);

    /// <summary>主数值（高峰时段 HH:00–HH:00 / 日期 / 紧凑 tokens / 日数千分位）。</summary>
    public string Value { get; }

    /// <summary>数据脚注（口径说明携带数据：占比 / 当日量 / 平均基数 / 窗口天数），无则 null。</summary>
    public string? Foot { get; }

    public bool HasFoot => Foot is not null;
}

/// <summary>
/// D-021 星期分布单行（周一..周日固定七行）：星期名 + token 合计 + 占比 + bar 值。
/// bar 口径同 <see cref="DimensionRowViewModel"/>：占窗口内全部 token 的百分比（0..100，
/// ProgressBar.mini 直接消费）；行尾文案 = 千分位 tokens · 一位小数占比。
/// </summary>
public sealed class WeekdayUsageRowViewModel
{
    public WeekdayUsageRowViewModel(string label, long totalTokens, double sharePercent, string valueText)
    {
        Label = label;
        TotalTokens = totalTokens;
        BarValue = Math.Clamp(sharePercent, 0d, 100d);
        ValueText = valueText;
    }

    /// <summary>星期名（Translations 周一..周日，随语言）。</summary>
    public string Label { get; }

    /// <summary>该星期合计 token（HasData 日聚合）。</summary>
    public long TotalTokens { get; }

    /// <summary>bar 值 = 占 Σ token 百分比（0..100）。</summary>
    public double BarValue { get; }

    /// <summary>行尾文案（"6,750 · 77.1%"，InvariantCulture）。</summary>
    public string ValueText { get; }
}

/// <summary>
/// D-012 时间维度建议观察项表行（原型「时间维度建议观察项」三行：标题 + 说明）。
/// 静态文案块——描述观察方法与数据边界，不携带也不伪造任何数据；内容与真实数据能力一致
/// （见 <see cref="TimeViewModel.CreateObservations"/> 的逐行依据）。
/// </summary>
public sealed class TimeObservationRowViewModel
{
    public TimeObservationRowViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    /// <summary>观察项名（原型第一列：高峰时段 / 会话长度 / 数据边界）。</summary>
    public string Title { get; }

    /// <summary>观察方法说明（原型第二列；与 facts 实际存储能力一致，不承诺没有的字段）。</summary>
    public string Description { get; }
}

/// <summary>
/// D-012 时间分析页 VM（原型 #page-time）：按小时分布（D-004 TrendChart HourlyTokens，本地时区）+
/// 活跃日历热力图（D-005 Heatmap，日 → Token 强度）+ 时间维度建议观察项说明表（静态三行）。
/// D-021 扩充（零新查询，同源客户端聚合）：时间 KPI 行（<see cref="BuildTimeKpis"/>：高峰时段 /
/// 最活跃日 / 日均 tokens / 活跃天数）+ 星期分布（<see cref="BuildWeekdayDistribution"/>，周一..周日
/// 七行 + 工作日/周末占比）——落实页副标题「识别使用高峰、工作时段分布」中尚无数据呈现的两项。
/// 数据来自 <see cref="IUsageQueryService"/>（I-029 小时分布 + I-028 日序列），参数由全局筛选
/// （<see cref="FilterStateService"/>）经 <see cref="GlobalFilterMapper"/> 映射。
/// <b>unknown 口径（I-029 契约）</b>：无分钟级时间精度的会话不入小时桶（不伪造分布），以
/// <see cref="HourlyDistributionResult.UnknownSessionCount"/>/UnknownTotalTokens 可见——页面在分布图下
/// 标注「N 个会话缺少精确时间，未计入小时分布（占 X%）」；占比分母 = 入桶会话数 + unknown 会话数
/// （每个会话要么入桶要么计入 unknown，I-029 测试锁定的不变式）。
/// <b>热力图补零日选型（决策点，Dev Log 已记录）</b>：双端有界窗口（7/30/90 天）日序列含补零日
/// （<see cref="DailyUsagePoint.HasData"/>=false），以淡格（Heatmap 空态底色 + 「无数据」tooltip）呈现——
/// 日历连续性可辨（对齐 D-002 补零定案与 TrendChart DailyTokens 同一语义）；无界 All 只返回实际有数据日，
/// 自然只画实际日。
/// 生命周期 / 线程 / 序号防护纪律同 <see cref="OverviewViewModel"/>：每次导航新建实例、外壳 Dispose 退订、
/// FilterStateService 通知已在 UI 线程；加载失败保留上一次成功数据 + 错误面板（同会话页 P2-2 纪律）。
/// </summary>
public partial class TimeViewModel : ViewModelBase, IDisposable, IDataRefreshAware
{
    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _query;

    /// <summary>加载序号：重置型加载自增；跨 await 序号不一致的响应/异常直接丢弃（同 OverviewViewModel）。</summary>
    private int _loadSequence;

    public PageDescriptor Page { get; }

    /// <summary>小时分布（I-029，0..23 全量桶 + unknown 计数；TrendChart HourlyTokens 直接消费）。</summary>
    [ObservableProperty]
    private HourlyDistributionResult? _hourlyDistribution;

    /// <summary>热力图格子（I-028 日序列 → 日 Token 强度；补零日 HasData=false 淡格呈现）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeatmapCells))]
    private IReadOnlyList<HeatmapCell>? _heatmapCells;

    /// <summary>高峰时段卡（D-021，入桶 token 最大小时；无有效高峰 "—"）。</summary>
    [ObservableProperty]
    private TimeKpiCardViewModel _peakHourCard = TimeKpiCardViewModel.Pending;

    /// <summary>最活跃日卡（D-021，HasData 日 token 最大；并列取最早日）。</summary>
    [ObservableProperty]
    private TimeKpiCardViewModel _peakDayCard = TimeKpiCardViewModel.Pending;

    /// <summary>日均 tokens 卡（D-021，Σ tokens / 活跃日数——补零日不摊薄，口径见脚注）。</summary>
    [ObservableProperty]
    private TimeKpiCardViewModel _dailyAverageCard = TimeKpiCardViewModel.Pending;

    /// <summary>活跃天数卡（D-021，HasData 日数；脚注按窗口有界/无界分口径）。</summary>
    [ObservableProperty]
    private TimeKpiCardViewModel _activeDaysCard = TimeKpiCardViewModel.Pending;

    /// <summary>星期分布七行（D-021，周一..周日；Σ token 为 0 → null 整块隐藏，不画全零行伪信息）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWeekdayRows))]
    private IReadOnlyList<WeekdayUsageRowViewModel>? _weekdayRows;

    /// <summary>工作日 / 周末占比 meta（无数据 → null 隐藏）。</summary>
    [ObservableProperty]
    private string? _weekdayMetaText;

    public bool HasWeekdayRows => WeekdayRows is { Count: > 0 };

    public bool HasWeekdayMeta => WeekdayMetaText is not null;

    /// <summary>unknown 占比标注（unknown 会话数为 0 时为 null → 视图隐藏；失败保留上一次成功标注）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnknownNote))]
    private string? _unknownNoteText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? _loadError;

    public bool HasUnknownNote => UnknownNoteText is not null;

    public bool HasHeatmapCells => HeatmapCells is { Count: > 0 };

    public bool HasLoadError => LoadError is not null;

    /// <summary>建议观察项三行（静态文案，页面固定展示；内容见 <see cref="CreateObservations"/>）。</summary>
    public IReadOnlyList<TimeObservationRowViewModel> Observations { get; } = CreateObservations();

    public TimeViewModel(PageDescriptor page, FilterStateService filters, IUsageQueryService query)
    {
        Page = page;
        _filters = filters;
        _query = query;
        filters.PropertyChanged += OnFilterPropertyChanged;
        _ = LoadAsync();
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (FilterStateService.AffectsQueries(e.PropertyName))
        {
            _ = LoadAsync();
        }
    }

    /// <summary>后台采集刷新轮次完成后重查本页（IDataRefreshAware）：复用 LoadAsync，序号防护保证与筛选重查并发时旧响应丢弃。</summary>
    public Task OnDataRefreshedAsync() => LoadAsync();

    /// <summary>按当前全局筛选重查小时分布 + 日序列（序号防护见类型说明）。</summary>
    public async Task LoadAsync()
    {
        var sequence = ++_loadSequence;
        var filter = GlobalFilterMapper.Build(_filters);

        try
        {
            var hourly = await _query.GetHourlyDistributionAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            var daily = await _query.GetDailySeriesAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            HourlyDistribution = hourly;
            UnknownNoteText = BuildUnknownNote(hourly.UnknownSessionCount, SumKnownSessions(hourly.Hours), hourly.UnknownTotalTokens);
            // 补零日保留为淡格（HasData=false），不在此丢弃——日历窗口连续性是选型的一部分（见类型说明）。
            HeatmapCells = daily
                .Select(p => new HeatmapCell { Label = p.Day, Value = p.TotalTokens, HasData = p.HasData })
                .ToList();
            // D-021：KPI 行与星期分布同源客户端聚合（零新查询，filter 只用于窗口口径脚注）
            var kpis = BuildTimeKpis(hourly, daily, filter);
            PeakHourCard = kpis.PeakHour;
            PeakDayCard = kpis.PeakDay;
            DailyAverageCard = kpis.DailyAverage;
            ActiveDaysCard = kpis.ActiveDays;
            (WeekdayRows, WeekdayMetaText) = BuildWeekdayDistribution(daily);
            LoadError = null;
        }
        catch (Exception ex)
        {
            if (sequence != _loadSequence)
            {
                return; // 旧请求的失败不覆盖新请求的状态
            }

            LoadError = string.Format(Translations.Instance.LoadErrorFormat, ex.Message);
        }
    }

    /// <summary>入桶会话数（= Σ 各桶 SessionCount；I-029 不变式：入桶 + unknown = 全部会话）。</summary>
    internal static long SumKnownSessions(IReadOnlyList<HourlyUsageBucket> hours) =>
        hours.Sum(h => h.SessionCount);

    /// <summary>
    /// unknown 占比标注文案（internal 供测试对账）：
    /// 「N 个会话缺少精确时间，未计入小时分布（占 X.X%）」，unknown 会话携带 token 时追加
    /// 「，涉及 M tokens 用量」。占比分母 = 入桶 + unknown 会话数（每个会话非入桶即 unknown，
    /// 无第三态）；unknown 为 0 时返回 null（无需标注，页面隐藏）。
    /// </summary>
    internal static string? BuildUnknownNote(long unknownSessions, long knownSessions, long unknownTokens)
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

    /// <summary>
    /// D-021 KPI 行计算（零新查询：I-029 小时分布 + I-028 日序列客户端聚合；internal 供测试对账）：
    /// <list type="bullet">
    /// <item>高峰时段：入桶 token 最大的小时（并列取最早），"HH:00–HH:00" 本地时区（23 点跨午夜写 00）；
    /// 入桶 token 全为 0 → "—"——会话数维度不构成「使用高峰」，不冒充。脚注占入桶 Token 百分比
    /// （分母不含 unknown 会话，与小时分布同口径，诚实标注）。</item>
    /// <item>最活跃日：HasData 日中 token 最大（并列按日序取最早）；全 0 → "—"。脚注当日紧凑 tokens。</item>
    /// <item>日均 tokens：Σ HasData 日 tokens / 活跃日数——补零日不摊薄（口径脚注显式说明）。</item>
    /// <item>活跃天数：HasData 日数；脚注分窗口口径：双端有界 →「窗口共 N 天」，无界 →「自首日起」
    /// （无界无从知窗口天数，不伪造）。</item>
    /// </list>
    /// </summary>
    internal static (TimeKpiCardViewModel PeakHour, TimeKpiCardViewModel PeakDay, TimeKpiCardViewModel DailyAverage, TimeKpiCardViewModel ActiveDays) BuildTimeKpis(
        HourlyDistributionResult hourly,
        IReadOnlyList<DailyUsagePoint> daily,
        UsageQueryFilter filter)
    {
        var t = Translations.Instance;

        var peak = hourly.Hours
            .Where(h => h.TotalTokens > 0)
            .OrderByDescending(h => h.TotalTokens)
            .ThenBy(h => h.Hour)
            .FirstOrDefault();
        TimeKpiCardViewModel peakHour;
        if (peak is null)
        {
            peakHour = TimeKpiCardViewModel.Pending;
        }
        else
        {
            var inBucketTokens = hourly.Hours.Sum(h => h.TotalTokens);
            var share = inBucketTokens > 0 ? (double)peak.TotalTokens / inBucketTokens * 100d : 0d;
            peakHour = new TimeKpiCardViewModel(
                $"{peak.Hour:00}:00–{(peak.Hour + 1) % 24:00}:00",
                string.Format(t.KpiPeakHourFootFormat, share.ToString("F1", CultureInfo.InvariantCulture) + "%"));
        }

        var dataDays = daily.Where(p => p.HasData).ToList();
        var peakDay = dataDays
            .Where(p => p.TotalTokens > 0)
            .OrderByDescending(p => p.TotalTokens)
            .ThenBy(p => p.Day, StringComparer.Ordinal)
            .FirstOrDefault();
        var peakDayCard = peakDay is null
            ? TimeKpiCardViewModel.Pending
            : new TimeKpiCardViewModel(
                peakDay.Day,
                string.Format(t.KpiPeakDayFootFormat, TrendChart.FormatCompactTokens(peakDay.TotalTokens)));

        var activeDays = dataDays.Count;
        var dailyAverage = activeDays == 0
            ? TimeKpiCardViewModel.Pending
            : new TimeKpiCardViewModel(
                TrendChart.FormatCompactTokens((double)dataDays.Sum(p => p.TotalTokens) / activeDays),
                string.Format(t.KpiDailyAverageFootFormat, activeDays.ToString("N0", CultureInfo.InvariantCulture)));

        TimeKpiCardViewModel activeDaysCard;
        if (activeDays == 0)
        {
            activeDaysCard = TimeKpiCardViewModel.Pending;
        }
        else if (filter.FromDay is { } fromDay && filter.ToDay is { } toDay
                 && DateOnly.TryParseExact(fromDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
                 && DateOnly.TryParseExact(toDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
        {
            activeDaysCard = new TimeKpiCardViewModel(
                activeDays.ToString("N0", CultureInfo.InvariantCulture),
                string.Format(t.KpiActiveDaysFootWindowFormat, (to.DayNumber - from.DayNumber + 1).ToString("N0", CultureInfo.InvariantCulture)));
        }
        else
        {
            // 无界窗口：首日按日序最早已知日（yyyy-MM-dd 字典序 = 时间序）
            var firstDay = dataDays.Select(p => p.Day).Min(StringComparer.Ordinal);
            activeDaysCard = new TimeKpiCardViewModel(
                activeDays.ToString("N0", CultureInfo.InvariantCulture),
                string.Format(t.KpiActiveDaysFootSinceFormat, firstDay));
        }

        return (peakHour, peakDayCard, dailyAverage, activeDaysCard);
    }

    /// <summary>
    /// 星期分布聚合（D-021，internal 供测试对账）：HasData=true 的日按本地星期
    /// （yyyy-MM-dd → DateOnly，DayOfWeek 映射周一=0..周日=6）累加 token，固定七行；
    /// bar 值 = 占 Σ token 百分比（0..100，与 DimensionRowViewModel 同口径）。补零日（HasData=false）
    /// 不入桶——口径与活跃日历一致（日序列含无精确时间的会话，与仅含精确时间的小时分布不同，脚注注明）。
    /// Σ token 为 0（无数据 / 全零）→ Rows=null Meta=null（整块隐藏，不画全零行伪信息）；
    /// meta = 工作日（周一~五）/ 周末（周六~日）占比，分别直接计算（不以 100-x 推周末）。
    /// </summary>
    internal static (IReadOnlyList<WeekdayUsageRowViewModel>? Rows, string? Meta) BuildWeekdayDistribution(
        IReadOnlyList<DailyUsagePoint> daily)
    {
        var byDayOfWeek = new long[7];
        foreach (var point in daily)
        {
            if (!point.HasData
                || !DateOnly.TryParseExact(point.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                continue;
            }

            byDayOfWeek[((int)day.DayOfWeek + 6) % 7] += point.TotalTokens;
        }

        var total = byDayOfWeek.Sum();
        if (total <= 0)
        {
            return (null, null);
        }

        var t = Translations.Instance;
        string[] labels = [t.WeekdayMon, t.WeekdayTue, t.WeekdayWed, t.WeekdayThu, t.WeekdayFri, t.WeekdaySat, t.WeekdaySun];
        var rows = new List<WeekdayUsageRowViewModel>(7);
        for (var i = 0; i < 7; i++)
        {
            var share = byDayOfWeek[i] * 100.0 / total;
            rows.Add(new WeekdayUsageRowViewModel(
                labels[i],
                byDayOfWeek[i],
                share,
                $"{byDayOfWeek[i].ToString("N0", CultureInfo.InvariantCulture)} · {share.ToString("F1", CultureInfo.InvariantCulture)}%"));
        }

        var workweekShare = byDayOfWeek.Take(5).Sum() * 100.0 / total;
        var weekendShare = (byDayOfWeek[5] + byDayOfWeek[6]) * 100.0 / total;
        var meta = string.Format(
            t.WeekdayMetaFormat,
            workweekShare.ToString("F1", CultureInfo.InvariantCulture) + "%",
            weekendShare.ToString("F1", CultureInfo.InvariantCulture) + "%");
        return (rows, meta);
    }

    /// <summary>
    /// 建议观察项三行定稿（internal 供测试断言内容与数据能力一致；依据见各行注释）：
    /// <list type="number">
    /// <item>高峰时段——小时分布（I-029）与按日成本真实可得；说明观察方法，不承诺页面没有的下钻。</item>
    /// <item>会话长度——session_facts 有 first_seen_at / last_seen_at（SqliteUsageStore 建表 DDL 核实），
    /// 时长可推导；但 I-029 精度判定已证实部分来源时间戳是采集兜底值（文件 mtime / 纪元标记），时长
    /// 仅供参考——如实标注，不做成假数据列。</item>
    /// <item>数据边界——I-029 unknown 契约的产品立场表述（原型 meta 原文），与页面 unknown 标注互相印证。</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<TimeObservationRowViewModel> CreateObservations()
    {
        var t = Translations.Instance;
        return
        [
            new(t.TimeObsPeakTitle, t.TimeObsPeakDesc),
            new(t.TimeObsSessionTitle, t.TimeObsSessionDesc),
            new(t.TimeObsBoundaryTitle, t.TimeObsBoundaryDesc),
        ];
    }

    public void Dispose() => _filters.PropertyChanged -= OnFilterPropertyChanged;
}
