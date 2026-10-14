using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TokenPulse.App.Controls;
using TokenPulse.App.Localization;
using TokenPulse.App.Services;
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;

namespace TokenPulse.App.ViewModels;

/// <summary>
/// 会话明细排序选项（I-029 排序白名单 + 降序）。三档对齐原型 #sessionSort（按 Token 排序 /
/// 按成本排序 / 最近活动），默认选中第一项（原型 select 初始渲染口径）。
/// Label 经 Translations 实时取串（进程级订阅语言切换刷新，同 TimeRangeOption 机制）；
/// 值相等只看 (Field, SortDescending)，供 ComboBox SelectedItem 绑定比较。
/// </summary>
public sealed class SessionSortOption : INotifyPropertyChanged, IEquatable<SessionSortOption>
{
    public SessionSortOption(SessionSortField field, bool sortDescending)
    {
        Field = field;
        SortDescending = sortDescending;
    }

    public SessionSortField Field { get; }

    public bool SortDescending { get; }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label => Field switch
    {
        SessionSortField.TotalTokens => Translations.Instance.SortByTokens,
        SessionSortField.Cost => Translations.Instance.SortByCost,
        _ => Translations.Instance.SortByRecent,
    };

    /// <summary>语言切换后刷新展示名。</summary>
    public void RefreshLabel() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

    public static IReadOnlyList<SessionSortOption> Options { get; } =
    [
        new(SessionSortField.TotalTokens, true),
        new(SessionSortField.Cost, true),
        new(SessionSortField.LastSeenAt, true),
    ];

    /// <summary>默认按 Token 降序（对齐原型 #sessionSort 默认选中首项；I-029 契约默认是最近活动，页面口径以原型为准）。</summary>
    public static SessionSortOption Default { get; } = Options[0];

    public override string ToString() => Label;

    public bool Equals(SessionSortOption? other) =>
        other is not null && Field == other.Field && SortDescending == other.SortDescending;

    public override bool Equals(object? obj) => obj is SessionSortOption other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Field, SortDescending);

    static SessionSortOption()
    {
        // 选项为进程级稳定实例（应用级生命周期），直订阅语言切换信号统一刷新 Label，无需退订。
        Translations.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Translations.CurrentLanguage))
            {
                foreach (var option in Options)
                {
                    option.RefreshLabel();
                }
            }
        };
    }
}

/// <summary>
/// 会话明细表的单行展示模型（原型 .sessiontable 行 + .table 观感）。不可变小对象，
/// 由 <see cref="SessionsViewModel"/> 整体替换/追加集合触发绑定刷新。
/// </summary>
public sealed class SessionRowViewModel
{
    public SessionRowViewModel(SessionListRow row)
    {
        SessionId = row.SessionId;
        Agent = row.Agent;
        // facts 无标题字段（I-002/I-029）：主文本诚实用会话标识 SessionId（同搜索口径），不伪造任务标题
        TitleText = row.SessionId;
        ProjectText = FirstNotEmpty(row.ProjectDisplayName, row.Project, "—");
        IconGlyph = AgentVisualMap.Glyph(row.Agent).ToString();
        ChipVariant = AgentVisualMap.ChipVariant(row.Agent);
        ModelText = FirstNotEmpty(row.DominantModel, "—");
        TokensText = row.TotalTokens.ToString("N0", CultureInfo.InvariantCulture);
        CostText = FormatCost(row.CostUsd);
        LastActivityText = SessionsViewModel.FormatRelativeTime(row.LastSeenAt);
    }

    public string SessionId { get; }

    public string Agent { get; }

    /// <summary>主文本 = 会话标识 SessionId（无标题字段的诚实呈现，见构造）。</summary>
    public string TitleText { get; }

    /// <summary>副文本 = 项目（归一化展示名优先，歧义守卫下可能是原始路径；空则 "—"）。</summary>
    public string ProjectText { get; }

    public string IconGlyph { get; }

    /// <summary>软色底变体名（颜色经 SharedStyles chip-* 伪类 DynamicResource 呈现，VM 不持画刷）。</summary>
    public string ChipVariant { get; }

    public string ModelText { get; }

    public string TokensText { get; }

    /// <summary>估算成本文案：<see cref="SessionListRow.CostUsd"/> null（会话内全部记录成本未知）→「未知」不折 0；已知 → "$x.xx"。</summary>
    public string CostText { get; }

    public string LastActivityText { get; }

    internal static string FormatCost(decimal? costUsd) => costUsd is { } known
        ? TrendChart.FormatCostUsd(known, hasUnknownCost: false) ?? Translations.Instance.Unknown
        : Translations.Instance.Unknown;

