using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CCUsage.App.Controls;
using CCUsage.App.Services;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.App.ViewModels;

/// <summary>
/// 单张 KPI 卡的展示模型（D-006）：主数值 + 环比箭头文案。不可变小对象，
/// 由 <see cref="OverviewViewModel"/> 整体替换实例触发绑定刷新。
/// 箭头颜色不落在数据上：视图经 <c>Classes.kpi-change-up/down/flat</c> 类绑定切换
/// AppAccentGreenBrush（↑ 绿）/ AppAccentRedBrush（↓ 红）/ AppMutedTextBrush（→ 平），
/// 明暗主题由 DynamicResource 兜底（对齐原型 .kpi-foot 的 .up/.down 色语义）。
/// </summary>
public sealed class KpiCardViewModel
{
    public KpiCardViewModel(string value, MetricChange change)
    {
        Value = value;
        var arrow = change.Direction switch
        {
            ChangeDirection.Increase => "↑",
            ChangeDirection.Decrease => "↓",
            _ => "→",
        };
        // 百分比不可定义（上一周期无数据 / 为 0 / 当前成本全未知）→ "—"，不伪造数字；
        // 有一位小数即可读（原型 "↑ 23.6%" 同款）。方向由箭头表达，数值取绝对值避免 "↓ -1.1%" 双重负号。
        var percent = change.PercentChange is { } p
            ? Math.Abs(p).ToString("F1", CultureInfo.InvariantCulture) + "%"
            : "—";
        ChangeText = $"{arrow} {percent}";
        IsUp = change.Direction == ChangeDirection.Increase;
        IsDown = change.Direction == ChangeDirection.Decrease;
        IsFlat = change.Direction == ChangeDirection.Neutral;
    }

    /// <summary>加载完成前的占位（诚实：未加载不冒充 0）。</summary>
    public static KpiCardViewModel Pending { get; } = new("—", MetricChange.Neutral);

    /// <summary>主数值（总 Token 紧凑格式 / 成本四态文案 / 会话数千分位 / 占比百分数）。</summary>
    public string Value { get; }

    /// <summary>环比文案（含箭头前缀，如 "↑ 23.6%"、"→ —"）。</summary>
    public string ChangeText { get; }

    public bool IsUp { get; }

    public bool IsDown { get; }

    public bool IsFlat { get; }
}

/// <summary>
/// D-006 总览页（上半）VM：KPI 四卡（总 Token / 估算成本 / 会话数 / 缓存读取占比，含环比）+
/// Token 使用趋势（D-004 TrendChart DailyTokens）+ Agent 使用占比环形（D-005 DonutChart）。
/// 数据来自 <see cref="IUsageQueryService"/>（I-028 KPI/日序列 + I-029 Agent 维度），
/// 参数由全局筛选（<see cref="FilterStateService"/>）映射——TimeRange → 本地自然日窗口
/// （7/30/90 天含端点；All 双端无界），Agent.Id 直传查询服务（"all" → 不过滤）。
/// 生命周期：每次导航新建实例，旧实例由外壳 Dispose 退订常驻 FilterStateService
/// （与 PagePlaceholderViewModel 同一约定）；异步加载用序号防护——筛选快速切换时
/// 旧响应在序号比对处丢弃，绝不覆盖新选择（同 ChartPreviewViewModel 的做法）。
/// 线程约定：实例方法（含 LoadAsync 与筛选订阅回调）按 UI 线程亲和设计——
/// 序号自增与比对无锁，依赖 FilterStateService 已把变更通知 marshal 到 UI 线程。
/// </summary>
public partial class OverviewViewModel : ViewModelBase, IDisposable
{
    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _query;

    /// <summary>加载序号：每次触发自增；跨 await 回来时序号不一致的响应直接丢弃。</summary>
    private int _loadSequence;

    public PageDescriptor Page { get; }

    // ── KPI 四卡（原型 .kpis 顺序：总 Token / 估算成本 / 会话数 / 缓存读取占比）──
    [ObservableProperty]
    private KpiCardViewModel _tokensCard = KpiCardViewModel.Pending;

    [ObservableProperty]
    private KpiCardViewModel _costCard = KpiCardViewModel.Pending;

    [ObservableProperty]
    private KpiCardViewModel _sessionsCard = KpiCardViewModel.Pending;

    [ObservableProperty]
    private KpiCardViewModel _cacheCard = KpiCardViewModel.Pending;

    /// <summary>日序列（I-028 双端有界补零窗口；TrendChart DailyTokens 直接消费）。</summary>
    [ObservableProperty]
    private IReadOnlyList<DailyUsagePoint>? _dailyPoints;

    /// <summary>Agent 环形段（token 量 Top6 + 「其他」聚合，段序映射 AppSeries0..6）。</summary>
    [ObservableProperty]
    private IReadOnlyList<DonutSegment>? _agentSegments;

    /// <summary>环中心主文字（= KPI 总 Token 的紧凑格式，对齐原型 donut 中心 "8.74M"）。</summary>
    [ObservableProperty]
    private string _agentDonutCenterTitle = string.Empty;

    /// <summary>加载失败状态（诚实错误行，不静默吞异常；筛选再变化会自动重试）。</summary>
    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private bool _hasLoadError;

