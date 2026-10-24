using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.Tray;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.ViewModels;

/// <summary>弹层三态机（D-015）：Idle（实时，无 banner）/ Active（工作中 banner）/ Warn（异常 banner）。
/// Warn 优先于 Active——异常是当前最该看的信息；下一轮无异常的刷新把 Warn 清回 Idle/Active。</summary>
public enum StatusPopoverState
{
    Idle,
    Active,
    Warn,
}

/// <summary>
/// 一轮刷新的速率取样（D-015 修正项 4）：全局与分 Agent 的 tokens/min。
/// 口径 = 相邻两轮今日 tokens 差值 ÷ 轮间隔；差值 ≤0（无增量 / 跨日回落重定基线）或间隔超过
/// <see cref="RefreshRateTracker.MaxInterval"/>（>1 分钟按卡定降级，90s 容纳调度抖动）时无速率。
/// <see cref="LastIncrementAt"/> = 最近一次有增量的轮次时刻（「最近活动 hh:mm」降级文案的数据源）。
/// </summary>
/// <param name="TokensPerMinute">全局速率（tokens/min）；无速率为 null（不伪造 0）。</param>
/// <param name="AgentRates">分 Agent 速率（tokens/min）；仅本轮有增量的 Agent 出现。</param>
/// <param name="LastIncrementAt">最近一次增量轮次时刻；尚无增量为 null。</param>
public sealed record RefreshRateSample(
    double? TokensPerMinute,
    IReadOnlyDictionary<string, double> AgentRates,
    DateTimeOffset? LastIncrementAt)
{
    public static RefreshRateSample Empty { get; } = new(null, new Dictionary<string, double>(), null);
}

/// <summary>
/// 全局速率跟踪器（D-015 修正项 4，挂 DashboardViewModel 的 TodayTokens 每轮回写）：
/// 首轮只定基线不显速率（卡定「首轮无上一轮基线不显速率」）。分 Agent 的差值不在此处——
/// DashboardViewModel 只有总量回写，Agent 快照对由弹层 VM 自己的相邻两次查询结果配对
/// （<see cref="StatusPopoverViewModel.ComputeAgentRates"/>，同一差值÷间隔公式）。
/// 纯内存状态机，headless 可直测。
/// </summary>
internal sealed class RefreshRateTracker
{
    /// <summary>速率有效的前提是相邻轮间隔不超过它：刷新默认 1 分钟（卡定「>1 分钟降级」），
    /// 90s 容纳定时器/扫描的调度抖动；用户把刷新间隔调大到 2 分钟起即永不显速率（按卡降级）。</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(90);

    /// <summary>速率有效的下限间隔：两个连续的定时器轮次至少相隔一个刷新间隔（默认 1 分钟）；
    /// 更近的相邻回写不是"两轮"——典型如启动时缓存热载 → 首轮扫描完成（间隔几秒，增量是离线
    /// 期间积累的全部用量，除以几秒会产出荒谬速率）。下限取 45s = 间隔一半，吸收定时器相位抖动。</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(45);

    private long _prevTotal = -1;
    private DateTimeOffset _prevAt;
    private DateTimeOffset? _lastIncrementAt;

    /// <summary>喂入一轮今日 tokens 回写，返回本轮取样（全局速率 + 最近活动时刻）。
    /// 首次喂入只定基线（卡定「首轮无上一轮基线不显速率」）。</summary>
    public RefreshRateSample Feed(long totalTokens, DateTimeOffset at)
    {
        double? rate = null;
        if (_prevTotal >= 0)
        {
            var delta = totalTokens - _prevTotal;
            var interval = at - _prevAt;
            if (delta > 0 && interval >= MinInterval && interval <= MaxInterval)
            {
                rate = delta / interval.TotalMinutes;
                _lastIncrementAt = at;
            }
        }

        // 负增量（跨日回落 / 重扫口径变化）同样只重定基线，不产出速率
        _prevTotal = totalTokens;
        _prevAt = at;
        return new RefreshRateSample(rate, new Dictionary<string, double>(), _lastIncrementAt);
    }
}

