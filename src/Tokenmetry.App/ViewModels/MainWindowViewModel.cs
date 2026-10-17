using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// D-003 应用外壳 VM：持有 9 项导航、当前页面（经路由 DataTemplates 映射到视图）、
/// 顶部栏全局筛选（FilterStateService）与数据状态展示。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _usageQuery;
    private readonly AgentCatalogService _agentCatalog;
    private readonly IToastService _toastService;
    private readonly PricingEngine _pricingEngine;
    private readonly BackgroundRefreshService? _refreshService;

    [ObservableProperty]
    private string _title = "Tokenmetry — Token Analytics";

    [ObservableProperty]
    private NavigationItemViewModel? _selectedNavigation;

    /// <summary>当前页面 VM；视图由 MainWindow 的 DataTemplates 按实际类型路由。</summary>
    [ObservableProperty]
    private ViewModelBase? _currentPage;

    /// <summary>顶部栏数据源状态码（A-12 语义沿用 DashboardViewModel.Status：ok / refreshing /
    /// no data / error / loading）。文案经 <see cref="DataStatusText"/> 实时取串，语言切换重译。</summary>
    private string _dataStatusCode = "loading";

    /// <summary>顶部栏数据源状态文案（按状态码实时取串，初始为启动态——A-12：不再硬编码"就绪"）；
    /// 状态码由 BackgroundRefreshWiring 每轮回写（NoteDataSourceStatus）。</summary>
    public string DataStatusText => DescribeDataStatus(_dataStatusCode);

    private static string DescribeDataStatus(string status) => status switch
    {
        "error" => Translations.Instance.StatusSourceError,
        "no data" => Translations.Instance.StatusNoLocalData,
        "refreshing" => Translations.Instance.StatusScanning,
        "loading" => Translations.Instance.StatusStarting,
        _ => Translations.Instance.StatusReady,
    };

    /// <summary>顶部栏状态灯级别（A-12）：ok=绿 / warn=橙（无数据）/ error=红（本轮失败）。
    /// 经 Classes 条件绑定切换 Ellipse.status-dot 的 status-error/status-warn 类。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDataSourceError), nameof(IsDataSourceWarned))]
    private string _dataStatusLevel = "ok";

    /// <summary>A-12 状态灯红色态绑定（刷新失败）。</summary>
    public bool IsDataSourceError => DataStatusLevel == "error";

    /// <summary>首次使用初始化遮罩可见性：启动探测到 facts 库完全无数据（首次使用）时挂起，
    /// 启动首轮采集刷新结束（成败均然）摘除——只覆盖"初始化数据"这一阶段，不让遮罩挡住
    /// 诚实终态（no data 橙 / error 红 由顶栏状态灯呈现）。仅由启动路径
    /// <see cref="BeginStartupDataRefreshAsync"/> 维护，手动刷新 / 定时器轮次不挂遮罩。</summary>
    [ObservableProperty]
    private bool _isInitializingData;

    /// <summary>A-12 状态灯橙色态绑定（本轮扫描完成但无数据）。</summary>
    public bool IsDataSourceWarned => DataStatusLevel == "warn";

    /// <summary>顶部栏"最近更新"（按刷新时刻实时取串；初始未刷新态）。由 App.axaml.cs 的
    /// 后台刷新回调经 NoteDataRefreshed 更新；语言切换按存储的时刻重译。</summary>
    private DateTimeOffset? _lastRefreshedAt;

    public string LastUpdatedText => _lastRefreshedAt is { } at
        ? string.Format(Translations.Instance.LastUpdatedFormat, at.LocalDateTime)
        : Translations.Instance.NotRefreshedYet;

    /// <summary>侧栏 9 项导航（顺序同 ShellPages 注册表）。</summary>
    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; } = NavigationItemViewModel.CreateDefaults();

    /// <summary>全局筛选状态（顶部栏 ComboBox 直接绑定；各页 VM 经 PropertyChanged 订阅）。</summary>
    public FilterStateService Filters => _filters;

    /// <summary>右侧最近会话栏（D-008 真实数据接线：最近会话 Top6 + 会话页级快速筛选状态持有者）。</summary>
    public SessionsPanelViewModel SessionsPanel { get; }

    /// <summary>S-004 拆分后两个单例页：设置页（应用偏好）与数据与计价页（数据源/口径/目录/别名），
    /// MainWindowViewModel 持有同一实例，外壳 CreatePage 复用。</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>数据与计价页 VM（数据源表 / 口径 / 模型目录 / 别名映射），DI 单例同上。</summary>
    public DataPricingViewModel DataPricing { get; }

    public MainWindowViewModel(
        FilterStateService filters,
        SettingsViewModel settings,
        DataPricingViewModel dataPricing,
        SessionsPanelViewModel sessionsPanel,
        IUsageQueryService usageQuery,
        AgentCatalogService agentCatalog,
        IToastService toastService,
        PricingEngine pricingEngine,
        BackgroundRefreshService? refreshService = null)
    {
        _filters = filters;
        Settings = settings;
        DataPricing = dataPricing;
        SessionsPanel = sessionsPanel;
        _usageQuery = usageQuery;
        _agentCatalog = agentCatalog;
        _toastService = toastService;
        _pricingEngine = pricingEngine;
        // 可选依赖（既有测试按 7 参构造）：App DI 注入容器内同一 BackgroundRefreshService 单例，
        // 顶栏手动刷新与定时器/托盘/设置页重扫共用同一单飞槽；未注入（测试）时刷新命令如实 no-op。
        _refreshService = refreshService;

        SelectedNavigation = NavigationItems[0];
        CurrentPage = CreatePage(PageId.Overview);
        // 外壳 VM 与应用同生命周期，直订阅语言切换信号（重译顶栏命令式文案），无需退订。
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
        // D-008：外壳启动时异步拉取真实 Agent 清单（AgentCatalogService 缓存，单一真相源），
        // 拉到后写入 FilterStateService（顶部栏全局下拉刷新）；右栏复选组共用同一份缓存。
        // 失败诚实保持 [所有 Agent]，不伪造占位清单（见 AgentCatalogService / FilterStateService 注释）。
        _ = LoadAgentOptionsAsync();
    }

    /// <summary>
    /// 语言切换重译：DataStatusText / LastUpdatedText 由状态码与时刻实时取串，此处 raise 即可；
    /// 侧栏导航标签由 NavigationItemViewModel 各自订阅重译。瞬态 toast 不回溯重译。
    /// </summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage))
        {
            OnPropertyChanged(nameof(DataStatusText));
            OnPropertyChanged(nameof(LastUpdatedText));
        }
    }

    private async Task LoadAgentOptionsAsync()
    {
        try
        {
            var options = await _agentCatalog.GetAgentOptionsAsync();
            _filters.UpdateAgentOptions(options);
        }
        catch (Exception ex)
        {
            // 选项加载失败保持初始 [所有 Agent]（诚实空集，不伪造占位清单），下次启动重试（服务层缓存未写入）；
            // 审查修复轮 P2-3：静默吞异常不利排查，补一条最小诊断日志（stderr，不影响 UI）。
            Console.Error.WriteLine($"[Tokenmetry] 全局 Agent 选项加载失败（保持\"所有 Agent\"单选项）：{ex.Message}");
        }
    }

    partial void OnSelectedNavigationChanged(NavigationItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        // 页面 VM 每次导航新建，旧实例先退订全局筛选（实现 IDisposable 的页面 VM 约定，
        // 见 PagePlaceholderViewModel/OverviewViewModel）；Settings/DataPricing 是 DI 单例，
        // 不属于本方法生命周期管理。
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = CreatePage(value.Page.Id);
        // S-002：单例页重进刷新钩子——新页面就位后通知导航进入，数据与计价页的数据源表按最新扫描快照重建。
        (CurrentPage as INavigationAware)?.OnNavigatedTo();
    }

    private ViewModelBase CreatePage(PageId id) => id switch
    {
        PageId.Settings => Settings,
        PageId.DataPricing => DataPricing,
        // D-006/D-007：总览为真实页（KPI + 趋势 + Agent 环形 + 模型/项目/构成/成本趋势），
        // 随全局筛选刷新；GoToPage 经构造回调注入（「查看详情 ↗」跳模型/项目页），
        // 页面 VM 不反向引用本类型（Action<PageId> 单向注入，见 D-007 Dev Log 选型）。
        PageId.Overview => new OverviewViewModel(ShellPages.For(id), _filters, _usageQuery, GoToPage),
        // D-009/D-010/D-011：Agent / 模型 / 项目统计为真实页（随全局筛选刷新）。D-009 行点击经
        // OpenSessionsForAgent 跳会话页（全局单选 Agent 途径，见 AgentsViewModel 定案）；D-011 行点击
        // 经 OpenSessionsForProject 跳会话页（右栏页级项目筛选程序化应用，见 ProjectsViewModel 定案）；
        // D-010 无行点击下钻。页面 VM 不反向引用本类型（回调单向注入，同 D-007 选型）。
        PageId.Agents => new AgentsViewModel(ShellPages.For(id), _filters, _usageQuery, OpenSessionsForAgent),
        PageId.Models => new ModelsViewModel(ShellPages.For(id), _filters, _usageQuery),
        PageId.Projects => new ProjectsViewModel(ShellPages.For(id), _filters, _usageQuery, OpenSessionsForProject),
        // D-008：会话明细为真实页（搜索防抖 + 排序 + 明细表 + 加载更多）；Agent 复选/模型/项目
        // 页级筛选状态由右栏 SessionsPanelViewModel 持有，页面经其提交事件触发重查（定案见各自类型说明）。
        PageId.Sessions => new SessionsViewModel(ShellPages.For(id), _filters, _usageQuery, SessionsPanel, _toastService),
        // D-012：时间分析为真实页（小时分布 + 活跃日历热力图 + 观察项表），随全局筛选刷新。
        PageId.Time => new TimeViewModel(ShellPages.For(id), _filters, _usageQuery),
        // D-013：成本分析为真实页（KPI 四卡 + 口径说明表）；待确认价格卡与定价引擎同一单例
        // （GetUnpricedModels 全局口径，不随筛选变化——CostViewModel 类型说明）。
        PageId.Cost => new CostViewModel(ShellPages.For(id), _filters, _usageQuery, _pricingEngine),
        _ => new PagePlaceholderViewModel(ShellPages.For(id), _filters),
    };

    /// <summary>
    /// D-009 Agent 排行行点击 → 会话页带该 Agent 筛选（决策点 2 定案：全局途径——顶部栏单选 Agent
    /// 是 Agent 维度的跨页筛选宿主，落地后可见、可经「所有 Agent」清除）。目标 Agent 不在选项清单
    /// （目录加载失败窗口期）时如实补入选项，不静默丢弃跳转。
    /// D-016 起状态栏弹层的 Agent 行点击同走本路由（public——既有路由的公开，非新增）。
    /// <b>竞态安全依赖的不变量（审查修复轮 P2 锁定）</b>：AgentCatalogService 的目录查询恒为
    /// <b>无界严格超集</b>（<see cref="UsageQueryFilter"/> 无界、facts 存在即在清单中）——因此
    /// "快照缺 X" 只可能因 X 晚于快照入库；此处的判重补入（AgentOption record 值相等 + AgentId 键判重）
    /// 才能保持幂等。目录查询语义一旦收窄（如随当前筛选收窄）本兜底即失效，改动前先读
    /// AgentCatalogService 的无界超集契约。
    /// </summary>
    public void OpenSessionsForAgent(string agentId)
    {
        var option = _filters.AgentOptions.FirstOrDefault(o => o.Id == agentId);
        if (option is null)
        {
            option = new AgentOption(agentId, agentId);
            _filters.UpdateAgentOptions([.. _filters.AgentOptions, option]);
        }

        // 审查修复轮 P1：右栏复选组是启动期目录快照，可能不含后台刷新后新增的 Agent——只补全局
        // 选项会落进会话页 ResolveAgentScope 的 Empty 死胡同（复选组全勾但不含目标 → 交集空，
        // 硬呈"显示 0 / 0 条"，且「清除」只重勾已知复选项救不回）。同步补入一个<b>已勾选</b>的
        // 复选项，让页级交集继续成立；状态在右栏可见、可手动反勾 / 经「清除」复位。
        // 复选组为空（目录未加载 / 无数据的诚实兜底态）时不补入：count==0 分支本身已按
        // Single(全局) 兜底，补入反而会伪装成"目录只含 X"的已加载态。
        if (SessionsPanel.AgentFilterCount > 0 && SessionsPanel.AgentFilters.All(a => a.AgentId != agentId))
        {
            SessionsPanel.AgentFilters.Add(new AgentQuickFilterViewModel(agentId, option.Label));
        }

        _filters.Agent = option;
        GoToPage(PageId.Sessions);
    }

    /// <summary>
    /// D-011 项目排行行点击 → 会话页带该项目筛选（决策点 2 定案：页级途径——项目维度无全局宿主，
    /// 写入右栏页级筛选并程序化立即应用；落地后右栏可见、「清除」可复位）。状态先写后导航，
    /// 会话页首查即带上筛选。
    /// </summary>
    private void OpenSessionsForProject(string projectPath, string? label)
    {
        SessionsPanel.ApplyExternalProjectFilter(projectPath, label);
        GoToPage(PageId.Sessions);
    }

    /// <summary>顶部栏设置入口：导航到设置页（应用偏好；数据源与计价在数据与计价页）。</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        SelectedNavigation = NavigationItems.First(i => i.Page.Id == PageId.Settings);
    }

    /// <summary>导航到指定页（右栏"查看全部"等外壳内跳转用）。</summary>
    [RelayCommand]
    private void GoToPage(PageId page)
    {
        SelectedNavigation = NavigationItems.First(i => i.Page.Id == page);
    }

    /// <summary>
    /// 顶栏手动刷新（⟳ 按钮）：经容器内同一 <see cref="BackgroundRefreshService"/> 单飞槽触发一轮
    /// 采集刷新——与定时器 / 托盘「立即刷新」/ 数据与计价页重扫同一合并语义（在跑的轮次直接并入等待，
    /// 不重复扫描）。触发即回写顶栏"正在扫描本地数据…"（状态灯绿），轮次结束的状态回写由
    /// BackgroundRefreshWiring 统一完成（NoteDataRefreshed / NoteDataSourceStatus / 页面重查），
    /// 本命令不重复维护终态。未接线（测试构造未注入刷新服务）时如实 no-op。
    /// </summary>
    [RelayCommand]
    private async Task RefreshDataAsync()
    {
        if (_refreshService is null)
        {
            return;
        }

        NoteDataSourceStatus("refreshing");
        await _refreshService.TriggerRefreshAsync();
    }

    /// <summary>
    /// 启动数据初始化（App.OnFrameworkInitializationCompleted 调用，fire-and-forget）：
    /// 无论库内有无数据，进程打开就立即触发一轮采集刷新（定时器首跑要等一个完整间隔）；
    /// 顶栏即时切到"正在扫描"反馈——原先整个首轮期间停留在"启动中…"且毫无可见进展，
    /// 首启用户只能手动点刷新（本轮不再静默）。
    /// <para>探测到 facts 库完全无数据（首次使用）时挂全窗口"正在初始化"遮罩
    /// （<see cref="IsInitializingData"/>），本轮结束（成功 / no data / error 均然）摘除：
    /// 成功由接线层页面重查填入数据；无数据与失败由顶栏状态灯如实呈现，遮罩不掩盖终态。
    /// 探测本身失败（查询抛错）按"未知"处理——不挂遮罩但照常刷新，宁可不遮罩不误挡。</para>
    /// 未注入刷新服务（测试 7 参构造）时如实 no-op。
    /// </summary>
    public async Task BeginStartupDataRefreshAsync()
    {
        if (_refreshService is null)
        {
            return;
        }

        NoteDataSourceStatus("refreshing");
        if (await ProbeStoreEmptyAsync())
        {
            IsInitializingData = true;
        }

        try
        {
            await _refreshService.TriggerRefreshAsync();
        }
        finally
        {
            IsInitializingData = false;
        }
    }

    /// <summary>
    /// facts 库无数据探测（启动路径专用）：无界日序列只返回实际有数据的日期（IUsageQueryService
    /// 契约），空集即库内完全无 usage facts。查询在线程池执行——SQLite 聚合的同步段不上 UI 线程，
    /// 避免大库首屏卡顿；续体回到 UI 上下文，调用方的属性回写保持 UI 线程亲和。
    /// 失败返回 false（= 不挂遮罩），异常落最小诊断日志（stderr，同 LoadAgentOptionsAsync 约定）。
    /// </summary>
    private async Task<bool> ProbeStoreEmptyAsync()
    {
        try
        {
            var series = await Task.Run(() => _usageQuery.GetDailySeriesAsync(new UsageQueryFilter()));
            return series.Count == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 启动数据探测失败（不显示初始化遮罩）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 后台数据刷新完成时更新顶部栏"最近更新"时间戳（可能由定时器线程调用，内部 marshal 到 UI 线程）。
    /// TODO(I-028/D-006)：刷新成败与最后数据时间应来自查询/采集服务的真实状态，而非刷新动作时间。
    /// </summary>
    public void NoteDataRefreshed()
    {
        RunOnUIThread(() =>
        {
            _lastRefreshedAt = DateTimeOffset.Now;
            OnPropertyChanged(nameof(LastUpdatedText));
        });
    }

    /// <summary>
    /// A-12：由 BackgroundRefreshWiring 每轮回写采集状态（DashboardViewModel.Status 语义：
    /// ok / refreshing / no data / error / loading），驱动顶栏状态文案与状态灯颜色。
    /// 可能由定时器/线程池线程调用，内部 marshal 到 UI 线程。
    /// </summary>
    public void NoteDataSourceStatus(string status)
    {
        var level = status switch
        {
            "error" => "error",
            "no data" => "warn",
            _ => "ok",
        };
        RunOnUIThread(() =>
        {
            _dataStatusCode = status;
            DataStatusLevel = level;
            OnPropertyChanged(nameof(DataStatusText));
        });
    }

    /// <summary>
    /// 后台采集刷新轮次完成后重查当前页面与右栏最近会话：页面 VM 只在导航 / 全局筛选变化时查询，
    /// 不重查则扫描入库后可见页面停留在旧数据（手动刷新按钮的可见效果依赖本回调）。
    /// 由接线层线程池线程调用，内部 marshal 到 UI 线程（页面 VM 按 UI 线程亲和设计）。
    /// 重查经各页 <see cref="IDataRefreshAware.OnDataRefreshedAsync"/>（LoadAsync 序号防护保证
    /// 并发安全）；数据与计价页不实现该接口（数据源表由重扫路径与 OnNavigatedTo 重建）。
    /// </summary>
    public void RefreshCurrentPageData()
    {
        RunOnUIThread(() =>
        {
            if (CurrentPage is IDataRefreshAware refreshable)
            {
                _ = refreshable.OnDataRefreshedAsync();
            }

            _ = SessionsPanel.ReloadRecentSessionsAsync();
        });
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
