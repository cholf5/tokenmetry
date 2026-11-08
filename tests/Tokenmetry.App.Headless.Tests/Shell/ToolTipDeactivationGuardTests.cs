using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tokenmetry.App.Services;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// ToolTip 失活守卫（ToolTipDeactivationGuard）：mac 上悬浮 Tip 是独立 NSWindow，app 失活后
/// 收不到鼠标事件、永不关闭，会钉在别的 App 之前（Avalonia #6677 一族）。两条防线都在
/// headless 宿主里可端到端验证——headless 的 Activate()/Hide() 会经 dispatcher post 触发
/// Activated/Deactivated，走的就是真实事件链路。断言只看 IsOpen 状态（语言无关纪律）。
/// </summary>
public class ToolTipDeactivationGuardTests
{
    /// <summary>阳性对照：激活窗口里 ToolTip 照常打开，守卫不误伤正常悬浮。</summary>
    [AvaloniaFact]
    public void Tooltip_Opens_WhenWindowActive()
    {
        var window = CreateGuardedWindow(out var button);
        window.Show();
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsActive);

        ToolTip.SetIsOpen(button, true);
        Assert.True(ToolTip.GetIsOpen(button));

        window.Close();
    }

    /// <summary>防线一：宿主窗口失活时，打开 ToolTip 的请求被取消——盖住「悬停后 Cmd+Tab
    /// 切走、show-delay 定时器失活后才到期」的竞态（弹层从未创建）。headless 唯一的失活
    /// 通道是 Hide()；真实场景（主窗被别的 App 盖住/切走）同样处于未激活态，守卫只看 IsActive。</summary>
    [AvaloniaFact]
    public void Tooltip_OpenCancelled_WhenWindowInactive()
    {
        var window = CreateGuardedWindow(out var button);
        window.Show();
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        window.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsActive); // 失活态（headless 经 Hide 进入；真实场景是切到别的 App）

        ToolTip.SetIsOpen(button, true);
        Assert.False(ToolTip.GetIsOpen(button));

        window.Close();
    }

    /// <summary>防线二：Tip 开着时窗口失活 → 已开的 Tip 被关掉。走真实 Deactivated 接线
    /// （headless 的 Hide() 会 post Deactivated），覆盖「开着 Tip 切走」与关到托盘两条现实路径。</summary>
    [AvaloniaFact]
    public void Tooltip_Closed_WhenGuardedWindowDeactivated()
    {
        var window = CreateGuardedWindow(out var button);
        window.Show();
        window.Activate();
        Dispatcher.UIThread.RunJobs();

        ToolTip.SetIsOpen(button, true);
        Assert.True(ToolTip.GetIsOpen(button));

        window.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.False(ToolTip.GetIsOpen(button));

        window.Close();
    }

    /// <summary>阴性对照：没接守卫的窗口失活时没人关 Tip——证明上一条测的是接线，
    /// 而不是 headless 平台在窗口 Hide 时顺手关弹层的副产物。</summary>
    [AvaloniaFact]
    public void Tooltip_StaysOpen_WhenUnguardedWindowDeactivated()
    {
        var button = new Button { Width = 40, Height = 20 };
        var window = new Window { Content = button, Width = 100, Height = 60 };
        ToolTip.SetTip(button, "tooltip");
        window.Show();
        window.Activate();
        Dispatcher.UIThread.RunJobs();

        ToolTip.SetIsOpen(button, true);
        Assert.True(ToolTip.GetIsOpen(button));

        window.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.True(ToolTip.GetIsOpen(button)); // 无守卫：没人关

        window.Close();
    }

    /// <summary>标准构造：Tip 必须先挂上（ToolTip 实例是 IsOpenChanged 走打开流程的前提），
    /// Attach 接上守卫的 Deactivated/Closing 防线。</summary>
    private static Window CreateGuardedWindow(out Button button)
    {
        button = new Button { Width = 40, Height = 20 };
        var window = new Window { Content = button, Width = 100, Height = 60 };
        ToolTip.SetTip(button, "tooltip");
        ToolTipDeactivationGuard.Attach(window);
        return window;
    }
}
