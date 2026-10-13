using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CCUsage.App.Controls;
using CCUsage.App.Services;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.App.ViewModels;

/// <summary>
/// D-009 Agent KPI 卡（Top4，原型 #agentKpis 同款：icon-chip + Agent 名 + 紧凑 token 主值 +
/// "N 会话 · 成本"脚注）。不可变小对象，由 <see cref="AgentsViewModel"/> 整体替换触发绑定刷新。
/// 成本脚注走 <see cref="OverviewViewModel.FormatCost"/> 四态（未知成本绝不折 0，显示「未知」）；
/// 无任何成本数据显示 "—"（未知纪律同 I-028）。
/// </summary>
public sealed class AgentKpiCardViewModel
{
    public AgentKpiCardViewModel(DimensionUsageRow row)
    {
        Name = row.Key;
        Glyph = AgentVisualMap.Glyph(row.Key).ToString();
        ChipVariant = AgentVisualMap.ChipVariant(row.Key);
        TokensText = TrendChart.FormatCompactTokens(row.TotalTokens);
        FootText = $"{row.SessionCount.ToString("N0", CultureInfo.InvariantCulture)} 会话 · {OverviewViewModel.FormatCost(row.Cost)}";
    }

    /// <summary>Agent 名（维度 Key 即展示名）。</summary>
    public string Name { get; }

    /// <summary>软色底图标字形（AgentVisualMap 稳定散列，同会话页/右栏）。</summary>
    public string Glyph { get; }

    /// <summary>软色底变体名（经 AgentChip.Variant 附加属性 → chip-* 伪类 DynamicResource 着色）。</summary>
    public string ChipVariant { get; }

    /// <summary>主数值：总 Token 紧凑格式（同总览 KPI 卡口径）。</summary>
    public string TokensText { get; }

    /// <summary>脚注："N 会话 · 估算成本"（成本四态文案）。</summary>
    public string FootText { get; }
}

/// <summary>
/// D-009 Agent 排行表单行（原型 .table 七列：Agent + 会话数 / 输入 / 输出 / 总 Token / 估算成本 / 占比）。
/// 不可变小对象；系列圆点 = 行序映射 AppSeries{N}Brush（普通资源不随明暗翻转，无 Application 环境
/// （纯单测）为 null，视图侧圆点透明，不影响数据可读性——同 OverviewViewModel.DimensionRowViewModel）。
/// token 列用紧凑格式（FormatCompactTokens，同 KPI 卡/环心口径）：token 量级无上界，七列定宽布局下
/// 千分位全数字会被裁切成错值（真实数据 1,261,301,001 实测裁尾）——宁紧凑不裁切，精确值可下钻会话页。
/// </summary>
public sealed class AgentRankRowViewModel
{
    public AgentRankRowViewModel(DimensionUsageRow row, int rank)
    {
        Name = row.Key;
        SessionCountText = row.SessionCount.ToString("N0", CultureInfo.InvariantCulture);
        InputTokensText = TrendChart.FormatCompactTokens(row.InputTokens);
        OutputTokensText = TrendChart.FormatCompactTokens(row.OutputTokens);
        TotalTokensText = TrendChart.FormatCompactTokens(row.TotalTokens);
        CostText = OverviewViewModel.FormatCost(row.Cost);
        ShareText = OverviewViewModel.FormatShare(row.TokenShare);
        DotBrush = ResolveSeriesBrush(rank);
    }

    public string Name { get; }

    public string SessionCountText { get; }

    public string InputTokensText { get; }

    public string OutputTokensText { get; }

    public string TotalTokensText { get; }

    /// <summary>估算成本四态文案（未知成本不折 0：未知 / $x.xx+ / $x.xx / —）。</summary>
    public string CostText { get; }

    /// <summary>占比文案（0..1 → "36.8%"；无基数 → "—"）。</summary>
    public string ShareText { get; }

    /// <summary>行首圆点画刷（行序映射系列色；可空）。</summary>
    public IBrush? DotBrush { get; }

    private static IBrush? ResolveSeriesBrush(int index) =>
        Application.Current?.TryGetResource($"AppSeries{index % 7}Brush", ThemeVariant.Default, out var value) == true
            && value is IBrush brush ? brush : null;
}

