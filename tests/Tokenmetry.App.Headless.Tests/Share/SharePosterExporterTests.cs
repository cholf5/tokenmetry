using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
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
