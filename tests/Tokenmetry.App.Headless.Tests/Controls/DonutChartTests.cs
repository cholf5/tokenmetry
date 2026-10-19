using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
/// D-005 DonutChart 的 headless 验证（TC-001 环形图边界）：
/// 1. 角度纯函数：比例正确、角度和恒为 360°；
/// 2. 1 / 2 / 7 段渲染（弧段数、图例行数、百分比、满圆拆弧）；
/// 3. 空数据 / 全零数据 → 占位环 + 空态文案，不崩；
/// 4. ObservableCollection 增量更新重建；
/// 5. 明暗两套渲染 + 截图留档。
/// </summary>
public class DonutChartTests
{
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_CHART_PREVIEW_DIR");

    private static Window CreateWindow(DonutChart chart, ThemeVariant variant) => new()
    {
        Width = 520,
        Height = 260,
        Content = chart,
        RequestedThemeVariant = variant,
        Background = ThemeBackground(variant),
    };

    /// <summary>窗口底 = 对应变体的 AppBackgroundBrush（截图体现真实页面底色）。</summary>
    private static IBrush ThemeBackground(ThemeVariant variant) =>
        Application.Current!.TryGetResource("AppBackgroundBrush", variant, out var bg)
            && bg is IBrush brush ? brush : new SolidColorBrush(Color.Parse("#111c2e"));

    private static DonutSegment[] FiveSegments() => new[]
    {
        new DonutSegment("Claude Code", 3_600_000),
        new DonutSegment("Codex", 2_260_000),
        new DonutSegment("Cursor", 1_380_000),
        new DonutSegment("GitHub Copilot", 760_000),
        new DonutSegment("Cline", 743_316),
    };

    // ── 几何纯函数 ──

    [AvaloniaFact]
    public void BuildSweeps_produces_proportional_angles_summing_to_360()
    {
        var sweeps = DonutChart.BuildSweeps(new[] { 1d, 1d, 2d });

        Assert.Equal(3, sweeps.Count);
        Assert.Equal(0, sweeps[0].StartDegrees, 9);
        Assert.Equal(90, sweeps[0].SweepDegrees, 9);
        Assert.Equal(90, sweeps[1].StartDegrees, 9);
        Assert.Equal(90, sweeps[1].SweepDegrees, 9);
        Assert.Equal(180, sweeps[2].StartDegrees, 9);
        Assert.Equal(180, sweeps[2].SweepDegrees, 9);
        Assert.Equal(360, sweeps.Sum(s => s.SweepDegrees), 9);
        Assert.Equal(0, sweeps[0].StartDegrees, 9);
        Assert.Equal(360, sweeps[^1].StartDegrees + sweeps[^1].SweepDegrees, 9);
    }

    [AvaloniaFact]
    public void BuildSweeps_single_segment_is_full_circle()
    {
        var sweeps = DonutChart.BuildSweeps(new[] { 5d });

        var (start, sweep) = Assert.Single(sweeps);
        Assert.Equal(0, start, 9);
        Assert.Equal(360, sweep, 9);
    }

    [AvaloniaFact]
    public void BuildSweeps_empty_or_all_zero_returns_empty()
    {
        Assert.Empty(DonutChart.BuildSweeps(Array.Empty<double>()));
        Assert.Empty(DonutChart.BuildSweeps(new[] { 0d, 0d, 0d }));
    }

    [AvaloniaFact]
    public void Full_circle_ring_geometry_covers_whole_ring()
    {
        // 单段 100%：ArcSegment 起终点重合退化 → 控件拆两个 180° 半弧，几何包围盒 = 整环
        var geometry = DonutChart.BuildRingSegment(60, 60, outerRadius: 53, innerRadius: 40, 0, 360);
        var bounds = geometry.Bounds;

        Assert.Equal(106, bounds.Width, 2);
        Assert.Equal(106, bounds.Height, 2);
        Assert.Equal(7, bounds.X, 2);
        Assert.Equal(7, bounds.Y, 2);
    }

    [AvaloniaFact]
    public void Quarter_ring_geometry_matches_expected_quadrant()
    {
        // 0°→90°：顶(60,7)到右(113,60)的四分之一环
        var geometry = DonutChart.BuildRingSegment(60, 60, 53, 40, 0, 90);
        var bounds = geometry.Bounds;

        Assert.Equal(53, bounds.Width, 2);
        Assert.Equal(53, bounds.Height, 2);
        Assert.Equal(113, bounds.Right, 2);
        Assert.Equal(7, bounds.Top, 2);
    }