    public OverviewViewModel(PageDescriptor page, FilterStateService filters, IUsageQueryService query)
    {
        Page = page;
        _filters = filters;
        _query = query;
        // FilterStateService 保证通知在 UI 线程发出，这里直接触发重查即可。
        filters.PropertyChanged += OnFilterPropertyChanged;
        _ = LoadAsync();
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只关心两个筛选维度；Display 是派生串，忽略避免一次变更触发两次重查
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(FilterStateService.TimeRange)
            || e.PropertyName == nameof(FilterStateService.Agent))
        {
            _ = LoadAsync();
        }
    }

    /// <summary>
    /// 全局筛选 → 查询参数：时间窗口按本地自然日（含端点，今天收口），
    /// Agent.Id 直传（<see cref="AgentOption.AllId"/> → null 不过滤，查询服务语义为空白不过滤）。
    /// internal 供测试断言映射。
    /// </summary>
    internal UsageQueryFilter BuildQueryFilter(TimeRangeOption range, AgentOption agent)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly? fromDay = range.Value switch
        {
            TimeRange.Last7Days => today.AddDays(-6),
            TimeRange.Last30Days => today.AddDays(-29),
            TimeRange.Last90Days => today.AddDays(-89),
            TimeRange.All => null,
            _ => null,
        };
        return new UsageQueryFilter
        {
            FromDay = fromDay is { } from ? UsageQueryFilter.FormatDay(from) : null,
            ToDay = fromDay is null ? null : UsageQueryFilter.FormatDay(today),
            Agent = agent.Id == AgentOption.AllId ? null : agent.Id,
        };
    }

    /// <summary>按当前全局筛选重查 KPI + 日序列 + Agent 维度（序号防护见类型说明）。</summary>
    public async Task LoadAsync()
    {
        var sequence = ++_loadSequence;
        var filter = BuildQueryFilter(_filters.TimeRange, _filters.Agent);

        try
        {
            var kpi = await _query.GetKpiAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            var daily = await _query.GetDailySeriesAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            var agents = await _query.GetAgentBreakdownAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            ApplyKpi(kpi);
            DailyPoints = daily;
            AgentSegments = CreateAgentSegments(agents);
            AgentDonutCenterTitle = TrendChart.FormatCompactTokens(kpi.TotalTokens);
            HasLoadError = false;
            LoadError = null;
        }
        catch (Exception ex)
        {
            if (sequence != _loadSequence)
            {
                return; // 旧请求的失败不覆盖新请求的状态
            }

            HasLoadError = true;
            LoadError = $"加载失败：{ex.Message}";
        }
    }

    // ── 展示映射（internal 供测试对账：stub KPI 值 → 卡片显示值，TC-001 页面层分工）──

    private void ApplyKpi(UsageKpiResult kpi)
    {
        // PreviousPeriod 为 null（如"全部时间"无界窗口）→ 平箭头 + "—"，不臆造对比
        TokensCard = new KpiCardViewModel(
            TrendChart.FormatCompactTokens(kpi.TotalTokens),
            kpi.PreviousPeriod?.TotalTokensChange ?? MetricChange.Neutral);
        CostCard = new KpiCardViewModel(
            FormatCost(kpi.Cost),
            kpi.PreviousPeriod?.CostChange ?? MetricChange.Neutral);
        SessionsCard = new KpiCardViewModel(
            kpi.SessionCount.ToString("N0", CultureInfo.InvariantCulture),
            kpi.PreviousPeriod?.SessionCountChange ?? MetricChange.Neutral);
        CacheCard = new KpiCardViewModel(
            FormatShare(kpi.CacheReadShare),
            kpi.PreviousPeriod?.CacheReadShareChange ?? MetricChange.Neutral);
    }

    /// <summary>
    /// 成本 KPI 四态文案（未知成本纪律：未知绝不显示 $0.00）：
    /// (x,false) "$x.xx"；(x,true) "$x.xx+"（已知成本下限）；(null,true) "未知"；(null,false) "—"（无成本数据）。
    /// </summary>
    internal static string FormatCost(AggregatedCost cost) => cost.KnownCostUsd is { } known
        ? TrendChart.FormatCostUsd(known, cost.HasUnknownCost) ?? "未知"
        : cost.HasUnknownCost ? "未知" : "—";

    /// <summary>占比文案：0..1 → "67.3%"（对齐原型一位小数）；无基数（总 token 为 0）→ "—"。</summary>
    internal static string FormatShare(decimal? share) => share is { } value
        ? (value * 100m).ToString("F1", CultureInfo.InvariantCulture) + "%"
        : "—";

    /// <summary>
    /// Agent 维度 → 环形段：token 量 Top6 + 「其他」聚合（超 7 系列色的原型同款处理，
    /// 同 ChartPreviewViewModel）；零 token 行不入环（占比无意义）。
    /// </summary>
    internal static IReadOnlyList<DonutSegment> CreateAgentSegments(IReadOnlyList<DimensionUsageRow> agents)
    {
        var withTokens = agents
            .Where(a => a.TotalTokens > 0)
            .OrderByDescending(a => a.TotalTokens)
            .ToList();
        var segments = withTokens
            .Take(6)
            .Select(a => new DonutSegment(a.Key, a.TotalTokens))
            .ToList();
        var rest = withTokens.Skip(6).ToList();
        if (rest.Count > 0)
        {
            segments.Add(new DonutSegment("其他", rest.Sum(a => a.TotalTokens)));
        }

        return segments;
    }

    public void Dispose() => _filters.PropertyChanged -= OnFilterPropertyChanged;
}
