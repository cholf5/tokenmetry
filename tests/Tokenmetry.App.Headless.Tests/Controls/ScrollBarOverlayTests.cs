using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Controls;

/// <summary>
/// 全局 overlay 滚动条契约（Styles/ScrollBarTheme.axaml + Controls/ScrollBarOverlay）：
/// 1) 闲置完全隐藏（overlay-shown 类关闭、不参与命中——悬浮条不挡贴边内容）；
/// 2) 模板无上下箭头（PART_LineUp/DownButton 不复存在）；
/// 3) 滑块贴滚动视口右缘（垂直）/底缘（水平），ScrollBar 本体贴视口边缘；
/// 4) 滚动值变化即显、闲置延迟后隐；
/// 5) 显示来源二 = 指针移入滚动条邻近区（命中条外扩）即显，内容区中部活动不显示；
///    悬停滑块（准备抓握）时钉住不隐。
/// 注意：Window 内容默认居中，指针坐标一律经 TranslatePoint 换算，不假设 sv 在 (0,0)。
/// 时序约定：断言"显示态稳定"先用长闲置窗，断言"收回"再注入 1ms——显示淡入 120ms，
/// 1ms 窗会在 RunJobs 内抢先收回（闲置竞态），不能用同一窗同时断言两件事。
/// 断言一律语言无关（类/部件名/几何），不依赖明暗与文案。
/// </summary>
public class ScrollBarOverlayTests
{
    private static readonly TimeSpan IdleHideDelayMs1 = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan IdleHideDelayHold = TimeSpan.FromSeconds(60);

