using System;
using Avalonia;

namespace Tokenmetry.App.Tray;

/// <summary>
/// 弹窗锚定纯函数（D-016）：全部入参在<b>同一屏幕坐标系</b>（Avalonia Screen.Bounds / Window.Position
/// 的同一单位——Windows 为物理像素、macOS 为 Cocoa 点，两者平台内自洽；调用方负责用
/// <c>screen.Scaling</c> 把窗口 DIP 尺寸换算进该空间）。窗口含隐形投影边距，钳制以整个窗口矩形
/// 对工作区做界即可——可见卡片落在边距内侧，天然不出屏。纯静态，headless 直测。
/// </summary>
internal static class PopoverPositioner
{
    /// <summary>macOS 近似锚定（卡定「零 interop 先行」）：主屏工作区（menu bar 之下）右上角，
    /// 贴近状态栏图标区——锚点与真实图标位置偏差由 TC-003 真机目检把关，不可接受再上最小 interop。</summary>
    public static PixelPoint AnchorTopRight(PixelRect workingArea, Size windowSize, double gapFromRight, double gapFromTop)
    {
        var x = workingArea.X + workingArea.Width - windowSize.Width - gapFromRight;
        var y = workingArea.Y + gapFromTop;
        return ClampToArea(new PixelPoint((int)Math.Round(x), (int)Math.Round(y)), workingArea, windowSize);
    }

    /// <summary>Windows 托盘 flyout：卡片底缘在光标上方 gapBelow（点击瞬间光标在托盘图标上）、
    /// 右缘越过光标 rightOverlap（托盘贴屏幕右缘，弹层向左展开），越界钳回工作区。</summary>
    public static PixelPoint AnchorAboveCursor(PixelRect workingArea, Size windowSize, PixelPoint cursor, double gapBelow, double rightOverlap)
    {
        var x = cursor.X + rightOverlap - windowSize.Width;
        var y = cursor.Y - windowSize.Height - gapBelow;
        return ClampToArea(new PixelPoint((int)Math.Round(x), (int)Math.Round(y)), workingArea, windowSize);
    }

    /// <summary>macOS 精确锚定（interop 图标 frame）：右弹优先（B4，NSPopover 系惯例）——
    /// 卡片左缘贴图标左缘、顶缘在图标正下方 gap，向右下展开；右缘越出工作区才整体左移、
    /// 右缘贴工作区右缘（ClampToArea 语义），超宽再钳左缘 ≥ 工作区左。入参为<b>可见卡片</b>矩形
    /// （不含投影边距——调用方把返回值左移/上移边距得到窗口位置）。</summary>
    public static PixelPoint AnchorBelow(PixelRect anchor, Size cardSize, PixelRect workingArea, double gap)
    {
        var x = anchor.X; // 左缘直取（int，无舍入）
        var y = (int)Math.Round(anchor.Y + anchor.Height + gap);
        return ClampToArea(new PixelPoint(x, y), workingArea, cardSize);
    }

    private static PixelPoint ClampToArea(PixelPoint point, PixelRect area, Size size)
    {
        var minX = area.X;
        var minY = area.Y;
        var maxX = Math.Max(minX, area.X + area.Width - (int)Math.Ceiling(size.Width));
        var maxY = Math.Max(minY, area.Y + area.Height - (int)Math.Ceiling(size.Height));
        var x = Math.Clamp(point.X, minX, maxX);
        var y = Math.Clamp(point.Y, minY, maxY);
        return new PixelPoint(x, y);
    }
}