/// <summary>
/// D-009 Agent 统计页 VM（原型 #page-agents）：Top4 Agent KPI 卡（token / 会话数 / 成本）+
/// Agent 排行表（会话数 / 输入 / 输出 / 总 Token / 估算成本 / 占比，I-029 Agent 维度聚合）。
/// 数据随全局筛选（<see cref="FilterStateService"/>：时间范围 + 单选 Agent）联动重查，
/// 参数映射走 <see cref="GlobalFilterMapper"/>（D-008 审定）。排行表展示维度全部行
/// （含零 token 的会话型 Agent——facts 存在即真实来源，诚实不筛；服务已按 token 降序，
/// 此处防御性重排，不依赖服务排序契约）。
/// <b>行点击跳转（D-009/D-011 决策点 2 定案：全局途径）</b>：写 FilterStateService.Agent（顶部栏
/// 单选 Agent 是 Agent 维度的跨页筛选宿主：可见、可经「所有 Agent」清除、会话页已消费该状态），
/// 再经构造注入的回调导航会话页；不采用右栏复选组途径——复选集是"全勾=不过滤"的集合语义，
/// 单 Agent 过滤要反勾其余项，且与全局单选取交集可能得诚实空页（两条途径并存的冲突恰好说明
/// Agent 维度的写入口应唯一落在全局单选）。
/// 生命周期 / 线程 / 序号防护纪律同 <see cref="OverviewViewModel"/>：每次导航新建实例、
/// 外壳 Dispose 退订、FilterStateService 通知已在 UI 线程。
/// </summary>
public partial class AgentsViewModel : ViewModelBase, IDisposable
{
    /// <summary>KPI 卡张数（原型 agentKpis 为 Top4）。</summary>
    public const int KpiTopN = 4;

    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _query;

    /// <summary>行点击跳转会话页的外壳回调（Agent 名为参）；null（测试/预览）时 no-op。</summary>
    private readonly Action<string>? _openSessionsForAgent;

    /// <summary>加载序号：重置型加载自增；跨 await 序号不一致的响应/异常直接丢弃（同 OverviewViewModel）。</summary>
    private int _loadSequence;

    public PageDescriptor Page { get; }

    // ── Top4 KPI 卡（固定 4 槽位对齐原型 .kpis 四列网格；不足 4 个 Agent 的槽位为 null → 视图隐藏）──
    [ObservableProperty]
    private AgentKpiCardViewModel? _kpi1;

    [ObservableProperty]
    private AgentKpiCardViewModel? _kpi2;

    [ObservableProperty]
    private AgentKpiCardViewModel? _kpi3;

    [ObservableProperty]
    private AgentKpiCardViewModel? _kpi4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows), nameof(ShowEmptyState))]
    private IReadOnlyList<AgentRankRowViewModel> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError), nameof(ShowEmptyState))]
    private string? _loadError;

    /// <summary>排行表行点击（ListBox SelectedItem）：跳会话页带该 Agent 全局筛选，选中态立即复位。</summary>
    [ObservableProperty]
    private AgentRankRowViewModel? _selectedAgent;

    public bool HasRows => Rows.Count > 0;

    public bool HasLoadError => LoadError is not null;

    /// <summary>空态可见性（失败时让位错误面板，同会话页 P2-2 纪律）。</summary>
    public bool ShowEmptyState => !HasRows && !HasLoadError;

    public AgentsViewModel(
        PageDescriptor page,
        FilterStateService filters,
        IUsageQueryService query,
        Action<string>? openSessionsForAgent = null)
    {
        Page = page;
        _filters = filters;
        _query = query;
        _openSessionsForAgent = openSessionsForAgent;
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

    /// <summary>按当前全局筛选重查 Agent 维度（序号防护见类型说明）。</summary>
    public async Task LoadAsync()
    {
        var sequence = ++_loadSequence;
        var filter = GlobalFilterMapper.Build(_filters.TimeRange, _filters.Agent);

        try
        {
            var agents = await _query.GetAgentBreakdownAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            ApplyRows(agents);
            LoadError = null;
        }
        catch (Exception ex)
        {
            if (sequence != _loadSequence)
            {
                return; // 旧请求的失败不覆盖新请求的状态
            }

            LoadError = $"加载失败：{ex.Message}";
        }
    }

    private void ApplyRows(IReadOnlyList<DimensionUsageRow> rows)
    {
        var ordered = rows
            .OrderByDescending(row => row.TotalTokens)
            .ThenBy(row => row.Key, StringComparer.Ordinal)
            .ToList();
        Rows = ordered.Select((row, index) => new AgentRankRowViewModel(row, index)).ToList();

        // Top4 KPI：token 降序前 4（不足补 null 槽位隐藏；零 token Agent 排序自然靠后）
        Kpi1 = ordered.Count > 0 ? new AgentKpiCardViewModel(ordered[0]) : null;
        Kpi2 = ordered.Count > 1 ? new AgentKpiCardViewModel(ordered[1]) : null;
        Kpi3 = ordered.Count > 2 ? new AgentKpiCardViewModel(ordered[2]) : null;
        Kpi4 = ordered.Count > 3 ? new AgentKpiCardViewModel(ordered[3]) : null;
    }

    partial void OnSelectedAgentChanged(AgentRankRowViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        _selectedAgent = null; // 直接回写字段避免 setter 递归；下次点击同一行仍触发
        OnPropertyChanged(nameof(SelectedAgent));
        _openSessionsForAgent?.Invoke(value.Name);
    }

    public void Dispose() => _filters.PropertyChanged -= OnFilterPropertyChanged;
}
