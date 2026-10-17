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
