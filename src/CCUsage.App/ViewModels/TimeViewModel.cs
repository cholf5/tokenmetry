using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CCUsage.App.Controls;
using CCUsage.App.Localization;
using CCUsage.App.Services;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.App.ViewModels;

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
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(FilterStateService.TimeRange)
            || e.PropertyName == nameof(FilterStateService.Agent))
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
        var filter = GlobalFilterMapper.Build(_filters.TimeRange, _filters.Agent);

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