    [AvaloniaFact]
    public void Idle_bar_is_hidden_without_arrow_buttons()
    {
        var (window, _, bar) = HostVertical();

        // 闲置（无滚动、无指针活动）：类关闭、透明、不参与命中
        Assert.DoesNotContain("overlay-shown", bar.Classes);
        Assert.True(bar.Opacity < 0.01, $"闲置应立即落在隐藏态（不产生 1→0 创建动画闪烁），实测 {bar.Opacity:0.###}");
        Assert.False(bar.IsHitTestVisible);

        // 无上下箭头：Line 按钮从模板消失（页按钮/滑块保留）
        var names = bar.GetVisualDescendants().OfType<Control>().Select(c => c.Name).ToList();
        Assert.DoesNotContain("PART_LineUpButton", names);
        Assert.DoesNotContain("PART_LineDownButton", names);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Vertical_thumb_is_flush_to_viewport_right_edge()
    {
        var (window, sv, bar) = HostVertical();

        // 滑块右缘 = ScrollBar 右缘 = 滚动视口右缘（贴边，无空隙）
        var thumb = Assert.IsType<Thumb>(bar.GetVisualDescendants().First(c => c is Thumb));
        var thumbRightInBar = thumb.TranslatePoint(new Point(thumb.Bounds.Width, thumb.Bounds.Height), bar)!.Value.X;
        Assert.Equal(bar.Bounds.Width, thumbRightInBar, 1);

        var barRightInSv = bar.TranslatePoint(new Point(bar.Bounds.Width, 0), sv)!.Value.X;
        Assert.Equal(sv.Bounds.Width, barRightInSv, 1);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Horizontal_thumb_is_flush_to_viewport_bottom_edge()
    {
        // Avalonia 的 ScrollViewer 默认 HorizontalScrollBarVisibility=Disabled（与 Vertical 的 Auto 不同），
        // 显式开启才会出现横向滚动条
        var sv = new ScrollViewer
        {
            Width = 200,
            Height = 100,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 400, Height = 400, Background = Brushes.Transparent },
        };
        var window = new Window { Width = 300, Height = 200, Content = sv };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bar = Assert.IsType<ScrollBar>(sv.GetVisualDescendants().First(c =>
            c is ScrollBar { Orientation: Orientation.Horizontal }));
        ScrollBarOverlay.SetIdleHideDelay(bar, IdleHideDelayMs1);

        var thumb = Assert.IsType<Thumb>(bar.GetVisualDescendants().First(c => c is Thumb));
        var thumbBottomInBar = thumb.TranslatePoint(new Point(thumb.Bounds.Width, thumb.Bounds.Height), bar)!.Value.Y;
        Assert.Equal(bar.Bounds.Height, thumbBottomInBar, 1);

        var barBottomInSv = bar.TranslatePoint(new Point(0, bar.Bounds.Height), sv)!.Value.Y;
        Assert.Equal(sv.Bounds.Height, barBottomInSv, 1);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Scrolling_shows_bar_then_idle_hides_it()
    {
        var (window, sv, bar) = HostVertical();

        // 长闲置窗下验证"滚动即显 + 淡入落定"
        ScrollBarOverlay.SetIdleHideDelay(bar, IdleHideDelayHold);
        sv.Offset = new Vector(0, 60);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("overlay-shown", bar.Classes);

        PumpUntil(window, () => bar.Opacity > 0.999);
        Assert.True(bar.Opacity > 0.999, $"显示态应淡入落定 1，实测 {bar.Opacity:0.###}");

        // 注入 1ms 闲置窗并制造新活动（重挂计时器）→ 收回
        ScrollBarOverlay.SetIdleHideDelay(bar, IdleHideDelayMs1);
        sv.Offset = new Vector(0, 61);
        PumpUntil(window, () => !bar.Classes.Contains("overlay-shown"));
        Assert.DoesNotContain("overlay-shown", bar.Classes);
        PumpUntil(window, () => bar.Opacity < 0.01);
        Assert.True(bar.Opacity < 0.01, $"隐藏态应淡出落定 0，实测 {bar.Opacity:0.###}");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Pointer_activity_far_from_bar_does_not_show_it()
    {
        var (window, sv, bar) = HostVertical();

        // 长闲置窗下指针在内容区中部活动（远离右缘命中区）：不显示
        ScrollBarOverlay.SetIdleHideDelay(bar, IdleHideDelayHold);
        var svOrigin = sv.TranslatePoint(new Point(0, 0), window)!.Value;
        window.MouseMove(new Point(svOrigin.X + 50, svOrigin.Y + 25));
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(new Point(svOrigin.X + 120, svOrigin.Y + 60));
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("overlay-shown", bar.Classes);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Pointer_nearing_bar_shows_it_then_idle_hides_it()
    {
        var (window, sv, bar) = HostVertical();

        // 长闲置窗下指针移入命中区（10px 条 + 24px 邻近窗，自视口右缘向内 34px）：
        // 取条左侧 20px 处——区内、条外，验证"接近即显"不依赖直接命中
        ScrollBarOverlay.SetIdleHideDelay(bar, IdleHideDelayHold);
        var svOrigin = sv.TranslatePoint(new Point(0, 0), window)!.Value;
        var nearX = svOrigin.X + sv.Bounds.Width - bar.Bounds.Width - 20;
        window.MouseMove(new Point(nearX, svOrigin.Y + 50));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("overlay-shown", bar.Classes);

        // 注入 1ms 闲置窗并制造新活动（重挂计时器）→ 收回
        // （指针停在命中区内、条外，钉住条件 IsPointerOver 不成立，闲置到期即隐）
        ScrollBarOverlay.SetIdleHideDelay(bar, IdleHideDelayMs1);
        sv.Offset = new Vector(0, 61);
        PumpUntil(window, () => !bar.Classes.Contains("overlay-shown"));
        Assert.DoesNotContain("overlay-shown", bar.Classes);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hovering_bar_pins_it_shown_past_idle()
    {
        var (window, sv, bar) = HostVertical();

        // 长闲置窗下把指针移到命中条上（右缘 10px 条）
        ScrollBarOverlay.SetIdleHideDelay(bar, IdleHideDelayHold);
        sv.Offset = new Vector(0, 60);
        Dispatcher.UIThread.RunJobs();
        // Window 内容默认居中：滚动条在窗口内的实际位置必须经 TranslatePoint 求，不能假设 (0,0) 起。
        // 指针移到命中条上（右缘 10px 条内、避开两端）
        var barOrigin = bar.TranslatePoint(new Point(0, 0), window)!.Value;
        window.MouseMove(new Point(barOrigin.X + 7, barOrigin.Y + 50));
        Dispatcher.UIThread.RunJobs();
        Assert.True(bar.IsPointerOver, "命中条在显示态可命中，指针应位于其上");

        // 悬停期间注入 1ms 闲置窗并制造新活动：到期轮询必须因指针在条上而钉住
        ScrollBarOverlay.SetIdleHideDelay(bar, IdleHideDelayMs1);
        sv.Offset = new Vector(0, 61);
        PumpUntil(window, () => !bar.Classes.Contains("overlay-shown"), maxIterations: 40);
        // 悬停滚动条期间应钉住显示态
        Assert.Contains("overlay-shown", bar.Classes);

        // 指针移出滚动区后才允许收回（窗口右下角，远离 sv）
        window.MouseMove(new Point(window.Width - 5, window.Height - 5));
        PumpUntil(window, () => !bar.Classes.Contains("overlay-shown"));
        Assert.DoesNotContain("overlay-shown", bar.Classes);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>竖向溢出的标准宿主：ScrollViewer 200×100 内容 400 高，闲置窗注入 1ms。</summary>
    private static (Window Window, ScrollViewer Viewer, ScrollBar Bar) HostVertical()
    {
        var sv = new ScrollViewer
        {
            Width = 200,
            Height = 100,
            Content = new Border { Height = 400, Background = Brushes.Transparent },
        };
        var window = new Window { Width = 300, Height = 200, Content = sv };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bar = Assert.IsType<ScrollBar>(sv.GetVisualDescendants().First(c =>
            c is ScrollBar { Orientation: Orientation.Vertical }));
        ScrollBarOverlay.SetIdleHideDelay(bar, IdleHideDelayMs1);
        return (window, sv, bar);
    }

    /// <summary>
    /// 推进 headless 调度/渲染时钟直到条件满足；1ms 闲置窗在真实时钟 + 帧推进下必然到期。
    /// headless 陷阱（2026-10-23 Windows 冒烟定位）：headless 平台在渲染空闲后不再推进
    /// DispatcherTimer 时钟——只 RunJobs + ForceRenderTimerTick 而不制造渲染失效时，
    /// Background 优先级的闲置计时器一次都不会 tick，看起来像控件"收回失败"，实为测试泵
    /// 没喂渲染帧；产品里的 DispatcherTimer 在真实桌面由平台调度/渲染循环驱动，无此问题。
    /// 故每轮先 InvalidateVisual 制造帧需求再推进。与 Shell/DispatcherPump 的异步加载等待
    /// 泵语义不同（那边等线程池续体，不需要帧），勿合并。
    /// </summary>
    private static void PumpUntil(Visual frameSource, Func<bool> done, int maxIterations = 300)
    {
        for (var i = 0; i < maxIterations && !done(); i++)
        {
            frameSource.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(1);
        }
    }
}
