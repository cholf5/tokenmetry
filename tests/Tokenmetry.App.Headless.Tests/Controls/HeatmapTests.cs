using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Controls;

/// <summary>
/// D-005 Heatmap 的 headless 验证（TC-002 热力图悬停）：
/// 1. 强度色纯函数：alpha 单调、色相保持、端点等于原型 0.12/0.9；
/// 2. 格子数 = 数据条数、列数/行高可配；
/// 3. 悬停 tooltip：有数据日 token 紧凑单位数值（随 UI 语言）、补零日"无数据"（B-006 同步换口径）；
/// 4. tooltip 统一挂 chart-tip 类（App 令牌配色，Semi 默认配色断层不进图表）；
/// 5. 空数据占位；
/// 6. 明暗两套渲染 + 截图留档。
/// </summary>
public class HeatmapTests
{
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_CHART_PREVIEW_DIR");

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

        // 有数据格：token 紧凑单位（zh-Hans 测试文化 → 万档；同落 10 根热力柱可读）。
        // GetTip 返回 chart-tip 装饰的 ToolTip 实例，文案在 Content 里。
        var withValue = borders[0];
        var withValueTip = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(withValue));
        Assert.Contains("10万 tok", (string)withValueTip.Content!);
        Assert.DoesNotContain("100,000", (string)withValueTip.Content!);

        // 补零格（HasData=false）：诚实"无数据"
        var padded = borders[5];
        var paddedTip = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(padded));
        Assert.Contains("无数据", (string)paddedTip.Content!);

        // 悬浮层挂 chart-tip 类（B-006）：显式 ToolTip 实例 + SharedStyles 令牌规则
        Assert.Contains("chart-tip", withValueTip.Classes);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Chart_tip_class_tokens_apply_on_open()
    {
        // chart-tip 类的真实生效（不只挂类）：显示后实例属性 = App 令牌画刷（DynamicResource 同实例）
        var heatmap = new Heatmap { Cells = Cells35(), Columns = 7 };
        var window = CreateWindow(heatmap, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var border = CellBorders(heatmap)[0];
        var tooltip = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(border));

        ToolTip.SetIsOpen(border, true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Application.Current!.TryGetResource("AppChipBackgroundBrush", ThemeVariant.Dark, out var chip),
            "Dark 变体缺少 AppChipBackgroundBrush");
        Assert.Same(Assert.IsAssignableFrom<ISolidColorBrush>(chip), tooltip.Background);
        Assert.True(Application.Current!.TryGetResource("AppTextBrush", ThemeVariant.Dark, out var text),
            "Dark 变体缺少 AppTextBrush");
        Assert.Same(Assert.IsAssignableFrom<ISolidColorBrush>(text), tooltip.Foreground);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 语言切换重译（B-007）：tooltip 是 build-time 固化文案，切语言须靠 CurrentLanguage 信号重建 ──

    [AvaloniaFact]
    public void Tooltips_retranslate_on_language_switch_without_data_change()
    {
        // B-007 TC-001：Cells 引用全程不动，zh→en→zh 双语言各一圈只靠语言信号触发 Rebuild
        var heatmap = new Heatmap { Cells = Cells35(), Columns = 7 };
        var window = CreateWindow(heatmap, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var original = Translations.Instance.CurrentLanguage;
        try
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese); // TestCulture zh-Hans；基线被污染时原地归位
            Dispatcher.UIThread.RunJobs();

            // zh 基线（同既有用例口径）：有数据格万档单位、补零格"无数据"
            var zhTip = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(CellBorders(heatmap)[0]));
            Assert.Contains("10万 tok", (string)zhTip.Content!);
            var zhPadded = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(CellBorders(heatmap)[5]));
            Assert.Contains("无数据", (string)zhPadded.Content!);

            // zh → en：无数据变更，仅语言信号
            Translations.Instance.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();

            var enBorders = CellBorders(heatmap); // Rebuild 重建了 Border，按索引重取同一逻辑格
            var enTip = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(enBorders[0]));
            Assert.Contains("100K tok", (string)enTip.Content!);
            Assert.DoesNotContain("万", (string)enTip.Content!);
            Assert.Contains("chart-tip", enTip.Classes); // 重译挂载不丢类装饰
            var enPadded = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(enBorders[5]));
            Assert.Contains("no data", (string)enPadded.Content!);
            Assert.DoesNotContain("无数据", (string)enPadded.Content!);

            // en → zh 回一圈（顺带恢复基线，验证反方向重译）
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
            Dispatcher.UIThread.RunJobs();

            var zhAgain = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(CellBorders(heatmap)[0]));
            Assert.Contains("10万 tok", (string)zhAgain.Content!);
        }
        finally
        {
            Translations.Instance.SetLanguage(original); // 进程级单例，恢复基线
            Dispatcher.UIThread.RunJobs();
        }

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
            ChartTestHelpers.RenderFrame(window);

            // stagger 渐入走满再取帧（motion design 测试基建）：合成帧时间戳走真实墙钟，
            // Sleep 推进墙钟 + tick 出帧拿到新时间戳（动画总时长 ≤ 630ms，150ms×若干轮充分）
            var cells = heatmap.GetVisualDescendants().OfType<Avalonia.Controls.Border>()
                .Where(b => b.Classes.Contains("hm-cell")).ToList();
            for (var i = 0; i < 10 && cells.Any(c => c.Opacity < 0.999); i++)
            {
                Thread.Sleep(150);
                Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            var frame = ChartTestHelpers.RenderFrame(window);

            // 最强档 ≈ rgba(76,141,255,0.9)，帧里应能找到该色相的高 alpha 命中
            Assert.True(ChartTestHelpers.FrameContainsColor(frame, Color.Parse("#4c8dff"), tolerance: 48),
                $"{variant} heatmap 帧缺少系列蓝强度色");
            ChartTestHelpers.SaveArchive(frame, ArchiveDir, file);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ── stagger 间隔（motion design）：间隔 15ms 起步，总时长封顶 600ms ──

    [AvaloniaFact]
    public void Stagger_interval_compresses_to_cap_total_duration()
    {
        Assert.Equal(0, Heatmap.StaggerIntervalMs(0));
        Assert.Equal(0, Heatmap.StaggerIntervalMs(1));
        Assert.Equal(15, Heatmap.StaggerIntervalMs(7));    // 少格：走起步间隔
        // 多格：压缩间隔使 120 + (count-1)×interval ≤ 600（计划原三元断言为恒真式，按其注简化为守卫式）
        var bigInterval = Heatmap.StaggerIntervalMs(200);
        Assert.Equal((Heatmap.StaggerCapMs - Heatmap.CellFadeMs) / 199, bigInterval, 6);
        Assert.True(Heatmap.CellFadeMs + (199 * bigInterval) <= Heatmap.StaggerCapMs + 1e-9);
    }

    [AvaloniaFact]
    public void Stagger_delay_is_index_times_interval()
    {
        Assert.Equal(0, Heatmap.StaggerDelayMs(0, 35));
        Assert.Equal(2 * Heatmap.StaggerIntervalMs(35), Heatmap.StaggerDelayMs(2, 35));
    }
}
