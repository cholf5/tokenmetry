using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCUsage.App.Services;
using CCUsage.Core.Abstractions;

namespace CCUsage.App.ViewModels;

/// <summary>
/// D-003 应用外壳 VM：持有 8 项导航、当前页面（经路由 DataTemplates 映射到视图）、
/// 顶部栏全局筛选（FilterStateService）与数据状态展示。
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _usageQuery;

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

    /// <summary>右侧最近会话栏（骨架，真实数据接线在 D-008）。</summary>
    public SessionsPanelViewModel SessionsPanel { get; }

    /// <summary>设置页沿用既有 SettingsViewModel/SettingsView（S-002 才重构为原型版）。</summary>
    public SettingsViewModel Settings { get; }

    public MainWindowViewModel(
        FilterStateService filters,
        SettingsViewModel settings,
        SessionsPanelViewModel sessionsPanel,
        IUsageQueryService usageQuery)
    {
        _filters = filters;
        Settings = settings;
        SessionsPanel = sessionsPanel;
        _usageQuery = usageQuery;

        SelectedNavigation = NavigationItems[0];
        CurrentPage = CreatePage(PageId.Overview);
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
        // D-006：总览为真实页（KPI + 趋势 + Agent 环形），随全局筛选刷新
        PageId.Overview => new OverviewViewModel(ShellPages.For(id), _filters, _usageQuery),
        _ => new PagePlaceholderViewModel(ShellPages.For(id), _filters),
    };

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
