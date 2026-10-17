using Avalonia;
using Avalonia.Platform;
using Tokenmetry.App.Tray;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// PopoverPositioner 纯函数（D-016）：屏幕坐标系内的锚定与钳制（单位无关——入参已在同一空间，
/// Windows 物理像素 / macOS Cocoa 点皆适用）。无 Avalonia 平台依赖（纯 struct 数学），普通 Fact。
/// </summary>
public class PopoverPositionerTests
{
    [Fact]
    public void AnchorTopRight_puts_window_below_menubar_at_right_edge()
    {
        var area = new PixelRect(0, 0, 1440, 900); // 工作区（menu bar 已扣）
        var size = new Size(498, 600);

        var pos = PopoverPositioner.AnchorTopRight(area, size, gapFromRight: 8, gapFromTop: 6);

        Assert.Equal(1440 - 498 - 8, pos.X);
        Assert.Equal(6, pos.Y);
    }

    [Fact]
    public void AnchorAboveCursor_sits_above_cursor_and_clamps_into_work_area()
    {
        var area = new PixelRect(0, 0, 1920, 1040); // Windows：任务栏已扣
        var size = new Size(498, 600);
        var cursor = new PixelPoint(1880, 1010); // 右下角托盘区

        var pos = PopoverPositioner.AnchorAboveCursor(area, size, cursor, gapBelow: 10, rightOverlap: 14);

        // 未越界时：右缘越过光标 14px，底缘在光标上方 10px
        Assert.Equal(1880 + 14 - 498, pos.X);
        Assert.Equal(1010 - 600 - 10, pos.Y);

        // 越界钳回：光标贴屏幕右缘 → 窗口右缘不超过工作区
        var clamped = PopoverPositioner.AnchorAboveCursor(
            new PixelRect(0, 0, 1920, 1040), new Size(498, 600), new PixelPoint(1918, 1010), 10, 14);
        Assert.Equal(1920 - 498, clamped.X);

        // 贴底（矮工作区）→ 底缘不越过工作区底
        var shortArea = new PixelRect(0, 0, 1920, 300);
        var clampedY = PopoverPositioner.AnchorAboveCursor(shortArea, size, new PixelPoint(960, 290), 10, 14);
        Assert.Equal(0, clampedY.Y);
    }

    [Fact]
    public void AnchorTopRight_clamps_oversized_window()
    {
        // 窗口比工作区还宽 → 钳到左上角（不越出）
        var pos = PopoverPositioner.AnchorTopRight(new PixelRect(0, 0, 400, 300), new Size(498, 600), 8, 6);
        Assert.Equal(0, pos.X);
        Assert.Equal(0, pos.Y);
    }
}
