using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// D-003 三栏应用外壳的自动化验证（headless，真实 App.axaml + Semi）：
/// 1. 三栏结构（顶部栏 / 左侧导航 / 主内容路由 / 右侧最近会话栏）与 9 项导航；
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
            AppServices.Get<DataPricingViewModel>(),
            AppServices.Get<IUsageQueryService>(),
            AppServices.Get<AgentCatalogService>(),
            AppServices.Get<IToastService>(),
            AppServices.Get<Core.Services.PricingEngine>());

    [AvaloniaFact]
    public void Shell_has_two_columns_with_topbar_and_sidebar()
    {
        var window = new MainWindow { DataContext = AppServices.Get<MainWindowViewModel>() };
        window.Show(); // 挂入视觉树触发模板应用（GetVisualDescendants 需要）
        Dispatcher.UIThread.RunJobs();

        var topBar = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TopBar");
        var sidebar = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Sidebar");
        var pageHost = window.GetVisualDescendants().OfType<ContentControl>().First(b => b.Name == "PageHost");
        // 外壳固化为两栏（2026-10-18 右栏撤除定案）：SessionsPanelBorder 不得存在
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(),
            b => b.Name == "SessionsPanelBorder");
        // 右栏最近会话 ListBox 已随撤栏消失，外壳导航列表按 nav-list 类精确选取
        var navList = window.GetVisualDescendants().OfType<ListBox>().Single(lb => lb.Classes.Contains("nav-list"));

        // 9 项导航（S-004 拆分后），文案与顺序对齐 ShellPages 注册表（总览 / 会话 / Agent / 模型 /
        // 项目 / 时间 / 成本 / 数据与计价 / 设置）
        Assert.Equal(9, navList.ItemCount);
        Assert.Equal(
            new[] { "总览", "会话明细", "Agent 统计", "模型统计", "项目统计", "时间分析", "成本分析", "数据与计价", "设置" },
            ((MainWindowViewModel)window.DataContext!).NavigationItems.Select(i => i.Page.Label).ToArray());

        // 两栏列宽：原型 .shell 184 / *（原 276 右栏列已归还主内容）
        Assert.Equal(184, sidebar.Width);
        Assert.Same(window.DataContext, pageHost.DataContext); // 主内容区绑定 CurrentPage 所在 VM
        Assert.Equal(new Thickness(0, 0, 0, 1), topBar.BorderThickness);
        Assert.Equal(new Thickness(0, 0, 1, 0), sidebar.BorderThickness);

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

        // 切到 Agent / 模型 / 项目统计（D-009/D-010/D-011 真实页，非占位）
        vm.SelectedNavigation = vm.NavigationItems[2];
        Dispatcher.UIThread.RunJobs();
        var agents = Assert.IsType<AgentsViewModel>(vm.CurrentPage);
        Assert.Equal(PageId.Agents, agents.Page.Id);
        Assert.True(window.GetVisualDescendants().OfType<AgentsView>().Any());

        vm.SelectedNavigation = vm.NavigationItems[3];
        Dispatcher.UIThread.RunJobs();
        var models = Assert.IsType<ModelsViewModel>(vm.CurrentPage);
        Assert.Equal(PageId.Models, models.Page.Id);
        Assert.True(window.GetVisualDescendants().OfType<ModelsView>().Any());

        vm.SelectedNavigation = vm.NavigationItems[4];
        Dispatcher.UIThread.RunJobs();
        var projects = Assert.IsType<ProjectsViewModel>(vm.CurrentPage);
        Assert.Equal(PageId.Projects, projects.Page.Id);
        Assert.True(window.GetVisualDescendants().OfType<ProjectsView>().Any());

        // 切到时间分析 / 成本分析（D-012/D-013 真实页，非占位——9 个导航页全部真实化）
        vm.SelectedNavigation = vm.NavigationItems[5];
        Dispatcher.UIThread.RunJobs();
        var time = Assert.IsType<TimeViewModel>(vm.CurrentPage);
        Assert.Equal(PageId.Time, time.Page.Id);
        Assert.True(window.GetVisualDescendants().OfType<TimeAnalysisView>().Any());

        vm.SelectedNavigation = vm.NavigationItems[6];
        Dispatcher.UIThread.RunJobs();
        var cost = Assert.IsType<CostViewModel>(vm.CurrentPage);
        Assert.Equal(PageId.Cost, cost.Page.Id);
        Assert.True(window.GetVisualDescendants().OfType<CostAnalysisView>().Any());

        // 切到数据与计价：路由到 DataPricingView（S-004 拆分：数据源表 + 口径区 + 目录 + 别名）
        vm.SelectedNavigation = vm.NavigationItems[7];
        Dispatcher.UIThread.RunJobs();
        Assert.Same(vm.DataPricing, vm.CurrentPage);
        Assert.True(window.GetVisualDescendants().OfType<DataPricingView>().Any());
        // S-002：单例页重进刷新（INavigationAware.OnNavigatedTo）——数据源表按真实采集注册表重建，
        // 行数 = 管道注册的 19 个数据源（快照有无随本机扫描而定，行数环境无关）
        Assert.Equal(19, vm.DataPricing.SourceRows.Count);

        // 切到设置：路由到 SettingsView（S-004 拆分后收缩为应用偏好）
        vm.SelectedNavigation = vm.NavigationItems[8];
        Dispatcher.UIThread.RunJobs();
        Assert.Same(vm.Settings, vm.CurrentPage);
        Assert.True(window.GetVisualDescendants().OfType<SettingsView>().Any());

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Placeholder_pages_show_current_filter_and_update_on_change()
    {
        // D-012/D-013 起时间分析 / 成本分析为真实页（路由断言见 Navigation_selection_* 与 TimeCostPagesTests），
        // 占位路由仅剩未知兜底；占位页语义（读当前筛选值 + 随变更更新）在 VM 层直接验证。
        var filters = new FilterStateService();
        var placeholder = new PagePlaceholderViewModel(ShellPages.For(PageId.Time), filters);

        // 默认筛选：最近 7 天 · 所有 Agent · 全部模型 · 全部项目（四维汇总，右栏撤除定案），占位页可观测
        Assert.Equal("最近 7 天 · 所有 Agent · 全部模型 · 全部项目", placeholder.CurrentFilterDisplay);

        // 切时间范围 → 通知到达订阅页 VM
        filters.TimeRange = TimeRangeOption.Last7Days;
        Assert.Equal("最近 7 天 · 所有 Agent · 全部模型 · 全部项目", filters.Display);
        Assert.Equal(filters.Display, placeholder.CurrentFilterDisplay);

        // 切 Agent → 同样可观测
        filters.Agent = new AgentOption("Codex", "Codex");
        Assert.Equal("最近 7 天 · Codex · 全部模型 · 全部项目", filters.Display);
        Assert.Equal(filters.Display, placeholder.CurrentFilterDisplay);
    }

    [AvaloniaFact]
    public void Navigating_to_same_page_creates_fresh_viewmodel_instance()
    {
        // 页面 VM 每次导航新建（D-003 约定）：两次进入时间分析页得到不同实例，旧实例状态不泄漏；
        // 导航离开触发旧实例 Dispose 退订的端到端验证在 TimeCostPagesTests（stub 查询计数可观测）。
        var vm = CreateIsolatedShellViewModel();
        Assert.IsType<OverviewViewModel>(vm.CurrentPage);

        vm.SelectedNavigation = vm.NavigationItems[5];
        var first = Assert.IsType<TimeViewModel>(vm.CurrentPage);

        vm.SelectedNavigation = vm.NavigationItems[6];
        Assert.IsType<CostViewModel>(vm.CurrentPage);

        vm.SelectedNavigation = vm.NavigationItems[5];
        var second = Assert.IsType<TimeViewModel>(vm.CurrentPage);
        Assert.NotSame(first, second);
    }

    [AvaloniaFact]
    public void Filter_state_marshals_change_notifications_to_ui_thread()
    {
        var filters = new FilterStateService();
        var notifiedOnUi = false;
        string? received = null;
        filters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FilterStateService.TimeRange))
            {
                notifiedOnUi = Dispatcher.UIThread.CheckAccess();
                received = filters.TimeRange.Id;
            }
        };

        // 非 UI 线程写入：赋值与通知都应 marshal 到 UI 线程（UI 线程未被阻塞时以 RunJobs 驱动）
        Task.Run(() => filters.TimeRange = TestTimeRanges.All).Wait();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("All", received);
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
        Assert.Equal(3, combos.Count); // Agent + Model + Project（D-020：时间范围改档名按钮 + 弹层选择器）

        // 时间范围（D-020 选择器）：默认最近 7 天；内置 8 档 + 自定义哨兵（预设未装载），档名按钮取 VM 面
        var timeButton = topBar.GetVisualDescendants().OfType<DropDownButton>().First();
        var picker = Assert.IsType<TimeRangePicker>(timeButton.Parent!);
        Assert.Same(vm.TimePicker, picker.DataContext);
        Assert.Equal(TimeRangeOption.Default.Id, vm.TimePicker.FilterState.TimeRange.Id);
        Assert.Equal(9, vm.TimePicker.FilterState.TimeRangeOptions.Count);

        // D-020 修正项 1/2 回归锁：触发按钮文字观感与相邻筛选下拉一致——颜色（Semi 默认按钮是
        // 主色蓝）与字重（Semi 按钮默认 DemiBold，下拉为 Normal）双维度；字号两边同 14 一并锁住。
        // 颜色按值比较——同名画刷经不同装载路径非同实例，实例断言不可靠（Q-014 教训）。
        var agentBrush = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(combos[0].Foreground);
        var timeBrush = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(timeButton.Foreground);
        Assert.Equal(agentBrush.Color, timeBrush.Color);
        Assert.Equal(combos[0].FontWeight, timeButton.FontWeight);
        Assert.Equal(combos[0].FontSize, timeButton.FontSize);

        // Agent：默认所有 Agent；选项清单为真实数据（D-008：初始仅"所有 Agent"，
        // AgentCatalogService 异步加载后替换，条目数随本机 facts 而定 → 只断言下界与默认选中）
        var agentCombo = combos[0];
        Assert.Equal(AgentOption.All, agentCombo.SelectedItem);
        Assert.True(agentCombo.ItemCount >= 1, "Agent 下拉至少应有\"所有 Agent\"哨兵项");

        // 数据状态：就绪 + 初始"尚未刷新"
        var texts = topBar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(vm.DataStatusText, texts);
        Assert.Contains("尚未刷新", texts);

        // 顶栏两个 icon-btn：⟳ 手动刷新（接外壳刷新线）+ ⚙ 设置入口——按命令定位，不依赖顺序
        Assert.Contains(
            topBar.GetVisualDescendants().OfType<Button>(),
            b => b.Command == vm.RefreshDataCommand);
        var settingsButton = topBar.GetVisualDescendants().OfType<Button>()
            .First(b => b.Command == vm.OpenSettingsCommand);
        Assert.NotNull(settingsButton.Command);
        settingsButton.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PageId.Settings, vm.SelectedNavigation!.Page.Id);
        Assert.Same(vm.Settings, vm.CurrentPage);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void MainWindow_at_minimum_size_keeps_two_columns_intact()
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
        var pageHost = window.GetVisualDescendants().OfType<ContentControl>().First(b => b.Name == "PageHost");

        Assert.Equal(184, sidebar.Width);
        // 主内容列 960-184=776（右栏撤除定案：原 276 列归还主内容），去掉页边距仍 ≥ 700
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(),
            b => b.Name == "SessionsPanelBorder");
        Assert.True(pageHost.Bounds.Width >= 700, $"主内容区宽度 {pageHost.Bounds.Width} 应 ≥ 700");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Nav_item_background_transition_never_glows_white()
    {
        // 导航项闪白回归锁：导航项静息态必须是「同 hover RGB 的 0 alpha」画刷，不能退回 Transparent。
        // Avalonia 的 Transparent 是透明白（#00FFFFFF），BrushTransition 插值会扫过半透明白：
        // 暗色侧鼠标悬停 / 点选切换时，条目会闪一下白。锁两层：
        // 1) 静息色与 hover 色同 RGB、alpha=0（渐变只走 alpha 的结构前提）；
        // 2) 真实切换选中项时，旧选中项的过渡中间帧不得比两端点更亮。
        var app = Application.Current!;
        var previousVariant = app.RequestedThemeVariant;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        try
        {
            var vm = CreateIsolatedShellViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var navList = window.GetVisualDescendants().OfType<ListBox>()
                .Single(lb => lb.Classes.Contains("nav-list"));
            var items = navList.GetRealizedContainers().OfType<ListBoxItem>().ToList();
            var leaving = items[0];

            Assert.True(app.TryGetResource("AppNavRestBackgroundBrush", ThemeVariant.Dark, out var restValue));
            var rest = Assert.IsAssignableFrom<ISolidColorBrush>(restValue).Color;
            Assert.True(app.TryGetResource("AppNavHoverBackgroundBrush", ThemeVariant.Dark, out var hoverValue));
            var hover = Assert.IsAssignableFrom<ISolidColorBrush>(hoverValue).Color;
            Assert.True(app.TryGetResource("AppNavActiveBackgroundBrush", ThemeVariant.Dark, out var activeValue));
            var active = Assert.IsAssignableFrom<ISolidColorBrush>(activeValue).Color;

            Assert.Equal(0, rest.A);
            Assert.Equal(hover.R, rest.R);
            Assert.Equal(hover.G, rest.G);
            Assert.Equal(hover.B, rest.B);

            // 未选中项的实际底色必须就是静息令牌：样式若退回 Transparent（透明白），这里直接红
            var atRest = Assert.IsAssignableFrom<ISolidColorBrush>(items[3].Background).Color;
            Assert.Equal(rest, atRest);

            var observed = new List<Color>();
            leaving.PropertyChanged += (_, e) =>
            {
                if (e.Property == TemplatedControl.BackgroundProperty
                    && leaving.Background is ISolidColorBrush brush)
                {
                    observed.Add(brush.Color);
                }
            };

            navList.SelectedIndex = 1;
            // 固定走满过渡窗口：不在 alpha=0 时提前退出——样式切换瞬间值可能先落到静息色、
            // 动画下一帧才接手；headless 时钟偶尔会一帧跨完整个过渡，故不强制要求采到中间帧
            for (var i = 0; i < 30; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(8);
            }

            // 过渡帧检查是补充护栏（结构前提已由上面的令牌/实际底色断言锁死）
            Assert.NotEmpty(observed);

            var maxChannel = new[] { hover.R, hover.G, hover.B, active.R, active.G, active.B }.Max() + 8;
            Assert.All(observed, c => Assert.True(
                c.R <= maxChannel && c.G <= maxChannel && c.B <= maxChannel,
                $"旧选中项过渡出现亮帧 #{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}（通道上限 {maxChannel:X2}）"
                + "——静息色退化成 Transparent（透明白）会扫过白色"));

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            app.RequestedThemeVariant = previousVariant;
        }
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
    public void Keyboard_tab_traversal_reaches_topbar_filters_settings_and_navigation()
    {
        // Q-002 键盘可达性锁：Tab 遍历主窗口，顶栏全局筛选（时间选择器按钮 + Agent/Model/Project 下拉）、
        // 设置入口、侧栏导航全部可达
        // （可视焦点 ring 的观感属 Semi 控件模板 + 真机复核范畴，headless 只锁遍历可达性）。
        var vm = CreateIsolatedShellViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var topBar = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TopBar");
        var combos = topBar.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(3, combos.Count); // Agent + Model + Project（D-020：时间范围改档名按钮 + 弹层选择器）
        var timeButton = topBar.GetVisualDescendants().OfType<DropDownButton>().First();
        var settingsButton = topBar.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("icon-btn"));
        var navList = window.GetVisualDescendants().OfType<ListBox>().Single(lb => lb.Classes.Contains("nav-list"));

        var visited = new List<Control>();
        var focusManager = window.FocusManager!;
        for (var i = 0; i < 10; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "Tab");
            Dispatcher.UIThread.RunJobs();

            if (focusManager.GetFocusedElement() is Control focused && !visited.Contains(focused))
            {
                visited.Add(focused);
            }

            var allReached =
                visited.Contains(timeButton) && visited.Contains(combos[0]) &&
                visited.Contains(settingsButton) &&
                visited.Any(c => c == navList || (c is ListBoxItem item && navList.IsVisualAncestorOf(item)));
            if (allReached)
            {
                break;
            }
        }

        Assert.Contains(timeButton, visited);         // 全局时间范围选择器（D-020 档名按钮）
        Assert.Contains(combos[0], visited);          // 全局 Agent 下拉
        Assert.Contains(settingsButton, visited);     // 顶栏设置入口
        Assert.Contains(visited, c => c == navList || (c is ListBoxItem item && navList.IsVisualAncestorOf(item)));
        // 焦点始终在窗口内（没有逃逸到不可见层）
        Assert.All(visited, c => Assert.True(window.IsVisualAncestorOf(c), "焦点控件应在主窗口可视树内"));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void App_shell_renders_light_and_dark_screenshots_for_archive()
    {
        // 截图留档目录由环境变量指定（验证时传 docs/design）；未设置时仅断言可渲染。
        var dir = Environment.GetEnvironmentVariable("TOKENMETRY_SHELL_PREVIEW_DIR");

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