    private static string FirstNotEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "—";
}

/// <summary>
/// D-008 会话明细页 VM（原型 #page-sessions）：搜索 + 排序（页面内）与 Agent 复选 / 模型 / 项目
/// （右栏快速筛选，<see cref="SessionsPanelViewModel"/> 持有状态）共同驱动明细表查询。
/// <b>筛选关系定案</b>：全局筛选（顶部栏时间范围 + 单选 Agent）= 跨页上下文；右栏复选组/模型/项目 =
/// 会话页级快捷筛选；页面查询 = 两者交集。I-029 的 <see cref="UsageQueryFilter.Agent"/> 是单值，
/// 复选集映射：全勾 → 不过滤（null）；恰勾 1 个 → 该 Agent；真子集（勾 2..N-1 个）→ 按已选 Agent
/// 逐个查询（PageSize=null）合并 + 与服务同口径排序后客户端分页（并集无法在一次单值过滤里表达，
/// 不改已审定的查询契约，取舍记录于 D-008 Dev Log）。复选全不勾 → 诚实空结果（不发查询）。
/// 生命周期 / 线程 / 序号防护纪律同 <see cref="OverviewViewModel"/>：每次导航新建实例、
/// 外壳 Dispose 退订（全局筛选 + 右栏事件 + 防抖定时器）、FilterStateService 通知已在 UI 线程。
/// 搜索防抖：300ms 重启式（每次击键重启定时器，停顿才重查——每击键全量过滤+排序代价随总数线性
/// 增长，且快速输入的中间态必然被覆盖）；headless 测试不驱动 DispatcherTimer 时钟，
/// 经 <see cref="CommitSearchForTests"/> 直调提交（任务卡允许"虚拟时间或直接调命令"）。
/// 分页：原型无分页控件，用「加载更多」（I-029 Page/PageSize 契约，页大小 50）；
/// 计数条 "显示 X / Y 条"（X=已加载，Y=过滤后总数）。
/// 下钻定案：行点击 → toast 显示会话标识 + "详情视图规划中"（原型为 toast 占位；
/// 不在本卡发明弹窗/详情页新功能面，正式下钻留待后续卡）。
/// </summary>
public partial class SessionsViewModel : ViewModelBase, IDisposable, IDataRefreshAware
{
    /// <summary>"加载更多"页大小（I-029 分页契约）。</summary>
    public const int PageSize = 50;

    /// <summary>搜索防抖间隔（毫秒）。</summary>
    internal const int SearchDebounceMilliseconds = 300;

    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _query;
    private readonly SessionsPanelViewModel _panel;
    private readonly IToastService _toast;

    /// <summary>加载序号：重置型加载自增；跨 await 序号不一致的响应/异常直接丢弃（同 OverviewViewModel）。</summary>
    private int _loadSequence;

    /// <summary>Dispose 后封禁一切重查路径（外壳用 Dispose 退订；搜索防抖定时器可能被后续输入重启）。</summary>
    private bool _disposed;

    private DispatcherTimer? _searchTimer;

    /// <summary>当前已加载页码（重置型加载归 1；加载更多递增）。</summary>
    private int _page = 1;

