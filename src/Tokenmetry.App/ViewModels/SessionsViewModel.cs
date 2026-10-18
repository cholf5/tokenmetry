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
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

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
        // facts 无标题字段（I-002/I-029，隐私红线不存会话内容）：主文本升级为项目展示名
        // （"这是哪个项目的活"），会话标识降为副文本 + tooltip——诚实且比 UUID 有信息量
        TitleText = FirstNotEmpty(row.ProjectDisplayName, row.Project, row.SessionId);
        SessionIdText = row.SessionId;
        var icon = AgentVisualMap.Resolve(row.Agent);
        IconGlyph = icon.Glyph.ToString();
        LogoVendor = icon.LogoVendor;
        ModelText = FirstNotEmpty(row.DominantModel, "—");
        TokensText = row.TotalTokens.ToString("N0", CultureInfo.InvariantCulture);
        CostText = FormatCost(row.CostUsd);
        LastActivityText = SessionsViewModel.FormatRelativeTime(row.LastSeenAt);
    }

    public string SessionId { get; }

    public string Agent { get; }

    /// <summary>主文本 = 项目展示名（空则原始路径，再空则会话标识），见构造。</summary>
    public string TitleText { get; }

    /// <summary>副文本 = 会话标识（tooltip 挂完整 SessionId 可复制）。</summary>
    public string SessionIdText { get; }

    /// <summary>散列兜底字形（lobe 无对应标的 Agent 才显示）。</summary>
    public string IconGlyph { get; }

    /// <summary>品牌 logo 变体名（颜色/图形经 SharedStyles agent-* 伪类 DynamicResource 呈现，VM 不持画刷）。</summary>
    public string LogoVendor { get; }

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
/// D-008 会话明细页 VM（原型 #page-sessions）：搜索 + 排序页内；筛选全部来自顶栏全局四维
/// （时间 + Agent + Model + Project，<see cref="FilterStateService"/>）。原右栏页级快速筛选
/// （Agent 复选组/模型/项目）已随右栏撤除并入顶栏全局筛选
/// （2026-10-18 右栏撤除定案：docs/plans/2026-10-18-right-rail-removal-design.md）。
/// 生命周期 / 线程 / 序号防护纪律同 <see cref="OverviewViewModel"/>：每次导航新建实例、
/// 外壳 Dispose 退订（全局筛选 + 防抖定时器）、FilterStateService 通知已在 UI 线程。
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
    private readonly IToastService _toast;

    /// <summary>加载序号：重置型加载自增；跨 await 序号不一致的响应/异常直接丢弃（同 OverviewViewModel）。</summary>
    private int _loadSequence;

    /// <summary>Dispose 后封禁一切重查路径（外壳用 Dispose 退订；搜索防抖定时器可能被后续输入重启）。</summary>
    private bool _disposed;

    private DispatcherTimer? _searchTimer;

    /// <summary>当前已加载页码（重置型加载归 1；加载更多递增）。</summary>
    private int _page = 1;

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
        IToastService toast)
    {
        Page = page;
        _filters = filters;
        _query = query;
        _toast = toast;

        filters.PropertyChanged += OnFilterPropertyChanged;
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

    /// <summary>组装 I-029 会话列表查询（页面状态 + 顶栏全局筛选 → 契约参数）。internal 供测试断言映射。</summary>
    internal SessionListQuery BuildSessionQuery(UsageQueryFilter filter, int page, bool unbounded = false)
    {
        var term = SearchText.Trim();
        return new SessionListQuery
        {
            Filter = filter,
            SearchTerm = term.Length == 0 ? null : term,
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
        var filter = GlobalFilterMapper.Build(_filters);

        try
        {
            var result = await _query.GetSessionsAsync(BuildSessionQuery(filter, page: 1));
            if (sequence != _loadSequence)
            {
                return;
            }

            Rows = new ObservableCollection<SessionRowViewModel>(result.Rows.Select(MapRow));
            TotalCount = result.TotalCount;
            LoadError = null;
        }
        catch (Exception ex)
        {
            if (sequence != _loadSequence)
            {
                return;
            }

            // 失败语义（审查修复轮 P2-2）：只置错误态，不清已加载行/总数——保留上一次成功数据。
            LoadError = string.Format(Translations.Instance.LoadErrorFormat, ex.Message);
        }
    }

    /// <summary>「加载更多」：页码 +1 追加下一页（服务分页；追加失败不清已加载数据）。</summary>
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
            var result = await _query.GetSessionsAsync(
                BuildSessionQuery(GlobalFilterMapper.Build(_filters), page: nextPage));
            if (sequence != _loadSequence)
            {
                return; // 筛选已变：旧页追加作废
            }

            append = result.Rows.ToList();
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
        _searchTimer?.Stop();
    }
}