/// <summary>
/// 弹层「主要 Agent」一行的展示模型（D-015）：品牌 logo 槽（I-034 agent-chip 同机制）、
/// token 数（千分位，对齐总览维度行）、占比与 mini bar 值（口径同 DimensionRowViewModel：
/// TokenShare × 100），副题 = 有增量速率时「当前 X/min」，否则该 Agent 今日最近会话的
/// 「最近活动 hh:mm」（最近会话查询派生，仅当日显示时间；再无则空——不伪造活动时刻）。
/// 不可变小对象，由 <see cref="StatusPopoverViewModel"/> 整体替换集合触发绑定刷新。
/// </summary>
public sealed class StatusPopoverAgentRowViewModel
{
    public StatusPopoverAgentRowViewModel(
        string agentId,
        string name,
        long tokens,
        decimal? tokenShare,
        string subtitle,
        Func<string, (string Vendor, string Glyph)> visualResolver)
    {
        AgentId = agentId;
        Name = name;
        TokensText = tokens.ToString("N0", CultureInfo.InvariantCulture);
        ShareText = OverviewViewModel.FormatShare(tokenShare);
        BarValue = tokenShare is { } share ? Math.Clamp((double)(share * 100m), 0d, 100d) : 0d;
        Subtitle = subtitle;
        var visual = visualResolver(name);
        LogoVendor = visual.Vendor;
        Glyph = visual.Glyph;
    }

    /// <summary>Agent 键（采集来源名，Agent 行点击带它跳会话页——D-009 既有路由）。</summary>
    public string AgentId { get; }

    public string Name { get; }

    /// <summary>品牌伪类名（AgentChip.Logo 附加属性消费；未识别落 generic 散列字形兜底）。</summary>
    public string LogoVendor { get; }

    public string Glyph { get; }

    /// <summary>token 数千分位文案（原型 .ar 同款，与总览维度行同口径）。</summary>
    public string TokensText { get; }

    /// <summary>占比文案（无基数为 "—"）。</summary>
    public string ShareText { get; }

    /// <summary>mini bar 值（0..100）。</summary>
    public double BarValue { get; }

    /// <summary>副题（速率 / 最近活动；可空串 = 两种降级都不可得，行内省略不占位）。</summary>
    public string Subtitle { get; }
}

/// <summary>
/// 弹层「模型用量」一行的展示模型（D-019）：模型名 + 今日成本金额（OverviewViewModel.FormatCost
/// 四态诚实文案）+ 成本占比（CostShare，本行含未知成本时为 null → "—"/空轨道，不伪造），
/// mini bar 值 = CostShare × 100。模型图标走 ModelVisualMap（model-chip 同机制，未识别落
/// generic 字形兜底）。不可变小对象，由 <see cref="StatusPopoverViewModel"/> 整体替换集合触发绑定刷新。
/// </summary>
public sealed class StatusPopoverModelRowViewModel
{
    public StatusPopoverModelRowViewModel(
        string name,
        AggregatedCost cost,
        decimal? costShare,
        Func<string, (string Vendor, char Glyph)> visualResolver)
    {
        Name = name;
        CostText = OverviewViewModel.FormatCost(cost);
        ShareText = OverviewViewModel.FormatShare(costShare);
        BarValue = costShare is { } share ? Math.Clamp((double)(share * 100m), 0d, 100d) : 0d;
        var visual = visualResolver(name);
        LogoVendor = visual.Vendor;
        Glyph = visual.Glyph.ToString();
    }

    public string Name { get; }

    /// <summary>厂商伪类名（ModelChip.Vendor 附加属性消费；未识别落 generic 字形兜底）。</summary>
    public string LogoVendor { get; }

    public string Glyph { get; }

    /// <summary>今日成本金额文案（I-028 四态：未知成本不冒充 0）。</summary>
    public string CostText { get; }

    /// <summary>成本占比文案（本行含未知成本时 "—"）。</summary>
    public string ShareText { get; }

    /// <summary>mini bar 值（0..100）。</summary>
    public double BarValue { get; }
}

