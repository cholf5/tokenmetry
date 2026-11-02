using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Headless.Tests.Controls;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Share;

/// <summary>
/// 分享海报视图（P3）：海报是静态产物，最关键的两条是——图能被渲染出来（不是空壳），
/// 以及两个会"骗过人"的地方确实被关掉了：环形图的扫入动画、趋势图的 hover 依赖。
/// </summary>
public class SharePosterViewTests
{
    private static DimensionUsageRow Row(string key, long total, decimal? cost = 0m) => new()
    {
        Key = key,
        InputTokens = total / 10,
        OutputTokens = total / 100,
        CacheReadTokens = total - total / 10 - total / 100,
        TotalTokens = total,
        Cost = new AggregatedCost(cost, hasUnknownCost: cost is null),
        TokenShare = null,
    };

    private static SharePosterViewModel ViewModel(SharePosterLayout layout = SharePosterLayout.Portrait,
        SharePosterTheme theme = SharePosterTheme.Dark)
    {
        var request = new SharePosterRequest(
            layout, theme, SharePosterProjectMask.Off, new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2));

        var daily = ChartTestHelpers.DailyPoints30();

        var data = SharePosterSnapshot.Build(
            request,
            daily,
            [Row("ZCode", 4_535_388_126, 200m), Row("Codex", 366_647_795, null)],
            [Row("glm-5.3-flash", 2_383_429_663, 80m), Row("gpt-5.3-codex", 345_048_948, 90m)],
            [Row("Zearth", 784_282_344, 10m), Row("sven-net", 518_709_985, 5m)],
            sessionCount: 764);

        return new SharePosterViewModel(data);
    }

    private static Window Host(SharePosterView view) => new()
    {
        Width = 1120,
        Height = 1700,
        Content = view,
        RequestedThemeVariant = ThemeVariant.Dark,
        Background = Brushes.Black,
    };

    [AvaloniaFact]
    public void Poster_renders_at_poster_width_with_both_charts_annotated()
    {
        var view = new SharePosterView { DataContext = ViewModel() };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        // UserControl 被窗口拉伸，真正的版面宽度在 PosterRoot 上（导出按它光栅化）
        var root = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PosterRoot");
        Assert.Equal(SharePosterView.PortraitWidth, root.Bounds.Width, 1);

        var charts = view.GetVisualDescendants().OfType<TrendChart>().ToList();
        Assert.Equal(2, charts.Count);
        // 海报没有 hover：两张图都必须自带峰值标注
        Assert.All(charts, c => Assert.False(string.IsNullOrEmpty(c.PeakAnnotationText)));

        window.Close();
    }

    [AvaloniaFact]
    public void Landscape_renders_1800_wide_with_two_rows_of_three_panels()
    {
        var view = new SharePosterView
        {
            DataContext = ViewModel(layout: SharePosterLayout.Landscape),
        };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var root = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PosterRoot");
        Assert.Equal(SharePosterView.LandscapeWidth, root.Bounds.Width, 1);

        // 横版不是竖版的缩放：两行三列，类型环形从第二行提到第一行、成本趋势并入第二行
        var grid = view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "PanelGrid");
        Assert.Equal(3, grid.ColumnDefinitions.Count);
        Assert.Equal(2, grid.RowDefinitions.Count);

        var panels = view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("poster-panel"))
            .ToList();
        Assert.Equal(6, panels.Count);
        Assert.All(panels, p => Assert.True(Grid.GetRow(p) <= 1, "横版只应有前两行有面板"));
        Assert.Equal(3, panels.Count(p => Grid.GetRow(p) == 0));
        Assert.Equal(3, panels.Count(p => Grid.GetRow(p) == 1));
        Assert.Equal(2, panels.Count(p => Grid.GetColumn(p) == 2));

        // 图表仍按海报尺寸渲染（高度走横版样式组，不是竖版的 240）
        var charts = view.GetVisualDescendants().OfType<TrendChart>().ToList();
        Assert.All(charts, c => Assert.Equal(250, c.Bounds.Height, 0));
        Assert.All(view.GetVisualDescendants().OfType<DonutChart>(), d => Assert.Equal(168, d.DonutSize, 0));

        window.Close();
    }

    [AvaloniaFact]
    public void Donuts_render_final_arcs_without_sweep_animation()
    {
        var view = new SharePosterView { DataContext = ViewModel() };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var donuts = view.GetVisualDescendants().OfType<DonutChart>().ToList();
        Assert.Equal(2, donuts.Count);
        Assert.All(donuts, d => Assert.False(d.AnimateSweep));

        // 离屏渲染时扫入动画不跑——若没关，弧段几何会停在退化状态，这里会是 0
        var arcs = donuts
            .SelectMany(d => d.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>())
            .Where(p => p.Data is not null)
            .Count(p => p.Data!.Bounds.Width > 1 && p.Data.Bounds.Height > 1);
        Assert.True(arcs >= 5, $"环形图应画出至少 5 段实弧，实测 {arcs}");

        window.Close();
    }

    [AvaloniaFact]
    public void Light_theme_request_switches_the_subtree_not_the_app()
    {
        var view = new SharePosterView { DataContext = ViewModel(theme: SharePosterTheme.Light) };
        var window = Host(view);   // 应用侧仍是 Dark
        using var frame = ChartTestHelpers.RenderFrame(window);

        var scope = view.GetVisualDescendants().OfType<ThemeVariantScope>().First();
        Assert.Equal(ThemeVariant.Light, scope.RequestedThemeVariant);

        window.Close();
    }
}
