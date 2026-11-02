using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Headless.Tests.Controls;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Share;

/// <summary>
/// 导出管线（P4）：文件名规则是纯函数；渲染是 2× 光栅化，必须对已挂树、已布局的海报根节点做——
/// 对悬空控件渲染会得到空白。
/// </summary>
public class SharePosterExporterTests
{
    private static SharePosterRequest Request(DateOnly start, DateOnly end) =>
        new(SharePosterLayout.Portrait, SharePosterTheme.Dark, SharePosterProjectMask.Off, start, end);

    [Fact]
    public void File_name_for_a_single_day_has_one_date()
    {
        var day = new DateOnly(2026, 10, 17);
        Assert.Equal("tokenmetry-20261017.png", SharePosterExporter.FileName(Request(day, day)));
    }

    [Fact]
    public void File_name_for_a_range_has_both_dates()
    {
        var request = Request(new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2));
        Assert.Equal("tokenmetry-20260128-20261102.png", SharePosterExporter.FileName(request));
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task Clipboard_round_trip_returns_the_same_bitmap()
    {
        // 位图生命周期回归锁：剪贴板在 Windows/X11 上是延迟提供，调用返回即 Dispose 会让粘贴为空。
        // headless 剪贴板读回路径能抓到「贴出去的是空位图」这类错误。
        var window = new Window { Width = 64, Height = 64 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var clipboard = window.Clipboard;
        Assert.NotNull(clipboard);

        using var source = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize(32, 16), new Vector(96, 96));
        var ok = await SharePosterExporter.CopyToClipboardAsync(clipboard, source);

        Assert.True(ok, "headless 剪贴板应支持位图");
        var pasted = await clipboard!.TryGetBitmapAsync();
        Assert.NotNull(pasted);
        Assert.Equal(32, pasted!.PixelSize.Width);
        Assert.Equal(16, pasted.PixelSize.Height);

        window.Close();
    }

    [AvaloniaFact]
    public void Single_day_poster_exports_at_the_tray_size()
    {
        // 托盘卡片按内容高度排（竖直方向不铺满宿主），导出尺寸必须等于托盘根的实际版面
        var day = new DateOnly(2026, 10, 17);
        var data = SharePosterSnapshot.Build(
            Request(day, day),
            ChartTestHelpers.DailyPoints30(),
            [new DimensionUsageRow { Key = "ZCode", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
            [new DimensionUsageRow { Key = "glm-5.3-flash", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
            [new DimensionUsageRow { Key = "Zearth", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
            sessionCount: 12);

        var view = new SharePosterView { DataContext = new SharePosterViewModel(data) };
        var window = new Window
        {
            Width = 1120,
            Height = 1700,
            Content = view,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = Brushes.Black,
        };
        using var frame = ChartTestHelpers.RenderFrame(window);

        var root = (Border)view.PosterRootControl;
        Assert.True(root.Name == "TrayRoot", "单日导出应对托盘根光栅化");
        Assert.True(root.Bounds.Height < 1200, $"托盘卡片应按内容高度排版，实测 {root.Bounds.Height}");
        using var bitmap = SharePosterExporter.Render(root);
        Assert.Equal((int)Math.Round(root.Bounds.Width * 2), bitmap.PixelSize.Width);
        Assert.Equal((int)Math.Round(root.Bounds.Height * 2), bitmap.PixelSize.Height);

        window.Close();
    }

    [AvaloniaFact]
    public void Render_produces_a_two_x_bitmap_with_visible_content()
    {
        var data = SharePosterSnapshot.Build(
            Request(new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2)),
            ChartTestHelpers.DailyPoints30(),
            [new DimensionUsageRow { Key = "ZCode", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
            [new DimensionUsageRow { Key = "glm-5.3-flash", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
            [new DimensionUsageRow { Key = "Zearth", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
            sessionCount: 12);

        var view = new SharePosterView { DataContext = new SharePosterViewModel(data) };
        var window = new Window
        {
            Width = 1120,
            Height = 1700,
            Content = view,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = Brushes.Black,
        };
        using var frame = ChartTestHelpers.RenderFrame(window);

        var root = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PosterRoot");
        using var bitmap = SharePosterExporter.Render(root);

        Assert.Equal((int)Math.Round(root.Bounds.Width * 2), bitmap.PixelSize.Width);
        Assert.Equal((int)Math.Round(root.Bounds.Height * 2), bitmap.PixelSize.Height);

        // 不是纯色填充：抽样统计不同颜色数（海报底色不透明，用"不透明像素"判会永远通过）
        var pixels = ChartTestHelpers.GetPixelBytes(bitmap);
        var distinct = new System.Collections.Generic.HashSet<int>();
        for (var i = 0; i + 3 < pixels.Length; i += 4 * 97)
        {
            distinct.Add((pixels[i] << 24) | (pixels[i + 1] << 16) | (pixels[i + 2] << 8) | pixels[i + 3]);
        }

        Assert.True(distinct.Count > 50, $"导出位图应含文字与图表内容，实测不同颜色仅 {distinct.Count} 种");

        window.Close();
    }
}