    // ── 渲染：1 / 2 / 7 段 ──

    [AvaloniaFact]
    public void Single_segment_renders_one_arc_legend_and_100_percent()
    {
        var chart = new DonutChart
        {
            Segments = new[] { new DonutSegment("Only", 1234) },
            CenterTitle = "1,234",
        };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var arc = Assert.Single(chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
        Assert.True(arc.Data!.Bounds.Width > 100, "单段 100% 应铺满整环");
        Assert.Equal(1, chart.GetVisualDescendants().OfType<ItemsControl>().Single().ItemCount);

        var legendTexts = chart.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text is not null && t.Text!.Contains('%'))
            .Select(t => t.Text).ToList();
        Assert.Contains("100.0%", legendTexts);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Single_segment_below_one_still_shows_100_percent()
    {
        // 审查 P2-1：单段小值（total<1）必须按真实占比显示 100%，不得被整数下限拉成 50%
        var chart = new DonutChart
        {
            Segments = new[] { new DonutSegment("Only", 0.5) },
        };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var legendTexts = chart.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text is not null && t.Text!.Contains('%'))
            .Select(t => t.Text).ToList();
        Assert.Equal(new[] { "100.0%" }, legendTexts);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Seven_segments_render_seven_arcs_and_legend()
    {
        var chart = new DonutChart
        {
            Segments = Enumerable.Range(1, 7).Select(i => new DonutSegment($"S{i}", i)).ToArray(),
        };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        Assert.Equal(7, chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Count());
        Assert.Equal(7, chart.GetVisualDescendants().OfType<ItemsControl>().Single().ItemCount);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Two_segments_render_half_half()
    {
        var chart = new DonutChart
        {
            Segments = new[] { new DonutSegment("A", 1), new DonutSegment("B", 1) },
        };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var paths = chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().ToList();
        Assert.Equal(2, paths.Count);
        // 各 180° 半环（0°→180° 右半 / 180°→360° 左半）：高 = 整环外径 138，宽 = 半 69
        Assert.All(paths, p =>
        {
            Assert.Equal(138, p.Data!.Bounds.Height, 1);
            Assert.Equal(69, p.Data.Bounds.Width, 1);
        });

        var legendTexts = chart.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text is not null && t.Text!.Contains('%'))
            .Select(t => t.Text).ToList();
        Assert.Equal(new[] { "50.0%", "50.0%" }, legendTexts);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 悬停 tooltip（Q-003：对称补齐 Donut 侧 chart-tip 类挂载与紧凑文案锁）──

    [AvaloniaFact]
    public void Segment_paths_get_chart_tip_tooltips_with_compact_tokens()
    {
        // Q-003 TC-001：复用 FiveSegments fixture，断言形态同 HeatmapTests（显式 ToolTip + chart-tip 类 + 紧凑文案）
        var chart = new DonutChart { Segments = FiveSegments() };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var paths = chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().ToList();
        Assert.Equal(5, paths.Count); // ArcCanvas 上段序 = 数据段序，首段 Claude Code 3_600_000

        // zh-Hans 测试文化与 B-006 同口径：紧凑 token 值（万制）+ "tok" 后缀，非千分位全量
        var first = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(paths[0]));
        Assert.Contains("Claude Code: 360万 tok", (string)first.Content!);   // 3,600,000 → zh 万档
        Assert.DoesNotContain("3,600,000", (string)first.Content!);         // 气泡里放不下全量数字

        // 每段都挂了带装饰类的显式 ToolTip（ChartTooltips.Set / 类名 / SharedStyles 规则回归时任一会红）
        Assert.All(paths, p =>
        {
            var tip = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(p));
            Assert.Contains("chart-tip", tip.Classes);
        });

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 语言切换重译（B-007）：tooltip/空态副题是 build-time 固化文案，切语言须靠 CurrentLanguage 信号重建 ──

    [AvaloniaFact]
    public void Segment_tooltips_retranslate_on_language_switch_without_data_change()
    {
        // B-007 TC-002：Segments 引用全程不动，zh→en→zh 只靠语言信号触发 Rebuild
        var chart = new DonutChart { Segments = FiveSegments() };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var original = Translations.Instance.CurrentLanguage;
        try
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese); // TestCulture zh-Hans
            Dispatcher.UIThread.RunJobs();
            var zhTip = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(
                ToolTip.GetTip(chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First()));
            Assert.Contains("Claude Code: 360万 tok", (string)zhTip.Content!);

            // zh → en：无数据变更，仅语言信号
            Translations.Instance.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();

            var enPath = chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First();
            var enTip = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(ToolTip.GetTip(enPath)); // Rebuild 换了 Path，须重取
            Assert.Contains("Claude Code: 3.6M tok", (string)enTip.Content!);
            Assert.DoesNotContain("万", (string)enTip.Content!);
            Assert.Contains("chart-tip", enTip.Classes); // 重译挂载不丢类装饰

            // en → zh 回一圈（顺带恢复基线，验证反方向重译）
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
            Dispatcher.UIThread.RunJobs();
            var zhAgain = Assert.IsAssignableFrom<Avalonia.Controls.ToolTip>(
                ToolTip.GetTip(chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First()));
            Assert.Contains("Claude Code: 360万 tok", (string)zhAgain.Content!);
        }
        finally
        {
            Translations.Instance.SetLanguage(original); // 进程级单例，恢复基线
            Dispatcher.UIThread.RunJobs();
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Empty_center_subtitle_retranslates_on_language_switch()
    {
        // B-007 TC-002 空态场景：CenterSubtitleText.Text = NoData 是 build-time 固化（旧账一并修）
        var chart = new DonutChart { Segments = Array.Empty<DonutSegment>() };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var subtitle = chart.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "CenterSubtitleText");

        var original = Translations.Instance.CurrentLanguage;
        try
        {
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese); // TestCulture zh-Hans
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("暂无数据", subtitle.Text);

            Translations.Instance.SetLanguage(AppLanguage.English);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("No data", subtitle.Text);

            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("暂无数据", subtitle.Text);
        }
        finally
        {
            Translations.Instance.SetLanguage(original); // 进程级单例，恢复基线
            Dispatcher.UIThread.RunJobs();
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 空数据 / 全零（TC-001 边界）──

    [AvaloniaFact]
    public void Empty_and_all_zero_segments_show_placeholder_without_crash()
    {
        foreach (var segments in new IEnumerable[] { Array.Empty<DonutSegment>(), new[] { new DonutSegment("z", 0), new DonutSegment("z2", 0) } })
        {
            var chart = new DonutChart { Segments = segments };
            var window = CreateWindow(chart, ThemeVariant.Dark);
            ChartTestHelpers.RenderFrame(window);

            Assert.Empty(chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
            var emptyText = ChartTestHelpers.FindTextBlock(chart, "暂无数据");
            Assert.NotNull(emptyText);
            Assert.True(ChartTestHelpers.IsEffectivelyVisible(emptyText!), "空态文案应有效可见");

            var ring = chart.GetVisualDescendants().OfType<Ellipse>().Single();
            Assert.True(ring.IsVisible);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Null_segments_show_placeholder()
    {
        var chart = new DonutChart { Segments = null };
        var window = CreateWindow(chart, ThemeVariant.Light);
        ChartTestHelpers.RenderFrame(window);

        Assert.True(ChartTestHelpers.IsEffectivelyVisible(chart.GetVisualDescendants().OfType<Ellipse>().Single()));
        Assert.Null(chart.GetVisualDescendants().OfType<ItemsControl>().Single().ItemsSource);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── ObservableCollection 增量更新 ──

    [AvaloniaFact]
    public void ObservableCollection_updates_rebuild_arcs()
    {
        var segments = new ObservableCollection<DonutSegment>(FiveSegments());
        var chart = new DonutChart { Segments = segments };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        Assert.Equal(5, chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Count());

        segments.Add(new DonutSegment("其他", 100_000));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(6, chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Count());

        segments.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Collection_updates_still_apply_after_detach_and_reattach()
    {
        // 审查 P2-2：detach 退订 INCC，reattach 必须恢复订阅（脱树期间集合不变更，重挂后变更仍生效）
        var segments = new ObservableCollection<DonutSegment>(FiveSegments());
        var chart = new DonutChart { Segments = segments };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        window.Content = null; // detach（宿主移除内容）→ OnDetachedFromVisualTree 退订
        Dispatcher.UIThread.RunJobs();
        window.Content = chart; // reattach → OnAttachedToVisualTree 重挂
        Dispatcher.UIThread.RunJobs();

        segments.Add(new DonutSegment("其他", 100_000));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(6, chart.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Count());
        Assert.Equal(6, chart.GetVisualDescendants().OfType<ItemsControl>().Single().ItemCount);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 明暗两套 + 截图留档 ──

    [AvaloniaFact]
    public void Donut_renders_in_light_and_dark_with_series_colors()
    {
        // 五段 fixture：中心区以系列蓝填充（AppSeries0 #4c8dff，不随明暗翻转）
        foreach (var (variant, file) in new (ThemeVariant, string)[]
                 {
                     (ThemeVariant.Light, "donut-light.png"),
                     (ThemeVariant.Dark, "donut-dark.png"),
                 })
        {
            var chart = new DonutChart
            {
                Segments = FiveSegments(),
                CenterTitle = "8.74M",
                CenterSubtitle = "Total Tokens",
            };
            var window = CreateWindow(chart, variant);
            var frame = ChartTestHelpers.RenderFrame(window);

            Assert.True(ChartTestHelpers.FrameContainsColor(frame, Color.Parse("#4c8dff"), tolerance: 48),
                $"{variant} donut 帧缺少系列色 0（蓝）");
            ChartTestHelpers.SaveArchive(frame, ArchiveDir, file);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ── 扫入时间线（motion design）：段延迟 40ms、单段 240ms、CubicEaseOut ──

    [AvaloniaFact]
    public void Sweep_at_is_zero_before_delay_and_final_after_duration()
    {
        Assert.Equal(0, DonutChart.SweepAt(elapsedMs: 0, index: 2, finalSweep: 90));   // 未到延迟
        Assert.Equal(0, DonutChart.SweepAt(39, 1, 90));                                // 延迟边界前
        Assert.Equal(90, DonutChart.SweepAt(1000, 0, 90));                             // 完成后取终值
        Assert.Equal(90, DonutChart.SweepAt(1000, 5, 90));
    }

    [AvaloniaFact]
    public void Sweep_at_is_monotonic_easeout_within_window()
    {
        double previous = 0;
        for (var t = 10.0; t <= 280; t += 10)
        {
            var sweep = DonutChart.SweepAt(t, index: 0, finalSweep: 120);
            Assert.InRange(sweep, previous, 120);
            Assert.True(sweep >= previous);
            previous = sweep;
        }

        // ease-out：前半程进度超过线性一半
        Assert.True(DonutChart.SweepAt(120, 0, 240) > 120 * (120.0 / 240));
    }

    [AvaloniaFact]
    public void Sweep_total_covers_last_segment()
    {
        Assert.Equal(0, DonutChart.SweepTotalMs(0));
        Assert.Equal(240, DonutChart.SweepTotalMs(1));
        Assert.Equal((3 - 1) * 40 + 240, DonutChart.SweepTotalMs(3));
    }

    // ── 中心文字淡入落定（回归锁：用户实测扫入后中心数字消失）──

    [AvaloniaFact]
    public void Center_texts_stay_visible_after_fade_in_completes()
    {
        // Avalonia 动画完成后属性回落到本地值：若本地值停在起帧 0，中心数字在 180ms 淡入
        // 结束的瞬间消失（用户实测）。淡入必须以本地终值 1 收尾。
        var chart = new DonutChart
        {
            Segments = FiveSegments(),
            CenterTitle = "8.74M",
            CenterSubtitle = "Total Tokens",
        };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        // 泵到中心标题淡入完成（180ms；headless 动画钟每 tick 只推进 ~16ms，按到位为止泵）
        var title = chart.FindControl<TextBlock>("CenterTitleText");
        var subtitle = chart.FindControl<TextBlock>("CenterSubtitleText");
        Assert.NotNull(title);
        Assert.NotNull(subtitle);
        for (var i = 0; i < 200 && title!.Opacity < 0.999; i++)
        {
            Thread.Sleep(30); // 合成帧时间戳走真实墙钟，Sleep 推墙钟 + tick 出帧
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var titleBase = title!.GetBaseValue(Avalonia.Visual.OpacityProperty);
        var subtitleBase = subtitle!.GetBaseValue(Avalonia.Visual.OpacityProperty);
        // headless 动画钟爬得慢，完成前有效值到不了 1——落定契约断言本地值：动画结束后
        // 有效值回落到本地值，本地值必须停在终态 1（bug 版本本地值为 0，实测消失）
        Assert.True(titleBase.HasValue && titleBase.Value > 0.999,
            $"中心标题淡入应落定本地值 1，实测 本地值 {titleBase} / 有效值 {title.Opacity:0.###}——动画值回落到本地 0 即消失");
        Assert.True(subtitleBase.HasValue && subtitleBase.Value > 0.999,
            $"中心副题淡入应落定本地值 1，实测 本地值 {subtitleBase} / 有效值 {subtitle.Opacity:0.###}——动画值回落到本地 0 即消失");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