    /// <summary>真子集合并模式的客户端全集（与 Rows 分页联动；其余模式为 null）。</summary>
    private List<SessionListRow>? _subsetRows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(HasMore), nameof(HasRows), nameof(ShowEmptyState), nameof(ShowCountLine))]
    private ObservableCollection<SessionRowViewModel> _rows = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(HasMore), nameof(ShowEmptyState), nameof(ShowCountLine))]
    private long _totalCount;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SessionSortOption _selectedSort = SessionSortOption.Default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError), nameof(ShowEmptyState), nameof(ShowCountLine))]
    private string? _loadError;

    /// <summary>行点击（ListBox SelectedItem）：toast 占位下钻，选中态立即复位（见类型说明下钻定案）。</summary>
    [ObservableProperty]
    private SessionRowViewModel? _selectedSession;

    public SessionsViewModel(
        PageDescriptor page,
        FilterStateService filters,
        IUsageQueryService query,
        SessionsPanelViewModel panel,
        IToastService toast)
    {
        Page = page;
        _filters = filters;
        _query = query;
        _panel = panel;
        _toast = toast;

        filters.PropertyChanged += OnFilterPropertyChanged;
        panel.QuickFiltersCommitted += OnQuickFiltersCommitted;
        _ = LoadAsync();
    }

    public PageDescriptor Page { get; }

    public SessionSortOption[] SortOptions { get; } = SessionSortOption.Options.ToArray();

    public bool HasRows => Rows.Count > 0;

    public bool HasMore => Rows.Count < TotalCount;

    public bool HasLoadError => LoadError is not null;

    /// <summary>
    /// 空态可见性（审查修复轮 P2-2）：无行<b>且无加载失败</b>才呈"没有匹配的会话"——
    /// 失败时同时出现错误面板 + 空态 + "显示 0 / 0 条"是自相矛盾的三重态。
    /// </summary>
    public bool ShowEmptyState => !HasRows && !HasLoadError;

    /// <summary>计数条/加载更多可见性（审查修复轮 P2-2）：加载失败时隐藏——失败不清已加载行
    /// （与 LoadMore 追加失败不清数据的纪律对齐），旧计数在错误面板下不再展示，重试成功后恢复。</summary>
    public bool ShowCountLine => !HasLoadError;

    /// <summary>计数条（原型 #sessionCount 同款口径，去掉"示例"）：显示 X / Y 条（X=已加载，Y=过滤后总数）。</summary>
    public string CountText => string.Format(
        Translations.Instance.CountTextFormat,
        Rows.Count.ToString("N0", CultureInfo.InvariantCulture),
        TotalCount.ToString("N0", CultureInfo.InvariantCulture));

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (FilterStateService.AffectsQueries(e.PropertyName))
        {
            _ = LoadAsync();
        }
    }

    private void OnQuickFiltersCommitted(object? sender, EventArgs e) => _ = LoadAsync();

    partial void OnSearchTextChanged(string value)
    {
        if (_disposed)
        {
            return;
        }

        RestartSearchTimer();
    }

    partial void OnSelectedSortChanged(SessionSortOption value)
    {
        if (!_disposed)
        {
            _ = LoadAsync();
        }
    }

    partial void OnSelectedSessionChanged(SessionRowViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        // 下钻定案（D-008）：toast 显示会话标识 + 规划中提示，不做弹窗/详情页
        _toast.Show(string.Format(Translations.Instance.ToastSessionPlannedFormat, value.SessionId));
        _selectedSession = null; // 直接回写字段避免 setter 递归；下次点击同一行仍触发
        OnPropertyChanged(nameof(SelectedSession));
    }

    /// <summary>搜索防抖：每次击键重启定时器（重启式 debounce），停顿 SearchDebounceMilliseconds 后重查。</summary>
    private void RestartSearchTimer()
    {
        _searchTimer ??= CreateSearchTimer();
        _searchTimer.Stop();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(SearchDebounceMilliseconds);
        _searchTimer.Start();
    }

    private DispatcherTimer CreateSearchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchDebounceMilliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = LoadAsync();
        };
        return timer;
    }

    /// <summary>测试钩子：跳过防抖等待立即按当前搜索词重查（headless 不驱动 DispatcherTimer 时钟）。</summary>
    internal void CommitSearchForTests()
    {
        if (_disposed)
        {
            return;
        }

        _searchTimer?.Stop();
        _ = LoadAsync();
    }

    // ── Agent 复选集 → 查询范围（D-008 定案，internal 供测试对账）──

    internal enum SessionAgentScopeMode
    {
        /// <summary>不过滤（复选组未加载兜底 / 全勾；全局 Agent 未选）。</summary>
        NoRestriction,

        /// <summary>单值过滤（恰勾 1 个，或全局单选 Agent 与复选集有交集）。</summary>
        Single,

        /// <summary>真子集并集（勾 2..N-1 个）：逐 Agent 查询合并 + 客户端分页。</summary>
        SubsetMerge,

        /// <summary>复选全不勾 / 与全局单选无交集：诚实空结果，不发查询。</summary>
        Empty,
    }

    internal sealed record SessionAgentScope(SessionAgentScopeMode Mode, IReadOnlyList<string> Agents);

    /// <summary>
    /// 右栏复选集 + 全局单选 Agent → 本次查询的 Agent 范围。全局 Agent 是单值时与复选集取交集
    /// （交集命中 → Single；否则 Empty）；全局未选时：复选组未加载（<see cref="SessionsPanelViewModel.AgentFilterCount"/>=0）
    /// → NoRestriction 兜底（选项未加载不应假装无会话），全不勾 → Empty，全勾 → NoRestriction，
    /// 恰 1 个 → Single，真子集 → SubsetMerge。
    /// 审查修复轮（P2-1）：全局已选 Agent 而<b>复选组尚未加载完成</b>（AgentFilterCount=0，启动竞态窗口）
    /// 时同样走 Single(全局) 兜底——此前该分支对空复选集取交集得 Empty，页面假呈"没有匹配的会话"
    /// 且选项到位后无重查；兜底语义与复选组未加载的 NoRestriction 分支一致（全局单选继续生效，
    /// 复选组就绪后的首次提交恢复交集语义，无需强制重查）。
    /// </summary>
    internal SessionAgentScope ResolveAgentScope()
    {
        var globalAgent = _filters.Agent.Id == AgentOption.AllId ? null : _filters.Agent.Id;
        var checkedIds = _panel.CheckedAgentIds;

        if (globalAgent is { } agent)
        {
            return _panel.AgentFilterCount == 0 || checkedIds.Contains(agent)
                ? new SessionAgentScope(SessionAgentScopeMode.Single, [agent])
                : new SessionAgentScope(SessionAgentScopeMode.Empty, []);
        }

        if (_panel.AgentFilterCount == 0 || checkedIds.Count == _panel.AgentFilterCount)
        {
            return new SessionAgentScope(SessionAgentScopeMode.NoRestriction, []);
        }

        return checkedIds.Count switch
        {
            0 => new SessionAgentScope(SessionAgentScopeMode.Empty, []),
            1 => new SessionAgentScope(SessionAgentScopeMode.Single, checkedIds),
            _ => new SessionAgentScope(SessionAgentScopeMode.SubsetMerge, checkedIds),
        };
    }

    /// <summary>本次查询的过滤参数（全局时间窗口 + Agent 范围）。internal 供测试断言映射。</summary>
    internal UsageQueryFilter BuildEffectiveFilter(SessionAgentScope scope)
    {
        var global = GlobalFilterMapper.Build(_filters);
        if (scope.Mode is not (SessionAgentScopeMode.Single or SessionAgentScopeMode.SubsetMerge)
            || scope.Agents.Count == 0)
        {
            return global;
        }

        // SubsetMerge 情况下全局 Agent 必为 null（见 ResolveAgentScope），Single 时覆盖为该 Agent
        return new UsageQueryFilter
        {
            FromDay = global.FromDay,
            ToDay = global.ToDay,
            Agent = scope.Agents[0],
        };
    }

    /// <summary>组装 I-029 会话列表查询（页面状态 → 契约参数）。internal 供测试断言映射。</summary>
    internal SessionListQuery BuildSessionQuery(UsageQueryFilter filter, bool unbounded, int page)
    {
        var term = SearchText.Trim();
        return new SessionListQuery
        {
            Filter = filter,
            SearchTerm = term.Length == 0 ? null : term,
            Project = _panel.SelectedProjectPath,
            Model = _panel.SelectedModelName,
            SortBy = SelectedSort.Field,
            SortDescending = SelectedSort.SortDescending,
            Page = page,
            PageSize = unbounded ? null : PageSize,
        };
    }

    /// <summary>后台采集刷新轮次完成后重查本页（IDataRefreshAware）：复用 LoadAsync（回到第 1 页按当前筛选/搜索/排序重查），序号防护保证与筛选重查并发时旧响应丢弃。</summary>
    public Task OnDataRefreshedAsync() => LoadAsync();

    /// <summary>按当前筛选/搜索/排序/页状态重查明细表（序号防护见类型说明）。</summary>
    public async Task LoadAsync()
    {
        var sequence = ++_loadSequence;
        _page = 1;
        _subsetRows = null;
        var scope = ResolveAgentScope();
        var filter = BuildEffectiveFilter(scope);

        try
        {
            List<SessionListRow> rows;
            long totalCount;

            if (scope.Mode == SessionAgentScopeMode.Empty)
            {
                rows = [];
                totalCount = 0;
            }
            else if (scope.Mode == SessionAgentScopeMode.SubsetMerge)
            {
                // 真子集并集：逐已选 Agent 查询（PageSize=null 不分页），VM 合并 + 同口径排序 + 客户端分页
                var merged = new List<SessionListRow>();
                long total = 0;
                foreach (var agent in scope.Agents)
                {
                    var result = await _query.GetSessionsAsync(
                        BuildSessionQuery(BuildEffectiveFilter(new SessionAgentScope(SessionAgentScopeMode.Single, [agent])), unbounded: true, page: 1));
                    if (sequence != _loadSequence)
                    {
                        return;
                    }

                    merged.AddRange(result.Rows);
                    total += result.TotalCount;
                }

                _subsetRows = SortForSubset(merged, SelectedSort.Field, SelectedSort.SortDescending);
                totalCount = total;
                rows = TakeClientPage(_subsetRows, 1);
            }
            else
            {
                var result = await _query.GetSessionsAsync(BuildSessionQuery(filter, unbounded: false, page: 1));
                if (sequence != _loadSequence)
                {
                    return;
                }

                rows = result.Rows.ToList();
                totalCount = result.TotalCount;
            }

            Rows = new ObservableCollection<SessionRowViewModel>(rows.Select(MapRow));
            TotalCount = totalCount;
            LoadError = null;
        }
        catch (Exception ex)
        {
            if (sequence != _loadSequence)
            {
                return; // 旧请求的失败不覆盖新请求的状态
            }

            // 失败语义（审查修复轮 P2-2）：只置错误态，不清已加载行/总数——重置型加载失败
            // 保留上一次成功数据（与 LoadMore 追加失败不清数据的纪律对齐）；
            // 首查即失败时 Rows 本就为空，空态经 ShowEmptyState 让位于错误面板。
            LoadError = string.Format(Translations.Instance.LoadErrorFormat, ex.Message);
        }
    }

    /// <summary>「加载更多」：页码 +1 追加下一页（仅 paged 模式走服务分页；subset 模式从客户端全集续取）。</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!HasMore)
        {
            return;
        }

        var sequence = _loadSequence; // append 有效性 = 期间未发生重置型加载
        var nextPage = _page + 1;
        List<SessionListRow> append;
        try
        {
            if (_subsetRows is { } merged)
            {
                append = TakeClientPage(merged, nextPage);
            }
            else
            {
                var scope = ResolveAgentScope();
                var result = await _query.GetSessionsAsync(
                    BuildSessionQuery(BuildEffectiveFilter(scope), unbounded: false, page: nextPage));
                if (sequence != _loadSequence)
                {
                    return; // 筛选已变：旧页追加作废
                }

                append = result.Rows.ToList();
            }
        }
        catch (Exception ex)
        {
            if (sequence == _loadSequence)
            {
                LoadError = string.Format(Translations.Instance.LoadMoreErrorFormat, ex.Message); // 追加失败不清已加载数据
            }

            return;
        }

        if (sequence != _loadSequence)
        {
            return;
        }

        _page = nextPage;
        foreach (var row in append.Select(MapRow))
        {
            Rows.Add(row);
        }
    }

    // ── subset 合并模式的同口径排序 / 客户端分页（镜像 UsageQueryService.SortRows，含未知成本不折 0）──

    internal static List<SessionListRow> SortForSubset(List<SessionListRow> rows, SessionSortField sortBy, bool descending)
    {
        IOrderedEnumerable<SessionListRow> ordered = sortBy switch
        {
            SessionSortField.TotalTokens => descending
                ? rows.OrderByDescending(row => row.TotalTokens)
                : rows.OrderBy(row => row.TotalTokens),
            SessionSortField.Cost => descending
                ? rows.OrderByDescending(row => row.CostUsd ?? decimal.MinValue)
                : rows.OrderBy(row => row.CostUsd ?? decimal.MinValue),
            _ => descending
                ? rows.OrderByDescending(row => row.LastSeenAt)
                : rows.OrderBy(row => row.LastSeenAt),
        };
        return ordered.ThenBy(row => row.SessionId, StringComparer.Ordinal).ToList();
    }

    private static List<SessionListRow> TakeClientPage(List<SessionListRow> source, int page)
    {
        var skip = (long)(page - 1) * PageSize;
        return skip >= source.Count ? [] : source.Skip((int)skip).Take(PageSize).ToList();
    }

    private static SessionRowViewModel MapRow(SessionListRow row) => new(row);

    // ── 展示格式（internal 供测试对账）──

    /// <summary>相对活动时间：刚刚 / N 分钟前 / N 小时前 / N 天前 / yyyy-MM-dd（跨月收敛到日期）。</summary>
    internal static string FormatRelativeTime(DateTimeOffset timestamp, DateTimeOffset? now = null)
    {
        var elapsed = (now ?? DateTimeOffset.Now) - timestamp;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero; // 时钟偏差防御
        }

        var t = Translations.Instance;
        return elapsed.TotalMinutes < 1 ? t.RelTimeNow
            : elapsed.TotalMinutes < 60 ? string.Format(t.RelTimeMinutesAgo, (int)elapsed.TotalMinutes)
            : elapsed.TotalHours < 24 ? string.Format(t.RelTimeHoursAgo, (int)elapsed.TotalHours)
            : elapsed.TotalDays < 30 ? string.Format(t.RelTimeDaysAgo, (int)elapsed.TotalDays)
            : timestamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _disposed = true;
        _filters.PropertyChanged -= OnFilterPropertyChanged;
        _panel.QuickFiltersCommitted -= OnQuickFiltersCommitted;
        _searchTimer?.Stop();
    }
}