/// <summary>
/// D-015 状态栏快捷信息窗的内容层 VM：信息架构按原型锁定（头部三态灯 + 三格 KPI + Token 构成条
/// + 主要 Agent Top4 + footer 快捷入口），数据全走既有管线——KPI/构成/Agent 用
/// <see cref="IUsageQueryService"/> 今日窗口（与总览页同源同口径），速率用修正项 4 的差值口径
/// （全局速率挂 <see cref="DashboardViewModel.TodayTokens"/> 每轮回写，分 Agent 用相邻两次
/// Agent 查询快照配对），异常 Warn 接 <see cref="DashboardViewModel.AnomalyDetected"/> 同源信号。
/// <para>线程纪律同 MainWindowViewModel：DashboardViewModel 的事件在线程池线程 raise，
/// 全部状态回写经 <see cref="RunOnUIThread"/> marshal 到 UI 线程；查询按序号防护（同
/// OverviewViewModel——弹层反复开关/轮次并发时旧响应在序号比对处丢弃）。</para>
/// <para>生命周期：壳层（D-016）创建并持有到进程结束；查询只发生在可见期（OnShown/OnHidden
/// 由壳层调用），速率/状态机不受可见性影响（轮次信号始终喂入，重新打开时状态即时正确）。
/// 导航回调由壳层注入（Action 单向，同 D-007 选型——VM 不反向引用外壳与弹窗）；null（测试/预览）
/// 时点击 no-op。Dispose 退订 DashboardViewModel（测试卫生；壳层持有场景随进程结束不退订）。</para>
/// </summary>
public partial class StatusPopoverViewModel : ViewModelBase, IDisposable
{
    /// <summary>Top4（卡定「主要 Agent Top4」，原型同款；总览环形是 Top6，弹层压缩视图取 4）。</summary>
    public const int TopAgentsCount = 4;

    /// <summary>模型用量 Top3（D-019：弹层压缩视图取 3，总览模型页是全表；按已知成本降序）。</summary>
    public const int TopModelsCount = 3;

    /// <summary>最近会话查询页大小（派生各 Agent 最近活动时刻用；40 行足够覆盖 Top4 Agent 的当日活动）。</summary>
    private const int LastActivityProbeSize = 40;

    private readonly IUsageQueryService? _query;
    private readonly DashboardViewModel? _dashboard;
    private readonly DateRangeService? _dateService;
    private readonly Action<PageId>? _navigateToPage;
    private readonly Action<string>? _openAgentSessions;
    private readonly Action? _openSettings;
    private readonly RefreshRateTracker _rateTracker = new();

    /// <summary>加载序号：每次触发自增；跨 await 回来时序号不一致的响应直接丢弃（同 OverviewViewModel）。</summary>
    private int _loadSequence;

    private bool _visible;

    /// <summary>最近一次异常（Warn banner 内容；下一次无异常的轮次清空）。</summary>
    private DashboardViewModel.AnomalyRaised? _pendingWarn;

    /// <summary>最近一次全局速率取样（轮次信号持续喂入，重新打开弹层时状态即时正确）。</summary>
    private RefreshRateSample _lastSample = RefreshRateSample.Empty;

    /// <summary>Agent 快照对（相邻两次查询的 tokens + 时刻），分 Agent 差值速率的基线。</summary>
    private Dictionary<string, long> _lastAgentTokens = new();
    private DateTimeOffset _lastAgentSnapshotAt = DateTimeOffset.MinValue;

