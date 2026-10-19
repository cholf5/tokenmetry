using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tokenmetry.App.Styles;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Controls;

/// <summary>
/// Motion 淡入落定契约（回归锁：用户实测 DonutChart 中心数字扫入后消失——Avalonia 动画
/// 完成后属性回落到本地值，本地值必须落在终态 1；stagger 延迟窗内必须由 FillMode 压住起帧 0）。
/// </summary>
public class MotionTests
{
    [AvaloniaFact]
    public void Run_fade_in_lands_base_opacity_one_after_completion()
    {
        var border = new Border();
        var window = new Window { Width = 200, Height = 100, Content = border };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Motion.RunFadeIn(border, durationMs: 1, delayMs: 0);

        // 1ms 动画在 headless 动画钟下几帧内完成；完成后有效值应回落到本地终值 1（而非 0）
        for (var i = 0; i < 100 && border.Opacity < 0.999; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Assert.True(border.Opacity > 0.999,
            $"淡入完成后应稳定在 1，实测 {border.Opacity:0.###}——本地值停在起帧 0（动画值回落即消失）");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Run_fade_in_holds_zero_during_delay_window()
    {
        var border = new Border();
        var window = new Window { Width = 200, Height = 100, Content = border };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 落定契约为本地值 1；延迟窗内必须由 FillMode 压住起帧 0——否则 stagger 里未轮到的
        // 元素会以基值 1 提前闪现，动画开始时再跳回 0
        Motion.RunFadeIn(border, durationMs: 120, delayMs: 2000);
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(border.Opacity < 0.001,
            $"延迟窗内应保持起帧 0（FillMode），实测 {border.Opacity:0.###}——基值 1 会提前闪现");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
