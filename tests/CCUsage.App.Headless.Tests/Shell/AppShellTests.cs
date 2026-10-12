using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CCUsage.App.DependencyInjection;
using CCUsage.App.Services;
using CCUsage.App.ViewModels;
using CCUsage.App.Views;
using CCUsage.Core.Abstractions;
using Xunit;

namespace CCUsage.App.Headless.Tests.Shell;

/// <summary>
/// D-003 三栏应用外壳的自动化验证（headless，真实 App.axaml + Semi）：
/// 1. 三栏结构（顶部栏 / 左侧导航 / 主内容路由 / 右侧最近会话栏）与 8 项导航；
/// 2. 导航切换路由（总览真实页 D-006 / 占位视图 / 设置页真实视图）与选中态；
/// 3. 全局筛选状态（FilterStateService）变更通知到达订阅页 VM、跨线程写入 marshal 到 UI 线程；
/// 4. 右栏最近会话 + 快速筛选骨架、顶部栏筛选与数据状态；
/// 5. 最小窗口尺寸下三栏不破、方向键移动导航选中（键盘可达）；
/// 6. 明暗两套离屏截图留档（docs/design/app-shell-*.png）。
/// </summary>
public class AppShellTests
{
    /// <summary>隔离 VM：新 FilterStateService，断言默认值/通知机制不受单例状态与其它用例影响。
    /// 查询服务为常驻单例（无会话状态），直接取 DI 实例（总览页按全局筛选重查，见 OverviewTests）；
    /// Agent 目录/toast 同取 DI 单例（目录带服务层缓存，D-008）。</summary>
    private static MainWindowViewModel CreateIsolatedShellViewModel() =>
        new(new FilterStateService(),
            AppServices.Get<SettingsViewModel>(),
            AppServices.Get<SessionsPanelViewModel>(),
            AppServices.Get<IUsageQueryService>(),
            AppServices.Get<AgentCatalogService>(),
            AppServices.Get<IToastService>());

    [AvaloniaFact]
    public void Shell_has_three_columns_with_topbar_sidebar_and_sessions_panel()
    {
        var window = new MainWindow { DataContext = AppServices.Get<MainWindowViewModel>() };
        window.Show(); // 挂入视觉树触发模板应用（GetVisualDescendants 需要）
        Dispatcher.UIThread.RunJobs();

        var topBar = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TopBar");
        var sidebar = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Sidebar");
        var sessionsPanel = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "SessionsPanelBorder");
        var pageHost = window.GetVisualDescendants().OfType<ContentControl>().First(b => b.Name == "PageHost");
        // D-008 起右栏另有最近会话 ListBox，外壳导航列表按 nav-list 类精确选取
        var navList = window.GetVisualDescendants().OfType<ListBox>().Single(lb => lb.Classes.Contains("nav-list"));

        // 8 项导航，文案与顺序对齐原型 .nav（总览 / 会话 / Agent / 模型 / 项目 / 时间 / 成本 / 设置）
        Assert.Equal(8, navList.ItemCount);
        Assert.Equal(
            new[] { "总览", "会话明细", "Agent 统计", "模型统计", "项目统计", "时间分析", "成本分析", "数据源与设置" },
            ((MainWindowViewModel)window.DataContext!).NavigationItems.Select(i => i.Page.Label).ToArray());