    /// <summary>最近一次 Apply 算出的分 Agent 速率（Active banner 的增量归属用；与 Agent 行同源）。</summary>
    private IReadOnlyDictionary<string, double> _lastAgentRates = new Dictionary<string, double>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveText), nameof(StateClassActive), nameof(StateClassWarn))]
    private StatusPopoverState _state = StatusPopoverState.Idle;

    /// <summary>状态灯伪类绑定（视图 Ellipse.popover-live-dot 的 active/warn 类切换）。</summary>
    public bool StateClassActive => State == StatusPopoverState.Active;
    public bool StateClassWarn => State == StatusPopoverState.Warn;

    /// <summary>本轮扫描进行中（头部状态灯临时切「扫描中…」，与三态正交——数据不因扫描消失）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveText))]
    private bool _isScanning;

    // ── 三态 banner ──
    [ObservableProperty] private bool _isBannerVisible;
    [ObservableProperty] private bool _isBannerWarn;
    [ObservableProperty] private string _bannerTitle = string.Empty;
    [ObservableProperty] private string _bannerSubtitle = string.Empty;

    // ── KPI 三格（原型 .stats；标签/脚注走 Translations 直绑）──
    [ObservableProperty] private string _todayTokensText = "—";
    [ObservableProperty] private string _todayCostText = "—";
    [ObservableProperty] private string _cacheShareText = "—";

    // ── 构成条（三段占比权重 → ProportionalColumns 翻译成列定义 + 三张 metric 卡；口径同总览 Token 类型构成）──
    [ObservableProperty] private IReadOnlyList<double> _segmentShares = ZeroShares;
    [ObservableProperty] private string _inputTokensText = "—";
    [ObservableProperty] private string _inputShareText = "—";
    [ObservableProperty] private string _outputTokensText = "—";
    [ObservableProperty] private string _outputShareText = "—";
    [ObservableProperty] private string _cacheTokensText = "—";
    [ObservableProperty] private string _cacheSegShareText = "—";

    /// <summary>全零段宽（无数据轨道裸露，不伪造段）。</summary>
    private static readonly double[] ZeroShares = [0, 0, 0];

    // ── 主要 Agent Top4 ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopAgents))]
    private IReadOnlyList<StatusPopoverAgentRowViewModel>? _topAgents;

    /// <summary>Agent 区有无数据（驱动「暂无数据」诚实空态行）。</summary>
    public bool HasTopAgents => TopAgents is { Count: > 0 };

    // ── 模型用量 Top3（D-019：按今日已知成本降序；无数据整块隐藏，不重复空态行）──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopModels))]
    private IReadOnlyList<StatusPopoverModelRowViewModel>? _topModels;

    /// <summary>模型区有无数据（驱动区块整体显隐）。</summary>
    public bool HasTopModels => TopModels is { Count: > 0 };

    /// <summary>头部状态灯文案（按态实时取串；语言切换经 Translations 绑定信号重算）。</summary>
    public string LiveText => State switch
    {
        StatusPopoverState.Warn => Translations.Instance.PopoverLiveWarn,
        StatusPopoverState.Active => Translations.Instance.PopoverLiveActive,
        _ => IsScanning ? Translations.Instance.PopoverScanning : Translations.Instance.PopoverLiveIdle,
    };

    public StatusPopoverViewModel(
        IUsageQueryService? query,
        DashboardViewModel? dashboard,
        DateRangeService? dateService,
        Action<PageId>? navigateToPage = null,
        Action<string>? openAgentSessions = null,
        Action? openSettings = null)
    {
        _query = query;
        _dashboard = dashboard;
        _dateService = dateService;
        _navigateToPage = navigateToPage;
        _openAgentSessions = openAgentSessions;
        _openSettings = openSettings;

        if (_dashboard != null)
        {
            _dashboard.PropertyChanged += OnDashboardPropertyChanged;
            _dashboard.AnomalyDetected += OnAnomalyDetected;
            // 基线不在此处预喂：构造后的第一次回写（含缓存热载）按"首轮"只定基线——
            // 卡定「首轮无上一轮基线不显速率」。IsScanning 同步当前扫描态。
            IsScanning = _dashboard.IsLoading;
        }
    }

    public void Dispose()
    {
        if (_dashboard != null)
        {
            _dashboard.PropertyChanged -= OnDashboardPropertyChanged;
            _dashboard.AnomalyDetected -= OnAnomalyDetected;
        }
    }

    // ── 壳层生命周期（D-016）：可见才查询，速率/状态机不受影响 ──

    public void OnShown()
    {
        _visible = true;
        _ = ReloadAsync();
    }

    public void OnHidden() => _visible = false;

    /// <summary>可见期标记（headless 测试「可见才查询」约定用）。</summary>
    internal bool VisibleForTest => _visible;

    // ── 点击行为（修正项 6：全部收敛既有导航，回调由壳层注入；null 时 no-op）──

    /// <summary>「打开完整统计」→ 总览页（主窗唤回由壳层的回调负责）。</summary>
    [RelayCommand]
    private void OpenStats() => _navigateToPage?.Invoke(PageId.Overview);

    /// <summary>footer「最近会话 →」→ 会话页（修正项 5：替换原型的「当前 Session」重复信息位）。</summary>
    [RelayCommand]
    private void OpenRecentSessions() => _navigateToPage?.Invoke(PageId.Sessions);

    /// <summary>头部 ⚙ → 设置页。</summary>
    [RelayCommand]
    private void OpenSettings() => _openSettings?.Invoke();

    /// <summary>「查看详情 →」→ Agent 统计页。</summary>
    [RelayCommand]
    private void OpenAgentDetails() => _navigateToPage?.Invoke(PageId.Agents);

    /// <summary>模型行点击 → 模型统计页（D-019：会话页无按模型筛选的既有路由，收敛到模型页）。</summary>
    [RelayCommand]
    private void OpenModels() => _navigateToPage?.Invoke(PageId.Models);

    /// <summary>Agent 行点击 → 会话页带该 Agent 筛选（D-009 既有路由，经壳层回调）。</summary>
    [RelayCommand]
    private void OpenAgentRow(string? agentId)
    {
        if (!string.IsNullOrEmpty(agentId))
        {
            _openAgentSessions?.Invoke(agentId);
        }
    }

    // ── 数据装载 ──

    /// <summary>按今日窗口重查 KPI / 构成 / Agent / 最近会话（序号防护；无查询服务（纯预览）时 no-op）。
    /// 失败保留旧值不静默改写为空——弹层是状态展示面，下一轮刷新会自动重试（stderr 最小诊断，同
    /// LoadAgentOptionsAsync 约定）。</summary>
    public Task ReloadAsync() => ReloadAsyncAt(DateTimeOffset.Now);

    /// <summary>带快照时刻的重查：分 Agent 速率的时间基准用触发它的那轮回写时刻（比"查完那一刻"
    /// 更接近相邻两轮的真实间隔）。internal 供测试注入合成时刻。</summary>
    internal async Task ReloadAsyncAt(DateTimeOffset snapshotAt)
    {
        if (_query is null || _dateService is null)
        {
            return;
        }

        var sequence = ++_loadSequence;
        var today = _dateService.Today();
        var filter = new UsageQueryFilter { FromDay = today, ToDay = today };
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

            var models = await _query.GetModelBreakdownAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            // 最近活动探针：不限今日、按最近活动排序的会话头几页——派生各 Agent 的最近活动时刻
            //（当日才显示 hh:mm；跨日活动不显示时间，诚实留空）
            var sessions = await _query.GetSessionsAsync(new SessionListQuery
            {
                SortBy = SessionSortField.LastSeenAt,
                SortDescending = true,
                Page = 1,
                PageSize = LastActivityProbeSize,
            });
            if (sequence != _loadSequence)
            {
                return;
            }

            Apply(kpi, daily, agents, models, sessions, snapshotAt);
        }
        catch (Exception ex)
        {
            if (sequence == _loadSequence)
            {
                Console.Error.WriteLine($"[Tokenmetry] 状态栏弹层刷新失败（保留旧值，下轮重试）：{ex.Message}");
            }
        }
    }

    private void Apply(UsageKpiResult kpi, IReadOnlyList<DailyUsagePoint> daily,
        IReadOnlyList<DimensionUsageRow> agents, IReadOnlyList<DimensionUsageRow> models,
        SessionListResult sessions, DateTimeOffset snapshotAt)
    {
        var t = Translations.Instance;
        // 今日 KPI：零 token 显 "—"（诚实：无数据不冒充 0）；成本/占比沿用既有四态文案
        TodayTokensText = kpi.TotalTokens > 0 ? TrendChart.FormatCompactTokens(kpi.TotalTokens) : "—";
        TodayCostText = OverviewViewModel.FormatCost(kpi.Cost);
        CacheShareText = OverviewViewModel.FormatShare(kpi.CacheReadShare);

        // 构成三段：口径同 OverviewViewModel.CreateTokenTypeSegments（Input=未缓存输入 /
        // 输出 / 缓存读写合并段；契约证据见该处注释），条与卡同源一次计算。
        long input = 0, output = 0, cache = 0;
        foreach (var point in daily)
        {
            input += point.InputTokens;
            output += point.OutputTokens;
            cache += point.CacheCreationTokens + point.CacheReadTokens;
        }

        var total = input + output + cache;
        SegmentShares = total > 0
            ? new[] { 100d * input / total, 100d * output / total, 100d * cache / total }
            : ZeroShares;
        (InputTokensText, InputShareText) = SegmentTexts(t, input, total);
        (OutputTokensText, OutputShareText) = SegmentTexts(t, output, total);
        (CacheTokensText, CacheSegShareText) = SegmentTexts(t, cache, total);

        // Agent Top4 + 副题（分 Agent 速率：与上次查询快照配对；最近活动：最近会话查询派生）
        var currentTokens = agents.ToDictionary(a => a.Key, a => a.TotalTokens);
        var agentRates = ComputeAgentRates(_lastAgentTokens, _lastAgentSnapshotAt, currentTokens, snapshotAt);
        _lastAgentTokens = currentTokens;
        _lastAgentSnapshotAt = snapshotAt;
        _lastAgentRates = agentRates;

        var lastActiveByAgent = sessions.Rows
            .GroupBy(r => r.Agent)
            .ToDictionary(g => g.Key, g => g.Max(r => r.LastSeenAt));

        TopAgents = agents
            .Where(a => a.TotalTokens > 0)
            .OrderByDescending(a => a.TotalTokens)
            .Take(TopAgentsCount)
            .Select(a => new StatusPopoverAgentRowViewModel(
                a.Key,
                a.Key,
                a.TotalTokens,
                a.TokenShare,
                BuildRowSubtitle(t, a.Key, agentRates, lastActiveByAgent, snapshotAt),
                name =>
                {
                    var visual = AgentVisualMap.Resolve(name);
                    return (visual.LogoVendor, visual.Glyph.ToString());
                }))
            .ToList();

        IsNoData = kpi.TotalTokens == 0 && total == 0 && TopAgents.Count == 0;

        // 模型用量 Top3（D-019）：按已知成本降序（KnownCostUsd null = 未知成本，-1 哨兵沉底——
        // 低于任何已知成本含"已知为零"；同档按 tokens 降序）。占比/条直接吃 CostShare（本行含
        // 未知成本时服务侧即 null → "—"/空轨道，不伪造）。
        TopModels = models
            .Where(m => m.TotalTokens > 0)
            .OrderByDescending(m => m.Cost.KnownCostUsd ?? -1m)
            .ThenByDescending(m => m.TotalTokens)
            .Take(TopModelsCount)
            .Select(m => new StatusPopoverModelRowViewModel(
                m.Key,
                m.Cost,
                m.CostShare,
                name =>
                {
                    var visual = ModelVisualMap.Resolve(name);
                    return (visual.Vendor, visual.Glyph);
                }))
            .ToList();

        // banner 的增量归属吃本 Apply 的新鲜 Agent 速率（轮次回写先于查询完成，归属滞后一轮时在此校正）
        RecomputeState();
    }

    /// <summary>弹层「暂无数据」诚实空态（KPI/构成/Agent 全零——首轮扫描完成但今日无记录）。</summary>
    [ObservableProperty]
    private bool _isNoData;

    /// <summary>单段 metric 卡文案：无基数（总 token 为 0）→ "—"/"—"（不伪造 0）。</summary>
    private static (string Tokens, string Share) SegmentTexts(Translations t, long value, long total)
        => total > 0
            ? (value.ToString("N0", CultureInfo.InvariantCulture),
               (100d * value / total).ToString("F1", CultureInfo.InvariantCulture) + "%")
            : ("—", "—");

    /// <summary>Agent 行副题（修正项 4 口径）：本轮有增量 → 「当前 X/min」；否则该 Agent 当日最近
    /// 会话 → 「最近活动 hh:mm」；再无 → 空串（行内省略，不伪造时刻）。</summary>
    private static string BuildRowSubtitle(
        Translations t,
        string agentKey,
        IReadOnlyDictionary<string, double> agentRates,
        IReadOnlyDictionary<string, DateTimeOffset> lastActiveByAgent,
        DateTimeOffset now)
    {
        if (agentRates.TryGetValue(agentKey, out var rate) && rate > 0)
        {
            var rateText = TrayStatusText.FormatTokens((long)Math.Round(rate)) + "/min";
            return string.Format(t.PopoverRowRateFormat, rateText);
        }

        if (lastActiveByAgent.TryGetValue(agentKey, out var at) && at.LocalDateTime.Date == now.LocalDateTime.Date)
        {
            return string.Format(t.PopoverRowLastActiveFormat, at.LocalDateTime);
        }

        return string.Empty;
    }

    /// <summary>
    /// 分 Agent 差值速率（修正项 4 同一公式用在 Agent 快照对上）：相邻两次查询间隔 ≤
    /// <see cref="RefreshRateTracker.MaxInterval"/> 且增量 &gt;0 → tokens/min。首次出现 / 消失 / 回落的
    /// Agent 不产出速率（无同源基线，不伪造）；基线整体替换为新快照。
    /// internal 纯函数供测试对账。
    /// </summary>
    internal static IReadOnlyDictionary<string, double> ComputeAgentRates(
        IReadOnlyDictionary<string, long> previous,
        DateTimeOffset previousAt,
        IReadOnlyDictionary<string, long> current,
        DateTimeOffset now)
    {
        var rates = new Dictionary<string, double>();
        var interval = now - previousAt;
        if (previous.Count > 0 && interval >= RefreshRateTracker.MinInterval && interval <= RefreshRateTracker.MaxInterval)
        {
            foreach (var (key, tokens) in current)
            {
                if (previous.TryGetValue(key, out var prev))
                {
                    var delta = tokens - prev;
                    if (delta > 0)
                    {
                        rates[key] = delta / interval.TotalMinutes;
                    }
                }
            }
        }

        return rates;
    }

    // ── 刷新线信号（DashboardViewModel，线程池线程 raise → marshal）──

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_dashboard is null)
        {
            return;
        }

        if (e.PropertyName == nameof(DashboardViewModel.TodayTokens))
        {
            var tokens = _dashboard.TodayTokens;
            RunOnUIThread(() => HandleTodayTokensRound(tokens, DateTimeOffset.Now));
        }
        else if (e.PropertyName is nameof(DashboardViewModel.Status) or nameof(DashboardViewModel.IsLoading))
        {
            // 扫描中灯：Status=="refreshing"（或 IsLoading）置位；轮次终态（ok/no data/error）复位
            var scanning = _dashboard.IsLoading || _dashboard.Status == "refreshing";
            RunOnUIThread(() => IsScanning = scanning);
        }
    }

    /// <summary>一轮今日 tokens 回写的统一处理：清上轮 Warn（无异常的轮次把它带回 Idle/Active；
    /// 同轮新异常的事件晚于回写，会随后再置回）→ 喂速率跟踪器 → 重算三态 → 可见期重查。
    /// internal 时间戳入参供测试/预览注入（真实轮次由 DashboardViewModel 回写驱动）。</summary>
    internal void HandleTodayTokensRound(long todayTokens, DateTimeOffset at)
    {
        _pendingWarn = null;
        _lastSample = _rateTracker.Feed(todayTokens, at);
        RecomputeState();
        if (_visible)
        {
            _ = ReloadAsyncAt(at);
        }
    }

    private void OnAnomalyDetected(object? sender, DashboardViewModel.AnomalyRaised anomaly) =>
        RunOnUIThread(() => NoteAnomaly(anomaly.Title, anomaly.Message));

    /// <summary>注入一条异常（Warn 态）：真实路径来自 DashboardViewModel.AnomalyDetected 同源信号，
    /// internal 直注入供 headless 测试与样式预览演示。</summary>
    internal void NoteAnomaly(string title, string message)
    {
        _pendingWarn = new DashboardViewModel.AnomalyRaised(title, message);
        RecomputeState();
    }

    /// <summary>三态机：Warn（有未消化异常）&gt; Active（本轮有增量速率）&gt; Idle。
    /// Warn 的清除点：无异常的轮次喂入处（OnDashboardPropertyChanged）先清 _pendingWarn 再重算——
    /// 同轮新异常（事件晚于 tokens 回写）会再置回。</summary>
    private void RecomputeState()
    {
        if (_pendingWarn is { } warn)
        {
            State = StatusPopoverState.Warn;
            IsBannerVisible = true;
            IsBannerWarn = true;
            BannerTitle = warn.Title;
            BannerSubtitle = warn.Message;
            return;
        }

        if (_lastSample.TokensPerMinute is { } rate)
        {
            State = StatusPopoverState.Active;
            IsBannerVisible = true;
            IsBannerWarn = false;
            // 增量归属：本轮增速最高的 Agent（Apply 的快照对口径，与 Agent 行同源）；
            // 拿不到具体 Agent（查询未跟上 / 增量来自快照外新 Agent）时给通用标题。
            var topAgent = _lastAgentRates.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
            BannerTitle = string.IsNullOrEmpty(topAgent)
                ? Translations.Instance.PopoverActiveBannerTitle
                : string.Format(Translations.Instance.PopoverActiveBannerTitleFormat, topAgent);
            BannerSubtitle = string.Format(
                Translations.Instance.PopoverActiveBannerSubtitleFormat,
                TrayStatusText.FormatTokens((long)Math.Round(rate)) + "/min");
            return;
        }

        State = StatusPopoverState.Idle;
        IsBannerVisible = false;
        IsBannerWarn = false;
    }

    /// <summary>语言切换重译派生文案（LiveText / banner 标题等由 Translations 取串的属性）。</summary>
    internal void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(LiveText));
        RecomputeState();
    }

    private static void RunOnUIThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
