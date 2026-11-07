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
    private static SharePosterRequest Request(DateOnly start, DateOnly end, SharePosterStyle? style = null) =>
        new(SharePosterLayout.Portrait, SharePosterTheme.Dark, SharePosterProjectMask.Off, start, end,
            style ?? (start == end ? SharePosterStyle.Tray : SharePosterStyle.Overview));

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

    [AvaloniaFact]
    public void Heatmap_poster_exports_at_landscape_size()
    {
        // 打卡热力图（样式 A）固定 1800 横版：能力表只登记横版，导出按热力根 2× 光栅化
        var data = SharePosterSnapshot.Build(
            new SharePosterRequest(
                SharePosterLayout.Landscape, SharePosterTheme.Dark, SharePosterProjectMask.Off,
                new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2), SharePosterStyle.Heatmap),
            ChartTestHelpers.DailyPoints30(),
            [new DimensionUsageRow { Key = "ZCode", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
            [new DimensionUsageRow { Key = "glm-5.3-flash", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
            [new DimensionUsageRow { Key = "Zearth", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
            sessionCount: 12);

        var view = new SharePosterView { DataContext = new SharePosterViewModel(data) };
        var window = new Window
        {
            Width = 1900,
            Height = 1000,
            Content = view,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = Brushes.Black,
        };
        using var frame = ChartTestHelpers.RenderFrame(window);

        var root = (Border)view.PosterRootControl;
        Assert.True(root.Name == "HeatRoot", "热力图应对热力根光栅化");
        using var bitmap = SharePosterExporter.Render(root);

        Assert.Equal((int)Math.Round(root.Bounds.Width * 2), bitmap.PixelSize.Width);
        Assert.Equal((int)Math.Round(root.Bounds.Height * 2), bitmap.PixelSize.Height);
        Assert.Equal(SharePosterView.LandscapeWidth * 2, bitmap.PixelSize.Width);

        // 色阶格子真的画出来了：抽样应见到多个不同颜色
        var pixels = ChartTestHelpers.GetPixelBytes(bitmap);
        var distinct = new System.Collections.Generic.HashSet<int>();
        for (var i = 0; i + 3 < pixels.Length; i += 4 * 97)
        {
            distinct.Add((pixels[i] << 24) | (pixels[i + 1] << 16) | (pixels[i + 2] << 8) | pixels[i + 3]);
        }

        Assert.True(distinct.Count > 50, $"热力图导出应含格子与文字，实测不同颜色仅 {distinct.Count} 种");

        window.Close();
    }

    [AvaloniaFact]
    public void Light_poster_export_matches_its_shadowless_reference()
    {
        // 回归锁：Border.BoxShadow 在 RenderTargetBitmap.Render 的立即渲染路径下，会把「泄漏的
        // 缩放变换」带给其后绘制的兄弟内容——首个带影卡片之后整张海报被放大错位（v0.4.0 的日间
        // 导出即中招，只因既有导出测试全部钉在 Dark、投影令牌透明而未被发现）。投影改走
        // DropShadowEffect 后，带影导出必须与无影参考基本一致（差异只允许落在卡片边缘的投影晕圈）。
        foreach (var style in new[] { SharePosterStyle.Tray, SharePosterStyle.Overview, SharePosterStyle.Heatmap })
        {
            var data = SharePosterSnapshot.Build(
                Request(new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2), style),
                ChartTestHelpers.DailyPoints30(),
                [new DimensionUsageRow { Key = "ZCode", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
                [new DimensionUsageRow { Key = "glm-5.3-flash", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
                [new DimensionUsageRow { Key = "Zearth", TotalTokens = 1_000_000, Cost = new AggregatedCost(1m, false) }],
                sessionCount: 12);

            var view = new SharePosterView { DataContext = new SharePosterViewModel(data) };
            var window = new Window
            {
                Width = 1900,
                Height = 1400,
                Content = view,
                RequestedThemeVariant = ThemeVariant.Light,
                Background = Brushes.Black,
            };
            using var frame = ChartTestHelpers.RenderFrame(window);

            var root = (Border)view.PosterRootControl;
            using var withShadow = SharePosterExporter.Render(root);
            foreach (var border in view.GetVisualDescendants().OfType<Border>()
                         .Where(b => b.Effect is not null).ToArray())
            {
                border.Effect = null;
            }

            using var withoutShadow = SharePosterExporter.Render(root);

            Assert.Equal(withoutShadow.PixelSize, withShadow.PixelSize);
            var a = ChartTestHelpers.GetPixelBytes(withShadow);
            var b = ChartTestHelpers.GetPixelBytes(withoutShadow);
            var differing = 0;
            for (var i = 0; i + 3 < a.Length; i += 4)
            {
                if (Math.Abs(a[i] - b[i]) > 8 || Math.Abs(a[i + 1] - b[i + 1]) > 8 || Math.Abs(a[i + 2] - b[i + 2]) > 8)
                {
                    differing++;
                }
            }

            var ratio = differing / (double)(a.Length / 4);
            Assert.True(
                ratio < 0.2,
                $"[{style}] 日间带影导出与无影参考差异 {ratio:P1}——投影渲染再次破坏了内容布局");

            window.Close();
        }
    }
}
