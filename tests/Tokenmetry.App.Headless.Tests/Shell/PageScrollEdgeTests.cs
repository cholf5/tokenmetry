using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// Q-006 页面滚动面贴窗口边缘契约（headless，真实 MainWindow + 页面路由）：
/// PageHost 不再套 22,22,22,30 外边距——页面滚动视口直达窗口右缘，竖向滚动条贴窗口右缘；
/// 页面留白由各页根滚动面自持（ScrollViewer.page-root 的 Padding=22,22,22,30），
/// 内容视觉间距与迁移前完全一致（判别性断言是"视口/滚动条贴窗缘"，留白断言迁移前后均绿）。
/// 会话页无页面级滚动面（钉住头尾 + 卡内虚拟化列表）：头尾/面板自补边距保持观感，
/// 列表滚动条贴卡片内缘属有意设计（卡内滚动），本类只锁卡片留白与顶部间距不变。
/// 几何断言经 TranslatePoint 换算窗口坐标，不假设元素在 (0,0)（headless 内容居中陷阱，Q-004 踩坑四）。
/// </summary>
public class PageScrollEdgeTests
{
    /// <summary>隔离 VM（同 AppShellTests 套路）：新 FilterStateService，导航状态不与其它用例串扰。</summary>
    private static MainWindowViewModel CreateIsolatedShellViewModel() =>
        new(new FilterStateService(),
            AppServices.Get<SettingsViewModel>(),
            AppServices.Get<DataPricingViewModel>(),
            AppServices.Get<IUsageQueryService>(),
            AppServices.Get<AgentCatalogService>(),
            AppServices.Get<IToastService>(),
            AppServices.Get<Core.Services.PricingEngine>());

    [AvaloniaFact]
    public void Page_scroll_surfaces_reach_window_right_edge_with_content_inset_self_held()
    {
        var vm = CreateIsolatedShellViewModel();
        var window = new MainWindow { DataContext = vm, Width = 960, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var windowWidth = window.ClientSize.Width;

        // ═══ 总览页（根 ScrollViewer 代表 7 个 page-root 页）═══
        var overview = window.GetVisualDescendants().OfType<OverviewView>().Single();
        var sv = Assert.IsType<ScrollViewer>(overview.GetVisualDescendants().First(c => c is ScrollViewer));

        // 滚动视口右缘 = 窗口右缘（Q-006 判别性断言：迁移前视口离窗缘 22px）
        var svRight = sv.TranslatePoint(new Point(sv.Bounds.Width, 0), window)!.Value.X;
        Assert.Equal(windowWidth, svRight, 1);

        // 滚动一下（Auto 显条）→ 竖向滚动条右缘 = 窗口右缘
        sv.Offset = new Vector(0, 60);
        Dispatcher.UIThread.RunJobs();
        var bar = Assert.IsType<ScrollBar>(sv.GetVisualDescendants()
            .First(c => c is ScrollBar { Orientation: Orientation.Vertical }));
        Assert.True(bar.IsVisible, "内容高于视口，滚动后竖条应可见");
        var barRight = bar.TranslatePoint(new Point(bar.Bounds.Width, 0), window)!.Value.X;
        Assert.Equal(windowWidth, barRight, 1);

        // 内容留白自持：滚动内容右缘离窗口右缘 ≥ 20px（Padding=22 接管 PageHost 外边距，视觉不变）
        var content = Assert.IsType<StackPanel>(sv.Content);
        var contentRight = content.TranslatePoint(new Point(content.Bounds.Width, 0), window)!.Value.X;
        Assert.True(contentRight <= windowWidth - 20,
            $"滚动内容右缘 {contentRight:0.#} 应离窗口右缘留 ≥20px 呼吸（窗口宽 {windowWidth}）");

        // ═══ 数据与计价页（根 Grid 包裹内层 ScrollViewer，page-root 类照常生效）═══
        vm.SelectedNavigation = vm.NavigationItems[7];
        Dispatcher.UIThread.RunJobs();
        var pricing = window.GetVisualDescendants().OfType<DataPricingView>().Single();
        var pricingSv = Assert.IsType<ScrollViewer>(pricing.GetVisualDescendants().First(c => c is ScrollViewer));
        var pricingSvRight = pricingSv.TranslatePoint(new Point(pricingSv.Bounds.Width, 0), window)!.Value.X;
        Assert.Equal(windowWidth, pricingSvRight, 1);

        // ═══ 会话页（无页面级滚动面：卡片留白与顶部间距保持，观感与迁移前一致）═══
        vm.SelectedNavigation = vm.NavigationItems[1];
        Dispatcher.UIThread.RunJobs();
        var sessions = window.GetVisualDescendants().OfType<SessionsView>().Single();

        var header = Assert.IsType<StackPanel>(sessions.GetVisualDescendants().First(c => c is StackPanel));

        // 进场过渡（Y 8→0 / 180ms，MainWindow code-behind）在 headless 不随 RunJobs 走帧，
        // 视图悬在起始 Y+8 处（74+8=82 即最初实测值）——泵渲染帧到几何落定再锁顶部间距
        // （判定须严于 Assert.Equal 的 0.05 精度，否则过渡尾段提前退出：74.937 翻车实录）
        PumpUntilSettled(window, () =>
            Math.Abs(header.TranslatePoint(new Point(0, 0), window)!.Value.Y - (52 + 22)) < 0.01);
        var headerTop = header.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.Equal(52 + 22, headerTop, 1); // 顶栏 52 + 页面上边距 22

        var panel = Assert.IsType<Border>(sessions.GetVisualDescendants()
            .First(c => c is Border { Classes: var cl } && cl.Contains("panel")));
        var panelRight = panel.TranslatePoint(new Point(panel.Bounds.Width, 0), window)!.Value.X;
        Assert.True(panelRight <= windowWidth - 20,
            $"会话面板右缘 {panelRight:0.#} 应离窗口右缘留 ≥20px（列表滚动条贴卡内缘属有意设计）");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// 推进 headless 渲染/动画时钟直到条件满足（进场过渡 180ms 落定用）。
    /// 与 ScrollBarOverlayTests.PumpUntil 同源手法但方向不同：那边等闲置计时器（先制造渲染失效），
    /// 这边等 RequestAnimationFrame 驱动的过渡动画（直接推帧即可），勿合并。
    /// </summary>
    private static void PumpUntilSettled(Window window, Func<bool> done, int maxIterations = 300)
    {
        for (var i = 0; i < maxIterations && !done(); i++)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
    }
}
