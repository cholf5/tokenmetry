using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TokenPulse.App.Controls;
using Xunit;

namespace TokenPulse.App.Headless.Tests.Controls;

/// <summary>
/// D-005 Heatmap 的 headless 验证（TC-002 热力图悬停）：
/// 1. 强度色纯函数：alpha 单调、色相保持、端点等于原型 0.12/0.9；
/// 2. 格子数 = 数据条数、列数/行高可配；
/// 3. 悬停 tooltip：有数据日 token 千分位数值、补零日"无数据"；
/// 4. 空数据占位；
/// 5. 明暗两套渲染 + 截图留档。
/// </summary>
public class HeatmapTests
{
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENPULSE_CHART_PREVIEW_DIR");

    private static Window CreateWindow(Heatmap heatmap, ThemeVariant variant) => new()
    {
        Width = 520,
        Height = 240,
        Content = heatmap,
        RequestedThemeVariant = variant,
        Background = ThemeBackground(variant),
    };

    /// <summary>窗口底 = 对应变体的 AppBackgroundBrush（截图体现真实页面底色）。</summary>
    private static IBrush ThemeBackground(ThemeVariant variant) =>
        Application.Current!.TryGetResource("AppBackgroundBrush", variant, out var bg)
            && bg is IBrush brush ? brush : new SolidColorBrush(Color.Parse("#111c2e"));

    private static IReadOnlyList<HeatmapCell> Cells35() =>
        Enumerable.Range(0, 35).Select(i => new HeatmapCell
        {
            Label = $"day-{i + 1}",
            Value = i % 7 == 3 ? 0 : (i + 1) * 100_000.0,
            HasData = i % 11 != 5, // 含补零格（HasData=false）与零用量格（HasData=true 且 0）
        }).ToList();

    private static UniformGrid CellsGridOf(Heatmap heatmap) =>
        heatmap.GetVisualDescendants().OfType<UniformGrid>().Single();

    private static List<Border> CellBorders(Heatmap heatmap) =>
        CellsGridOf(heatmap).Children.OfType<Border>().ToList();

    // ── 强度色纯函数 ──

    [AvaloniaFact]
    public void Intensity_color_alpha_is_monotonic_and_hue_preserved()
    {
        var baseColor = Color.Parse("#4c8dff"); // AppSeries0Color（原型 rgba(76,141,255,α)）
        var previousAlpha = -1;
        for (var step = 0; step <= 10; step++)
        {
            var color = Heatmap.IntensityColor(baseColor, step / 10.0);
            Assert.Equal(baseColor.R, color.R);
            Assert.Equal(baseColor.G, color.G);
            Assert.Equal(baseColor.B, color.B);
            Assert.True(color.A >= previousAlpha, $"强度 {step / 10.0} 的 alpha 应不低于前一档");
            previousAlpha = color.A;
        }

        // 端点对齐原型：0 → 0.12、1 → 0.90
        Assert.Equal((byte)Math.Round(255 * 0.12), Heatmap.IntensityColor(baseColor, 0).A);
        Assert.Equal((byte)Math.Round(255 * 0.90), Heatmap.IntensityColor(baseColor, 1).A);
    }

    [AvaloniaFact]
    public void Intensity_color_clamps_out_of_range_ratios()
    {
        var baseColor = Color.Parse("#4c8dff");
        Assert.Equal(Heatmap.IntensityColor(baseColor, 0).A, Heatmap.IntensityColor(baseColor, -1).A);
        Assert.Equal(Heatmap.IntensityColor(baseColor, 1).A, Heatmap.IntensityColor(baseColor, 2).A);
    }

    // ── 渲染：格子数 / 列数 / 尺寸 ──