        // 三栏列宽：原型 .shell 184 / * / 276
        Assert.Equal(184, sidebar.Width);
        Assert.Equal(276, sessionsPanel.Width);
        Assert.Same(window.DataContext, pageHost.DataContext); // 主内容区绑定 CurrentPage 所在 VM
        Assert.Equal(new Thickness(0, 0, 0, 1), topBar.BorderThickness);
        Assert.Equal(new Thickness(0, 0, 1, 0), sidebar.BorderThickness);
        Assert.Equal(new Thickness(1, 0, 0, 0), sessionsPanel.BorderThickness);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Navigation_selection_swaps_current_page_and_updates_active_state()
    {
        var vm = CreateIsolatedShellViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 初始路由到总览页（D-006 真实页，非占位），第一项为选中态
        var initial = Assert.IsType<OverviewViewModel>(vm.CurrentPage);
        Assert.Equal(PageId.Overview, initial.Page.Id);
        Assert.Same(vm.NavigationItems[0], vm.SelectedNavigation);
        Assert.True(window.GetVisualDescendants().OfType<OverviewView>().Any());

        // 切到会话明细（D-008 真实页，非占位）
        vm.SelectedNavigation = vm.NavigationItems[1];
        Dispatcher.UIThread.RunJobs();
        var sessions = Assert.IsType<SessionsViewModel>(vm.CurrentPage);
        Assert.Equal(PageId.Sessions, sessions.Page.Id);
        Assert.True(window.GetVisualDescendants().OfType<SessionsView>().Any());
        Assert.Same(vm.NavigationItems[1], vm.SelectedNavigation);

        // 切到数据源与设置：路由到既有 SettingsView（真实视图，非占位）
        vm.SelectedNavigation = vm.NavigationItems[7];
        Dispatcher.UIThread.RunJobs();
        Assert.Same(vm.Settings, vm.CurrentPage);
        Assert.True(window.GetVisualDescendants().OfType<SettingsView>().Any());

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Placeholder_pages_show_current_filter_and_update_on_change()
    {
        var vm = CreateIsolatedShellViewModel();

        // 初始页为总览真实页（D-006）；占位页行为用 Agent 统计页验证（会话明细 D-008 起为真实页）
        Assert.IsType<OverviewViewModel>(vm.CurrentPage);
        vm.SelectedNavigation = vm.NavigationItems[2];
        var placeholder = Assert.IsType<PagePlaceholderViewModel>(vm.CurrentPage);
        Assert.Equal(PageId.Agents, placeholder.Page.Id);

        // 默认筛选：最近 7 天 · 所有 Agent，占位页可观测
        Assert.Equal("最近 7 天 · 所有 Agent", placeholder.CurrentFilterDisplay);

        // 切时间范围 → 通知到达订阅页 VM
        vm.Filters.TimeRange = TimeRangeOption.Options[0];
        Assert.Equal("最近 7 天 · 所有 Agent", vm.Filters.Display);
        Assert.Equal(vm.Filters.Display, placeholder.CurrentFilterDisplay);

        // 切 Agent → 同样可观测
        vm.Filters.Agent = new AgentOption("Codex", "Codex");
        Assert.Equal("最近 7 天 · Codex", vm.Filters.Display);
        Assert.Equal(vm.Filters.Display, placeholder.CurrentFilterDisplay);

        // 路由到另一占位页：新页面读到当前筛选值而非默认值
        vm.SelectedNavigation = vm.NavigationItems[3];
        var models = Assert.IsType<PagePlaceholderViewModel>(vm.CurrentPage);
        Assert.Equal("最近 7 天 · Codex", models.CurrentFilterDisplay);
    }

    [AvaloniaFact]
    public void Navigating_away_unsubscribes_old_placeholder_from_filter_changes()
    {
        var vm = CreateIsolatedShellViewModel();
        // 初始为总览真实页（其 Dispose 退订回归在 OverviewTests），占位页退订用两次占位跳转验证
        // （会话明细 D-008 起为真实页，退订回归在 SessionsTests）
        Assert.IsType<OverviewViewModel>(vm.CurrentPage);

        vm.SelectedNavigation = vm.NavigationItems[2];
        var agents = Assert.IsType<PagePlaceholderViewModel>(vm.CurrentPage);

        vm.SelectedNavigation = vm.NavigationItems[3];
        var models = Assert.IsType<PagePlaceholderViewModel>(vm.CurrentPage);
        Assert.NotSame(agents, models);

        // 导航离开后旧占位页不再收筛选通知（Dispose 退订）。FilterStateService 是常驻单例，
        // 强引用订阅不退订会随导航次数无界累积（D-003 审查 P1 同源回归）。
        vm.Filters.TimeRange = TimeRangeOption.Options[2]; // 最近 90 天
        Assert.Equal("最近 90 天 · 所有 Agent", models.CurrentFilterDisplay);
        Assert.Equal("最近 7 天 · 所有 Agent", agents.CurrentFilterDisplay); // 旧实例冻结在退订前的值
    }

    [AvaloniaFact]
    public void Filter_state_marshals_change_notifications_to_ui_thread()
    {
        var filters = new FilterStateService();
        var notifiedOnUi = false;
        TimeRange? received = null;
        filters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FilterStateService.TimeRange))
            {
                notifiedOnUi = Dispatcher.UIThread.CheckAccess();
                received = filters.TimeRange.Value;
            }
        };

        // 非 UI 线程写入：赋值与通知都应 marshal 到 UI 线程（UI 线程未被阻塞时以 RunJobs 驱动）
        Task.Run(() => filters.TimeRange = TimeRangeOption.Options[3]).Wait();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TimeRange.All, received);
        Assert.True(notifiedOnUi, "筛选状态变更通知应在 UI 线程发出");
    }

    [AvaloniaFact]
    public void Data_status_last_updated_marshalls_to_ui_thread_from_timer_thread()
    {
        var vm = CreateIsolatedShellViewModel();
        Assert.Equal("尚未刷新", vm.LastUpdatedText);

        // 后台刷新定时器线程回调 → 顶部栏时间戳更新落到 UI 线程
        Task.Run(() => vm.NoteDataRefreshed()).Wait();
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("最近更新", vm.LastUpdatedText);
    }

    [AvaloniaFact]
    public void Topbar_contains_time_range_agent_filters_data_status_and_settings_entry()
    {
        var vm = CreateIsolatedShellViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var topBar = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TopBar");
        var combos = topBar.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(2, combos.Count);

        // 时间范围：默认最近 7 天（对齐原型），4 档选项
        var timeCombo = combos[0];
        Assert.Equal(TimeRangeOption.Default, timeCombo.SelectedItem);
        Assert.Equal(4, timeCombo.ItemCount);

        // Agent：默认所有 Agent；选项清单为真实数据（D-008：初始仅"所有 Agent"，
        // AgentCatalogService 异步加载后替换，条目数随本机 facts 而定 → 只断言下界与默认选中）
        var agentCombo = combos[1];
        Assert.Equal(AgentOption.All, agentCombo.SelectedItem);
        Assert.True(agentCombo.ItemCount >= 1, "Agent 下拉至少应有\"所有 Agent\"哨兵项");

        // 数据状态：就绪 + 初始"尚未刷新"
        var texts = topBar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(vm.DataStatusText, texts);
        Assert.Contains("尚未刷新", texts);

        // 设置入口按钮 → 导航到数据源与设置页
        var settingsButton = topBar.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("icon-btn"));
        Assert.NotNull(settingsButton.Command);
        settingsButton.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PageId.Settings, vm.SelectedNavigation!.Page.Id);
        Assert.Same(vm.Settings, vm.CurrentPage);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Sessions_panel_shows_recent_sessions_region_and_quick_filter_structure()
    {
        // 结构性断言（数据条目随本机 facts 与异步加载时序而定，数据口径在 SessionsTests 用 stub 精确对账）：
        // 最近会话区（空态/列表二选一）+ 快速筛选（Agent 复选组 + 模型/项目下拉 + 清除/应用入口）。
        var vm = CreateIsolatedShellViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var panel = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "SessionsPanelBorder");
        var panelVm = Assert.IsType<SessionsPanelViewModel>(vm.SessionsPanel);

        var texts = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("最近会话", texts);
        Assert.Contains("快速筛选", texts);
        // 空态与列表互斥可见（真实数据接线后不再有"骨架说明"文案）
        Assert.DoesNotContain("会话列表将在会话明细页（D-008）接入真实数据", texts);

        // 应用 / 清除命令在（提交点行为对账在 SessionsTests）
        Assert.NotNull(panelVm.ApplyQuickFiltersCommand);
        Assert.NotNull(panelVm.ClearQuickFiltersCommand);
        Assert.True(panelVm.ClearQuickFiltersCommand.CanExecute(null));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void MainWindow_at_minimum_size_keeps_three_columns_intact()
    {
        var vm = CreateIsolatedShellViewModel();
        var window = new MainWindow
        {
            DataContext = vm,
            Width = 960, // MinWidth
            Height = 640, // MinHeight
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Size(960, 640), window.ClientSize);

        var sidebar = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Sidebar");
        var sessionsPanel = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "SessionsPanelBorder");
        var pageHost = window.GetVisualDescendants().OfType<ContentControl>().First(b => b.Name == "PageHost");

        Assert.Equal(184, sidebar.Bounds.Width);
        Assert.Equal(276, sessionsPanel.Bounds.Width);
        // 主内容列 960-184-276=500，去掉页边距仍 ≥ 原型 .shell 主列最小宽 420
        Assert.True(pageHost.Bounds.Width >= 420, $"主内容区宽度 {pageHost.Bounds.Width} 应 ≥ 420");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Keyboard_arrow_keys_move_navigation_selection()
    {
        var vm = CreateIsolatedShellViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // D-008 起右栏另有最近会话 ListBox，外壳导航列表按 nav-list 类精确选取
        var navList = window.GetVisualDescendants().OfType<ListBox>().Single(lb => lb.Classes.Contains("nav-list"));
        var firstItem = Assert.IsType<ListBoxItem>(navList.GetRealizedContainers().First());
        firstItem.Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, "ArrowDown");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(vm.NavigationItems[1], vm.SelectedNavigation);
        Assert.Equal(PageId.Sessions, Assert.IsType<SessionsViewModel>(vm.CurrentPage).Page.Id);

        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, "ArrowUp");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(vm.NavigationItems[0], vm.SelectedNavigation);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void App_shell_renders_light_and_dark_screenshots_for_archive()
    {
        // 截图留档目录由环境变量指定（验证时传 docs/design）；未设置时仅断言可渲染。
        var dir = Environment.GetEnvironmentVariable("CCUSAGE_SHELL_PREVIEW_DIR");

        foreach (var (variant, fileName) in new (ThemeVariant, string)[]
                 {
                     (ThemeVariant.Light, "app-shell-light.png"),
                     (ThemeVariant.Dark, "app-shell-dark.png"),
                 })
        {
            var window = new MainWindow
            {
                DataContext = AppServices.Get<MainWindowViewModel>(),
                RequestedThemeVariant = variant,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // headless 渲染定时器不随 RunJobs 走帧，须手动触发一帧才能截到合成结果
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, fileName);
                frame.Save(path, new PngBitmapEncoderOptions());
                Assert.True(File.Exists(path), $"截图未生成：{path}");
            }

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
