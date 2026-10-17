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

    [Fact]
    public void AnchorBelow_puts_card_under_icon_right_aligned_and_clamps()
    {
        var anchor = new PixelRect(1840, 8, 22, 22); // 菜单栏图标（interop frame，屏幕坐标系）
        var cardSize = new Size(438, 600);
        var workArea = new PixelRect(0, 25, 1920, 1055); // menu bar 已扣

        var pos = PopoverPositioner.AnchorBelow(anchor, cardSize, workArea, gap: 4, rightOverlap: 8);

        // 未越界：卡片顶缘 = 图标底缘 + 4；右缘越过图标右缘 8
        Assert.Equal(1840 + 22 - 438 + 8, pos.X);
        Assert.Equal(8 + 22 + 4, pos.Y);

        // 贴屏幕右缘 → 右缘不越出工作区
        var clamped = PopoverPositioner.AnchorBelow(
            new PixelRect(1910, 8, 22, 22), cardSize, workArea, 4, 8);
        Assert.Equal(1920 - 438, clamped.X);
    }

    [Fact]
    public void Cocoa_frame_converts_to_avalonia_top_down_coords()
    {
        // 主屏 1440x900（Cocoa frame origin (0,0)），菜单栏图标窗口在顶部：
        // cocoa frame y=878 h=22（顶缘 900）→ Avalonia top = 900 - 900 = 0
        var converted = MacTrayInterop.ConvertCocoaFrameToAvaloniaCoords(100, 878, 22, 22, primaryScreenCocoaMaxY: 900);
        Assert.Equal(0, converted.Y);
        Assert.Equal(100, converted.X);
        Assert.Equal(22, converted.Width);
        Assert.Equal(22, converted.Height);

        // 纵轴翻转：cocoa 越靠下（y 小）→ avalonia 越靠下（y 大）
        var lower = MacTrayInterop.ConvertCocoaFrameToAvaloniaCoords(100, 400, 22, 22, 900);
        Assert.True(lower.Y > converted.Y);
    }
}
