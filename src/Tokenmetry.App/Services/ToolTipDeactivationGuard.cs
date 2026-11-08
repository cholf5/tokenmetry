using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Tokenmetry.App.Services;

/// <summary>
/// ToolTip 失活守卫：mac 上 Avalonia 的悬浮 Tip 是独立 NSWindow，层级高于普通窗口——
/// app 一失活（切到别的 App），macOS 不再给后台进程派发鼠标事件，ToolTip 赖以关闭的
/// 「指针离开」永远等不到，弹层就钉死在屏幕上、压在所有 App 之前（Avalonia #6677 一族，
/// 12.1.4 未修）。两个补丁点：
/// <list type="number">
/// <item>宿主窗口未激活时拦下 ToolTipOpening——盖住「悬停后 Cmd+Tab 切走，show-delay 定时器
/// 在失活后才到期」的竞态：定时器照常触发，但取消后 Avalonia 直接把 IsOpen 翻回 false，
/// 弹层从未创建（IsOpenChanged 在 Open 之前就 return），不存在闪一下。</item>
/// <item>窗口 Deactivated / Closing 时关闭窗口树内已打开的 Tip——盖住「Tip 开着时切走」
/// 与「关到托盘前 Tip 还开着」。只关 ToolTip 弹层，不碰窗口可见性，StatusPopoverWindow 里
/// 与 B-017 的失焦收起接线互不干扰。</item>
/// </list>
/// 边界：挂在弹层（PopupRoot）内部的 ToolTip 不在窗口可视树里，两条防线都覆盖不到——目前无此用法。
/// </summary>
public static class ToolTipDeactivationGuard
{
    private static int _installed;

    /// <summary>注册全局 ToolTipOpening class handler（App 启动时装一次；Attach 会兜底补装）。</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return;
        }

        // ToolTipOpening 是 Direct 路由、在宿主控件上 raise（Avalonia ToolTip.IsOpenChanged），
        // class handler 对任意控件的 ToolTip 都生效。只拦「宿主是窗口且未激活」：PopupRoot
        // （弹层里的 ToolTip）与尚未入树的控件不在此列，正常悬浮不受影响。
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((sender, e) =>
        {
            if (TopLevel.GetTopLevel(sender) is Window { IsActive: false })
            {
                e.Cancel = true;
            }
        });
    }

    /// <summary>给窗口接上「失活/关窗即关 Tip」防线（各 Window 构造函数里调用一次）。</summary>
    public static void Attach(Window window)
    {
        Install();
        window.Deactivated += (_, _) => CloseOpenTooltips(window);
        window.Closing += (_, _) => CloseOpenTooltips(window);
    }

    /// <summary>关闭 <paramref name="root"/> 可视树内所有打开中的 ToolTip（headless 测试接缝）。</summary>
    internal static void CloseOpenTooltips(TopLevel root)
    {
        if (ToolTip.GetIsOpen(root))
        {
            ToolTip.SetIsOpen(root, false);
        }

        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (ToolTip.GetIsOpen(control))
            {
                ToolTip.SetIsOpen(control, false);
            }
        }
    }
}
