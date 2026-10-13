using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCUsage.App.Services;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Services;

namespace CCUsage.App.ViewModels;

/// <summary>
/// D-003 应用外壳 VM：持有 8 项导航、当前页面（经路由 DataTemplates 映射到视图）、
/// 顶部栏全局筛选（FilterStateService）与数据状态展示。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _usageQuery;
    private readonly AgentCatalogService _agentCatalog;
    private readonly IToastService _toastService;
    private readonly PricingEngine _pricingEngine;

    [ObservableProperty]
    private string _title = "ccusage — Token Analytics";

    [ObservableProperty]
    private NavigationItemViewModel? _selectedNavigation;

    /// <summary>当前页面 VM；视图由 MainWindow 的 DataTemplates 按实际类型路由。</summary>
    [ObservableProperty]
    private ViewModelBase? _currentPage;

    /// <summary>顶部栏数据源就绪状态。TODO(I-028/D-006)：接入查询服务的真实数据源状态后替换静态文案。</summary>
    [ObservableProperty]
    private string _dataStatusText = "本地数据源就绪";

    /// <summary>顶部栏"最近更新"。初始为未刷新；由 App.axaml.cs 的后台刷新回调经 NoteDataRefreshed 更新。</summary>
    [ObservableProperty]
    private string _lastUpdatedText = "尚未刷新";

    /// <summary>侧栏 8 项导航（顺序同原型 .nav）。</summary>
    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; } = NavigationItemViewModel.CreateDefaults();

    /// <summary>全局筛选状态（顶部栏 ComboBox 直接绑定；各页 VM 经 PropertyChanged 订阅）。</summary>
    public FilterStateService Filters => _filters;

    /// <summary>右侧最近会话栏（D-008 真实数据接线：最近会话 Top6 + 会话页级快速筛选状态持有者）。</summary>
    public SessionsPanelViewModel SessionsPanel { get; }

    /// <summary>设置页沿用既有 SettingsViewModel/SettingsView（S-002 才重构为原型版）。</summary>
    public SettingsViewModel Settings { get; }

    public MainWindowViewModel(
        FilterStateService filters,
        SettingsViewModel settings,
        SessionsPanelViewModel sessionsPanel,
        IUsageQueryService usageQuery,
        AgentCatalogService agentCatalog,
        IToastService toastService,
        PricingEngine pricingEngine)
    {
        _filters = filters;
        Settings = settings;
        SessionsPanel = sessionsPanel;
        _usageQuery = usageQuery;
        _agentCatalog = agentCatalog;
        _toastService = toastService;
        _pricingEngine = pricingEngine;

        SelectedNavigation = NavigationItems[0];
        CurrentPage = CreatePage(PageId.Overview);
        // D-008：外壳启动时异步拉取真实 Agent 清单（AgentCatalogService 缓存，单一真相源），
        // 拉到后写入 FilterStateService（顶部栏全局下拉刷新）；右栏复选组共用同一份缓存。
        // 失败诚实保持 [所有 Agent]，不伪造占位清单（见 AgentCatalogService / FilterStateService 注释）。
        _ = LoadAgentOptionsAsync();
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
            Console.Error.WriteLine($"[ccusage] 全局 Agent 选项加载失败（保持\"所有 Agent\"单选项）：{ex.Message}");
        }
    }

    partial void OnSelectedNavigationChanged(NavigationItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        // 页面 VM 每次导航新建，旧实例先退订全局筛选（实现 IDisposable 的页面 VM 约定，
        // 见 PagePlaceholderViewModel/OverviewViewModel）；Settings 是 DI 单例，不属于本方法生命周期管理。
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = CreatePage(value.Page.Id);
    }

    private ViewModelBase CreatePage(PageId id) => id switch
    {
        PageId.Settings => Settings,
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
    /// <b>竞态安全依赖的不变量（审查修复轮 P2 锁定）</b>：AgentCatalogService 的目录查询恒为
    /// <b>无界严格超集</b>（<see cref="UsageQueryFilter"/> 无界、facts 存在即在清单中）——因此
    /// "快照缺 X" 只可能因 X 晚于快照入库；此处的判重补入（AgentOption record 值相等 + AgentId 键判重）
    /// 才能保持幂等。目录查询语义一旦收窄（如随当前筛选收窄）本兜底即失效，改动前先读
    /// AgentCatalogService 的无界超集契约。
    /// </summary>
    private void OpenSessionsForAgent(string agentId)
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

    /// <summary>顶部栏设置入口：导航到数据源与设置页。</summary>
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
    /// 后台数据刷新完成时更新顶部栏"最近更新"时间戳（可能由定时器线程调用，内部 marshal 到 UI 线程）。
    /// TODO(I-028/D-006)：刷新成败与最后数据时间应来自查询/采集服务的真实状态，而非刷新动作时间。
    /// </summary>
    public void NoteDataRefreshed()
    {
        var text = $"最近更新 {DateTime.Now:HH:mm}";
        if (Dispatcher.UIThread.CheckAccess())
        {
            LastUpdatedText = text;
        }
        else
        {
            Dispatcher.UIThread.Post(() => LastUpdatedText = text);
        }
    }
}