    [AvaloniaFact]
    public void Renders_cell_count_with_configured_columns_and_height()
    {
        var heatmap = new Heatmap { Cells = Cells35(), Columns = 7, CellHeight = 27 };
        var window = CreateWindow(heatmap, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var borders = CellBorders(heatmap);
        Assert.Equal(35, borders.Count);
        Assert.Equal(7, CellsGridOf(heatmap).Columns);
        Assert.All(borders, b => Assert.Equal(27, b.Height));
        Assert.All(borders, b => Assert.Equal(new CornerRadius(5), b.CornerRadius));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(5)]
    [InlineData(10)]
    public void Columns_property_controls_grid(int columns)
    {
        var heatmap = new Heatmap { Cells = Cells35(), Columns = columns };
        var window = CreateWindow(heatmap, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        Assert.Equal(columns, CellsGridOf(heatmap).Columns);
        Assert.Equal(35, CellBorders(heatmap).Count);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 悬停 tooltip（TC-002）──

    [AvaloniaFact]
    public void Cell_tooltips_show_day_value_or_no_data()
    {
        var heatmap = new Heatmap { Cells = Cells35(), Columns = 7 };
        var window = CreateWindow(heatmap, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var borders = CellBorders(heatmap);
        Assert.All(borders, b => Assert.NotNull(ToolTip.GetTip(b)));

        // 有数据格：token 千分位
        var withValue = borders[0];
        Assert.Contains("100,000 tok", (string)ToolTip.GetTip(withValue)!);

        // 补零格（HasData=false）：诚实"无数据"
        var padded = borders[5];
        Assert.Contains("无数据", (string)ToolTip.GetTip(padded)!);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 强度-颜色映射落在视觉树上（同强度同色、空数据格用令牌底）──

    [AvaloniaFact]
    public void Stronger_value_cells_get_more_opaque_fill()
    {
        var cells = new[]
        {
            new HeatmapCell { Label = "low", Value = 100, HasData = true },
            new HeatmapCell { Label = "high", Value = 1000, HasData = true },
            new HeatmapCell { Label = "padded", Value = 0, HasData = false },
        };
        var heatmap = new Heatmap { Cells = cells, Columns = 3 };
        var window = CreateWindow(heatmap, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var borders = CellBorders(heatmap);
        var low = Assert.IsAssignableFrom<ISolidColorBrush>(borders[0].Background);
        var high = Assert.IsAssignableFrom<ISolidColorBrush>(borders[1].Background);
        var padded = Assert.IsAssignableFrom<ISolidColorBrush>(borders[2].Background);

        Assert.True(high.Color.A > low.Color.A, "高强度格应比低强度格更实");
        // 补零格：次面板令牌底（无强度色相），与有数据格明显区分
        Assert.True(Application.Current!.TryGetResource("AppPanelAltBrush", ThemeVariant.Dark, out var expected));
        Assert.Same(expected, padded);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 空数据 ──

    [AvaloniaFact]
    public void Empty_cells_show_placeholder()
    {
        var heatmap = new Heatmap { Cells = Array.Empty<HeatmapCell>() };
        var window = CreateWindow(heatmap, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        Assert.Empty(CellBorders(heatmap));
        var emptyText = ChartTestHelpers.FindTextBlock(heatmap, "暂无数据");
        Assert.NotNull(emptyText);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(emptyText!), "空态文案应有效可见");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Collection_updates_still_apply_after_detach_and_reattach()
    {
        // 审查 P2-2：detach 退订 INCC，reattach 必须恢复订阅
        var cells = new System.Collections.ObjectModel.ObservableCollection<HeatmapCell>(Cells35());
        var heatmap = new Heatmap { Cells = cells, Columns = 7 };
        var window = CreateWindow(heatmap, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        window.Content = heatmap;
        Dispatcher.UIThread.RunJobs();

        cells.Add(new HeatmapCell { Label = "extra", Value = 1, HasData = true });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(36, CellBorders(heatmap).Count);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 明暗两套 + 截图留档 ──

    [AvaloniaFact]
    public void Heatmap_renders_in_light_and_dark_with_series_tint()
    {
        foreach (var (variant, file) in new (ThemeVariant, string)[]
                 {
                     (ThemeVariant.Light, "heatmap-light.png"),
                     (ThemeVariant.Dark, "heatmap-dark.png"),
                 })
        {
            var heatmap = new Heatmap { Cells = Cells35(), Columns = 7 };
            var window = CreateWindow(heatmap, variant);
            var frame = ChartTestHelpers.RenderFrame(window);

            // 最强档 ≈ rgba(76,141,255,0.9)，帧里应能找到该色相的高 alpha 命中
            Assert.True(ChartTestHelpers.FrameContainsColor(frame, Color.Parse("#4c8dff"), tolerance: 48),
                $"{variant} heatmap 帧缺少系列蓝强度色");
            ChartTestHelpers.SaveArchive(frame, ArchiveDir, file);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
