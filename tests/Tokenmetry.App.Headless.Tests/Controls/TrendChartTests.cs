using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Tokenmetry.App.Controls;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Localization;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Controls;

/// <summary>
/// D-004 TrendChart 的 headless 验证：
/// 1. 三形态渲染出图（系列色像素存在）+ 明暗两套；
/// 2. hover helper：裁剪翻转纯函数四象限、悬停事件与 tooltip 文案（token 紧凑单位 / 成本对账四态口径）、
///    补零日文案辨识、成本形态连线（补零日 $0 成线 + 未知成本日断线）；
/// 3. 明暗切换触发整图重建（轴文字色随令牌变化）；
/// 4. 空数据诚实空态（空集与全补零窗口，环境无关）；
/// 5. I-028 真实日序列联调冒烟（不依赖本机 facts 是否有数据）；
/// 6. B-006 tooltip 气泡令牌化（chip 底 / 卡片描边 / 文字色随明暗变体现取）。
/// </summary>
public class TrendChartTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_CHART_PREVIEW_DIR");

    private static Window CreateWindow(TrendChart chart, ThemeVariant variant) => new()
    {
        Width = 820,
        Height = 420,
        Content = chart,
        RequestedThemeVariant = variant,
        Background = ThemeBackground(variant),
    };

    /// <summary>窗口底 = 对应变体的 AppBackgroundBrush（截图体现真实页面底色）。</summary>
    private static IBrush ThemeBackground(ThemeVariant variant) =>
        Application.Current!.TryGetResource("AppBackgroundBrush", variant, out var bg)
            && bg is IBrush brush ? brush : new SolidColorBrush(Color.Parse("#0f1118"));

    // ── TC-001：三形态渲染（明暗两套 + 截图留档）──

    [AvaloniaFact]
    public void DailyTokens_mode_renders_series_pixels_in_light_and_dark()
    {
        var expected = new (ThemeVariant Variant, string BarHex, string File)[]
        {
            (ThemeVariant.Light, "#2e6bdc", "trendchart-daily-light.png"),
            (ThemeVariant.Dark, "#4c8dff", "trendchart-daily-dark.png"),
        };

        foreach (var (variant, barHex, file) in expected)
        {
            var chart = new TrendChart
            {
                Mode = TrendChartMode.DailyTokens,
                DailyPoints = ChartTestHelpers.DailyPoints30(),
            };
            var window = CreateWindow(chart, variant);
            var frame = ChartTestHelpers.RenderFrame(window);

            // 输入柱（蓝）与输出柱（紫）都应出现在合成帧里（非空像素、系列色命中）
            Assert.True(ChartTestHelpers.FrameContainsColor(frame, Color.Parse(barHex)),
                $"{variant} 日趋势帧缺少输入柱颜色 {barHex}");
            var purple = variant == ThemeVariant.Light ? "#7d4fe0" : "#a779ff";
            Assert.True(ChartTestHelpers.FrameContainsColor(frame, Color.Parse(purple)),
                $"{variant} 日趋势帧缺少输出柱颜色 {purple}");
            ChartTestHelpers.SaveArchive(frame, ArchiveDir, file);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Cost_and_hourly_modes_render_in_light_and_dark()
    {
        var cyan = new (ThemeVariant Variant, string Hex, string File)[]
        {
            (ThemeVariant.Light, "#1d9bc4", "cost-light"),
            (ThemeVariant.Dark, "#57c7e8", "cost-dark"),
        };

        foreach (var (variant, hex, name) in cyan)
        {
            var cost = new TrendChart
            {
                Mode = TrendChartMode.CostUsd,
                DailyPoints = ChartTestHelpers.DailyPoints30(),
            };
            var window = CreateWindow(cost, variant);
            var frame = ChartTestHelpers.RenderFrame(window);
            Assert.True(ChartTestHelpers.FrameContainsColor(frame, Color.Parse(hex)),
                $"{variant} 成本折线帧缺少折线颜色 {hex}");
            ChartTestHelpers.SaveArchive(frame, ArchiveDir, $"trendchart-{name}.png");
            window.Close();
            Dispatcher.UIThread.RunJobs();

            // 小时形态：0..23 全量桶
            var hourly = new TrendChart
            {
                Mode = TrendChartMode.HourlyTokens,
                HourlyDistribution = HourlyFixture(),
            };
            var hourlyWindow = CreateWindow(hourly, variant);
            var hourlyFrame = ChartTestHelpers.RenderFrame(hourlyWindow);
            Assert.True(ChartTestHelpers.FrameContainsColor(hourlyFrame, Color.Parse(hex)),
                $"{variant} 小时折线帧缺少折线颜色 {hex}");
            ChartTestHelpers.SaveArchive(hourlyFrame, ArchiveDir, $"trendchart-hourly-{variant.ToString().ToLowerInvariant()}.png");
            hourlyWindow.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static HourlyDistributionResult HourlyFixture() => new()
    {
        Hours = Enumerable.Range(0, 24).Select(h => new HourlyUsageBucket
        {
            Hour = h,
            SessionCount = h is 10 or 14 or 18 ? 8 : 2,
            TotalTokens = h switch { 10 => 950_000, 14 => 920_000, 18 => 900_000, < 6 => 0, _ => 200_000 + h * 12_000 },
        }).ToList(),
        UnknownSessionCount = 3,
        UnknownTotalTokens = 86_400,
        TimeZoneId = TimeZoneInfo.Local.Id,
    };

    // ── D-027：单日窗口的小时形态（已发生边界 / 小时成本断线 / tooltip 口径）──

    [AvaloniaTheory]
    [InlineData(null, 24)]  // 昨天及更早：24 小时全已发生
    [InlineData(-1, 0)]     // 未来单日：整天尚未到达
    [InlineData(0, 1)]      // 今天 00 点刚过
    [InlineData(5, 6)]
    [InlineData(23, 24)]
    [InlineData(99, 24)]    // 时钟越界夹回（不让调用方的钟漂画到桶外）
    [InlineData(-5, 0)]
    public void Real_hour_count_clamps_cutoff_to_bucket_range(int? cutoffHour, int expected) =>
        Assert.Equal(expected, TrendChart.RealHourCount(24, cutoffHour));

    [AvaloniaFact]
    public void Hourly_cutoff_keeps_not_yet_reached_hours_off_the_line()
    {
        var chart = new TrendChart
        {
            Mode = TrendChartMode.HourlyTokens,
            HourlyDistribution = HourlyFixture(),
            HourlyCutoffHour = 5, // 今天 05:xx：0..5 已发生，06..23 尚未到达
        };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        // 曲线与面积填充只覆盖已发生的小时：未来小时不是真实 0，绝不被连成“降到零”的曲线
        var line = chart.Plot.PlottableList.OfType<ScottPlot.Plottables.Scatter>().Single(s => s.LineWidth > 0);
        var lineXs = line.Data.GetScatterPoints().Select(p => p.X).ToArray();
        Assert.True(lineXs.Max() <= 5, $"曲线不应越过 05:00，实际最大 X = {lineXs.Max()}");
        Assert.True(line.FillY, "已发生段保留零基线面积填充");

        // 未来小时仅作淡化小点（锚在零轴），且 18 个桶全在（0..23 全量桶不变）
        var scatters = chart.Plot.PlottableList.OfType<ScottPlot.Plottables.Scatter>().ToList();
        Assert.Contains(scatters, s => s.LineWidth == 0 && s.Data.GetScatterPoints().Count == 6);   // 已发生标记点
        Assert.Contains(scatters, s => s.LineWidth == 0 && s.Data.GetScatterPoints().Count == 18);  // 尚未到达淡点

        // 只有 1 个已发生小时时无从成线，也不崩（仅标记点呈现）
        chart.HourlyCutoffHour = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(chart.Plot.PlottableList.OfType<ScottPlot.Plottables.Scatter>(), s => s.LineWidth > 0);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hourly_cost_mode_breaks_line_on_unknown_cost_hours()
    {
        var hours = Enumerable.Range(0, 24).Select(h => new HourlyUsageBucket
        {
            Hour = h,
            SessionCount = 1,
            TotalTokens = 1_000,
            // 10、11 点有用量但无价（未知成本）：断线，绝不折 0 连过去
            Cost = h is 10 or 11 ? new AggregatedCost(null, true) : new AggregatedCost(0.5m, false),
        }).ToList();
        var chart = new TrendChart
        {
            Mode = TrendChartMode.HourlyCost,
            HourlyDistribution = new HourlyDistributionResult { Hours = hours },
        };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var runs = chart.Plot.PlottableList.OfType<ScottPlot.Plottables.Scatter>()
            .Where(s => s.LineWidth > 0).ToList();
        Assert.Equal(2, runs.Count); // 0..9 与 12..23 两段
        Assert.True(runs[0].Data.GetScatterPoints().Max(p => p.X) <= 9);
        Assert.True(runs[1].Data.GetScatterPoints().Min(p => p.X) >= 12);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hourly_tooltips_state_session_start_attribution_and_not_yet_hours()
    {
        var t = Translations.Instance;
        var bucket = new HourlyUsageBucket
        {
            Hour = 14,
            TotalTokens = 42_000,
            SessionCount = 3,
            Cost = new AggregatedCost(1.25m, false),
        };

        // 口径可见（卡内定案路线 A）：小时图归因于会话开始时间，与按 fact.Day 归日的 KPI 不同口径
        var tokens = TrendChart.HourlyTokenTooltipText(bucket, notYet: false);
        Assert.Contains("14:00", tokens);
        Assert.Contains(TrendChart.FormatCompactTokens(42_000), tokens);
        Assert.Contains(t.AttributionSessionStart, tokens);

        var cost = TrendChart.HourlyCostTooltipText(bucket, notYet: false);
        Assert.Contains(TrendChart.FormatCostUsd(1.25m, false)!, cost);
        Assert.Contains(t.AttributionSessionStart, cost);

        // 未知成本小时：四态文案同 CostTooltipText，绝不显示 $0.00
        var unknownCost = TrendChart.HourlyCostTooltipText(
            new HourlyUsageBucket { Hour = 9, TotalTokens = 100, SessionCount = 1, Cost = new AggregatedCost(null, true) },
            notYet: false);
        Assert.Contains(t.ChartTipCostUnknown, unknownCost);
        Assert.DoesNotContain("$0", unknownCost);

        // 尚未到达的小时只写「尚未到达」——不冒充 0 用量，也不拿归因口径解释空气
        var future = TrendChart.HourlyTokenTooltipText(bucket, notYet: true);
        Assert.Contains("14:00", future);
        Assert.Contains(t.ChartTipHourNotYet, future);
        Assert.DoesNotContain(t.AttributionSessionStart, future);
    }

    // ── 浮窗锚点（motion design）：右偏 14px / 垂直居中 / 右半翻转 / 数据区夹回 ──

    [AvaloniaFact]
    public void Tooltip_origin_sits_right_of_point_and_vertically_centered()
    {
        var origin = TrendChart.ComputeTooltipOrigin(
            100, 150, dataLeft: 0, dataTop: 0, dataWidth: 400, dataHeight: 300,
            tooltipWidth: 90, tooltipHeight: 60);
        Assert.Equal(114, origin.X);           // 100 + 14
        Assert.Equal(120, origin.Y);           // 150 - 60/2
    }

    [AvaloniaFact]
    public void Tooltip_origin_flips_to_left_half_when_point_in_right_half()
    {
        var origin = TrendChart.ComputeTooltipOrigin(
            300, 150, 0, 0, 400, 300, 90, 60);
        Assert.Equal(300 - 14 - 90, origin.X); // 右半 → 气泡伸向左侧
        Assert.Equal(120, origin.Y);
    }

    [AvaloniaFact]
    public void Tooltip_origin_clamps_into_data_rect()
    {
        // 垂直：顶部点（气泡一半越上缘）夹回 dataTop
        var top = TrendChart.ComputeTooltipOrigin(100, 5, 0, 0, 400, 300, 90, 60);
        Assert.Equal(0, top.Y);
        // 垂直：底部点夹回 dataBottom - 高度
        var bottom = TrendChart.ComputeTooltipOrigin(100, 295, 0, 0, 400, 300, 90, 60);
        Assert.Equal(300 - 60, bottom.Y);
        // 水平：左半宽气泡（向右伸越右缘）夹回 dataRight - 宽度
        var wide = TrendChart.ComputeTooltipOrigin(190, 150, 0, 0, 400, 300, 250, 60);
        Assert.Equal(400 - 250, wide.X);
        // 水平：气泡比数据区宽（右半翻转后越左界）夹回 dataLeft
        var narrow = TrendChart.ComputeTooltipOrigin(45, 150, 0, 0, 60, 300, 90, 60);
        Assert.Equal(0, narrow.X);
    }

    [AvaloniaFact]
    public void Tooltip_follow_y_centers_on_cursor_and_clamps_into_data_rect()
    {
        // 规格 §3(2) 上下跟随：气泡垂直居中于光标，越界夹回数据区（与锚点 Y 夹回同语义）
        Assert.Equal(120, TrendChart.ComputeTooltipFollowY(mousePixelY: 150, dataTop: 0, dataHeight: 300, tooltipHeight: 60));
        Assert.Equal(0, TrendChart.ComputeTooltipFollowY(5, 0, 300, 60));
        Assert.Equal(300 - 60, TrendChart.ComputeTooltipFollowY(295, 0, 300, 60));
    }

    [AvaloniaFact]
    public void SmoothDamp_accelerates_from_rest_brakes_into_target_and_never_overshoots()
    {
        // 规格 §3(2) "加速和刹车"：对比旧逐帧 lerp(0.25) 的首步即 25% 全程（速度突跳），
        // 弹簧首步位移远小于峰值步（起步加速）；末段步长收缩（刹车）；全程不回退不过冲。
        const double gap = 300;
        var position = 0d;
        var velocity = 0d;
        var steps = new double[120];
        for (var i = 0; i < steps.Length; i++)
        {
            var before = position;
            position = TrendChart.SmoothDamp(position, gap, ref velocity, smoothTime: 0.2, deltaTime: 0.016);
            steps[i] = position - before;
        }

        var peak = steps.Max();
        Assert.True(steps[0] < peak * 0.6, $"起步应加速：首步 {steps[0]:0.##} 应显著小于峰值步 {peak:0.##}");
        Assert.True(steps[^1] < peak * 0.05, $"临近落定应刹车：末步 {steps[^1]:0.###} 远小于峰值步 {peak:0.##}");
        Assert.All(steps, s => Assert.True(s >= 0, "临界阻尼不应回退"));
        Assert.True(position <= gap + 0.5, "不应过冲目标");
        Assert.True(Math.Abs(position - gap) < 0.5, $"应落定目标，实测 {position:0.##}");
    }

    // ── hover helper：悬停管线（事件 + tooltip 文案口径）──

    [AvaloniaFact]
    public void Hover_on_real_day_fires_event_with_compact_tokens_and_reconciled_cost()
    {
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        TrendChartHoverEventArgs? received = null;
        chart.HoverChanged += (_, e) => received = e;

        var real = ChartTestHelpers.FirstRealPoint(points);
        // 浮窗 token 数随 UI 语言（Q-008）：本用例在英文口径锁定紧凑短单位；中文万进制口径见下一用例
        WithLanguage(AppLanguage.English, () =>
        {
            chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(real.Day, CultureInfo.InvariantCulture).ToOADate(), 0));

            Assert.NotNull(received);
            Assert.True(received!.IsActive);
            // token 数紧凑短单位（B-006 问题 3）：fixture 首个真实日 402,000/154,000/556,000 → 402K/154K/556K
            Assert.Contains("tot 556K tok", received.LabelText);
            Assert.Contains("in  402K", received.LabelText);
            Assert.Contains("out 154K", received.LabelText);
            Assert.DoesNotContain("556,000", received.LabelText); // 不再千分位全量——气泡内 5-9 位数字不可读
            // 成本行保持对账口径（两位小数 USD）
            Assert.Contains($"${real.Cost.KnownCostUsd!.Value.ToString("F2", CultureInfo.InvariantCulture)}", received.LabelText);
            Assert.False(received.LabelText!.Contains('+'), "非未知成本日不应带 '+' 下限标记");
        });

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hover_tooltip_uses_wan_units_in_chinese_and_follows_language_switch()
    {
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        TrendChartHoverEventArgs? received = null;
        chart.HoverChanged += (_, e) => received = e;

        var real = ChartTestHelpers.FirstRealPoint(points);
        var hover = () => chart.ShowHoverAt(
            new ScottPlot.Coordinates(DateTime.Parse(real.Day, CultureInfo.InvariantCulture).ToOADate(), 0));

        // 中文口径（Q-008）：402,000/154,000/556,000 → 40.2万/15.4万/55.6万（万进制，与 KPI/环形图同源）
        WithLanguage(AppLanguage.SimplifiedChinese, () =>
        {
            hover();
            Assert.Contains("总量 55.6万 Token", received!.LabelText);
            Assert.Contains("输入  40.2万", received.LabelText);
            Assert.Contains("输出 15.4万", received.LabelText);
        });

        // 语言切换实时性（B-007 CurrentLanguage 信号重译）：不换数据、不重建控件，浮窗单位即随切换换制
        WithLanguage(AppLanguage.English, () =>
        {
            hover();
            Assert.Contains("tot 556K tok", received!.LabelText);
            Assert.Contains("in  402K", received.LabelText);
        });

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hover_on_unknown_cost_day_marks_plus_suffix()
    {
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.CostUsd, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        TrendChartHoverEventArgs? received = null;
        chart.HoverChanged += (_, e) => received = e;

        // fixture 最后一天：已知成本 + 未知成本混合 → "$x.xx+"（已知成本下限口径）
        var mixed = points.Last(p => p.HasData);
        chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(mixed.Day, CultureInfo.InvariantCulture).ToOADate(), 0));

        Assert.NotNull(received);
        Assert.True(received!.IsActive);
        Assert.Contains(
            $"${mixed.Cost.KnownCostUsd!.Value.ToString("F2", CultureInfo.InvariantCulture)}+",
            received.LabelText);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hover_on_zero_filled_day_says_no_data()
    {
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        TrendChartHoverEventArgs? received = null;
        chart.HoverChanged += (_, e) => received = e;

        var padded = ChartTestHelpers.FirstPaddedPoint(points);
        chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(padded.Day, CultureInfo.InvariantCulture).ToOADate(), 0));

        Assert.NotNull(received);
        Assert.True(received!.IsActive);
        Assert.Contains(Translations.Instance.ChartTipNoDataZeroFilled, received.LabelText);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hover_miss_when_not_pinned_clears_state()
    {
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var lastEvent = default(TrendChartHoverEventArgs);
        chart.HoverChanged += (_, e) => lastEvent = e;

        // 未驻留时未命中（窗口最早日期再往前 10 年）→ 清除状态 IsActive=false
        //（驻留中未命中则保持现状，见 Hover_stays_pinned_across_dead_zone_until_pointer_exits）
        var firstDay = DateTime.Parse(points[0].Day, CultureInfo.InvariantCulture).ToOADate();
        chart.ShowHoverAt(new ScottPlot.Coordinates(firstDay - 3650, 0));

        Assert.NotNull(lastEvent);
        Assert.False(lastEvent!.IsActive);
        Assert.Null(lastEvent.LabelText);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 主题重绘（ScottPlot 不吃 DynamicResource，需变体变化时整图重建）──

    [AvaloniaFact]
    public void Theme_variant_change_rebuilds_plot_with_new_token_colors()
    {
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = ChartTestHelpers.DailyPoints30() };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var darkTickColor = chart.Plot.Axes.Bottom.TickLabelStyle.ForeColor;
        Assert.Equal(Color.Parse("#8293ad"), ToAvalonia(darkTickColor)); // Dark AppMutedTextBrush

        window.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();

        var lightTickColor = chart.Plot.Axes.Bottom.TickLabelStyle.ForeColor;
        Assert.Equal(Color.Parse("#546783"), ToAvalonia(lightTickColor)); // Light AppMutedTextBrush
        Assert.NotEqual(darkTickColor, lightTickColor);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 浮窗（motion design）：Avalonia 浮层替代 ScottPlot Tooltip plottable ──

    [AvaloniaFact]
    public void Hover_overlay_replaces_scottplot_tooltip_plottable()
    {
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        // ScottPlot Tooltip plottable 已移除（浮窗职责移交 Avalonia Border 浮层）
        Assert.Empty(chart.Plot.PlottableList.OfType<ScottPlot.Plottables.Tooltip>());

        var overlay = chart.FindControl<Border>("HoverTip");
        Assert.NotNull(overlay);
        Assert.Contains("chart-hovertip", overlay!.Classes);
        Assert.False(overlay.IsVisible);

        // ShowHoverAt 后浮层可见、文本与悬停标签同源
        var first = ChartTestHelpers.FirstRealPoint(points);
        chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(first.Day, CultureInfo.InvariantCulture).ToOADate(), 0));
        Dispatcher.UIThread.RunJobs();
        Assert.True(overlay.IsVisible);
        Assert.Equal(TrendChart.DailyTooltipText(first), overlay.FindControl<TextBlock>("HoverTipText")!.Text);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hover_renders_themed_bubble_pixels_and_archives()
    {
        // 像素级回归锁（视觉问题只断 style 属性不够）：库默认透明气泡在深色卡上整块隐形——
        // 修复后 chip 底（#232c39）必须以精确色值出现在合成帧（卡片 #181e27/#151a22、
        // 网格 #2c343f 与它都不可在该容差位混淆），且有近白文字像素；移开后气泡像素应消失。
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        var before = ChartTestHelpers.RenderFrame(window);
        Assert.False(ChartTestHelpers.FrameContainsColor(before, Color.Parse("#232c39"), tolerance: 1),
            "未悬停时不应有气泡底色像素");

        var real = ChartTestHelpers.FirstRealPoint(points);
        var overlay = chart.FindControl<Border>("HoverTip")!;
        chart.ShowHoverAt(new ScottPlot.Coordinates(
            DateTime.Parse(real.Day, CultureInfo.InvariantCulture).ToOADate(), real.TotalTokens));
        PumpUntilOverlaySettled(overlay, targetOpacity: 1); // 淡入过渡走满（headless 动画钟只在 RunJobs/渲染 tick 推进）
        var frame = ChartTestHelpers.RenderFrame(window);

        var bubble = Color.Parse("#232c39"); // chip 底 = Dark AppChipBackgroundBrush（与卡片/网格不可容差重合）
        Assert.True(ChartTestHelpers.FrameContainsColor(frame, bubble, tolerance: 1), "悬停帧缺少精确气泡底色像素");
        // 文字为 12px 抗锯齿字形，实色核可能被混色，用近白宽容差
        Assert.True(ChartTestHelpers.FrameContainsColor(frame, Color.Parse("#e8eef9"), tolerance: 28), "悬停帧缺少气泡文字像素");
        ChartTestHelpers.SaveArchive(frame, ArchiveDir, "trendchart-hover-dark.png");

        // 离开图表区域（HideHover）→ 气泡像素消失（状态收尾）；图内未命中是驻留，不走这里
        chart.HideHover();
        PumpUntilOverlaySettled(overlay, targetOpacity: 0); // 120ms 淡出走满再取帧
        var after = ChartTestHelpers.RenderFrame(window);
        Assert.False(ChartTestHelpers.FrameContainsColor(after, bubble, tolerance: 1), "移开后气泡底色像素应消失");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Tooltip_reentry_snaps_to_new_anchor_instead_of_gliding()
    {
        // 规格 §3(4)：隐藏后插值状态复位到新锚点（避免从旧位滑入）。HideHover 为淡出保持
        // IsVisible=true，重进判定不能依赖 IsVisible——若不复位，重悬停时气泡从旧锚点滑过整图。
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var overlay = chart.FindControl<Border>("HoverTip")!;
        var translate = Assert.IsType<Avalonia.Media.TranslateTransform>(overlay.RenderTransform);

        var first = ChartTestHelpers.FirstRealPoint(points);
        var last = points.Last(p => p.HasData);
        chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(first.Day, CultureInfo.InvariantCulture).ToOADate(), 0));
        var firstX = translate.X;

        // 离开（HideHover 淡出）→ 远端重进：浮窗应立即贴住新锚点，而不是从 firstX 滑过去
        chart.HideHover();
        chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(last.Day, CultureInfo.InvariantCulture).ToOADate(), 0));

        Assert.True(Math.Abs(translate.X - firstX) > 100,
            $"重进后浮窗应贴新锚点（复位插值状态），实测仍在旧位附近：X={translate.X:0.#}（旧位 {firstX:0.#}）");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hover_stays_pinned_across_dead_zone_until_pointer_exits()
    {
        // 驻留不变式（规格 §3(6) 反闪烁）：浮窗一旦显示，鼠标仍在图内移动但未命中新点（死区）
        // 时保持现状——crosshair、浮窗、事件都不动，命中新点后由插值时钟从旧锚点滑过去；
        // 只有离开图表区域（PointerExited→HideHover）才淡出。稀疏图（点距远超 15px 拾取半径）
        // 跨节点移动不再"旧的消失、新的闪现"。
        var points = new[]
        {
            new DailyUsagePoint
            {
                Day = "2026-09-01", HasData = true,
                InputTokens = 100_000, OutputTokens = 50_000, TotalTokens = 150_000,
                Cost = new AggregatedCost(1.5m, hasUnknownCost: false),
            },
            new DailyUsagePoint
            {
                Day = "2026-09-15", HasData = true,
                InputTokens = 200_000, OutputTokens = 100_000, TotalTokens = 300_000,
                Cost = new AggregatedCost(2.5m, hasUnknownCost: false),
            },
        };
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var overlay = chart.FindControl<Border>("HoverTip")!;
        var translate = Assert.IsType<Avalonia.Media.TranslateTransform>(overlay.RenderTransform);
        var day1 = DateTime.Parse("2026-09-01", CultureInfo.InvariantCulture).ToOADate();
        var day15 = DateTime.Parse("2026-09-15", CultureInfo.InvariantCulture).ToOADate();

        chart.ShowHoverAt(new ScottPlot.Coordinates(day1, points[0].TotalTokens));
        PumpUntilOverlaySettled(overlay, targetOpacity: 1);
        Assert.True(overlay.IsVisible);
        var pinnedX = translate.X;
        var pinnedText = overlay.FindControl<TextBlock>("HoverTipText")!.Text;

        // 两点正中（像素距任一点 ≫15px 拾取半径）、光标纵向移到轴上方（数据 Y 超上限）
        // → 死区未命中：X/文案/事件/crosshair 全保持，Y 仍随光标上下（规格 §3(2)(6) 叠加）
        TrendChartHoverEventArgs? missEvent = null;
        chart.HoverChanged += (_, e) => missEvent = e;
        chart.ShowHoverAt(new ScottPlot.Coordinates((day1 + day15) / 2, points[0].TotalTokens * 4));
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.True(overlay.IsVisible, "死区未命中浮窗应驻留");
        Assert.True(overlay.Opacity > 0.99, "死区未命中浮窗不应淡出");
        Assert.Equal(pinnedX, translate.X);
        Assert.Equal(pinnedText, overlay.FindControl<TextBlock>("HoverTipText")!.Text);
        Assert.Null(missEvent); // 驻留中不触发 IsActive=false
        Assert.True(chart.Plot.PlottableList.OfType<ScottPlot.Plottables.Crosshair>().Single().IsVisible,
            "死区未命中 crosshair 应保持可见");

        for (var i = 0; i < 90; i++)
        {
            chart.TickTipInterpolation();
        }

        var sparseDataRect = chart.Plot.LastRender.DataRect;
        Assert.True(Math.Abs(translate.X - pinnedX) < 1.5, "驻留全程 X 应保持旧节点锚定");
        Assert.True(Math.Abs(translate.Y - sparseDataRect.Top) < 1.5,
            $"驻留期间气泡 Y 应跟随光标上移（落定数据区顶部），实测 {translate.Y:0.#} vs {sparseDataRect.Top:0.#}");

        // 命中第二点：文案与事件切到新点，浮窗保持可见（X 从旧锚点滑过去，不闪现）
        chart.ShowHoverAt(new ScottPlot.Coordinates(day15, points[1].TotalTokens));
        Assert.True(overlay.IsVisible);
        Assert.True(overlay.Opacity > 0.99, "换点滑行期间浮窗不应淡出");
        Assert.Equal(TrendChart.DailyTooltipText(points[1]), overlay.FindControl<TextBlock>("HoverTipText")!.Text);

        // 离开图表区域（PointerExited 同入口）→ 才淡出（headless 动画钟尾段不保证精确落定 0，容差判定）
        chart.HideHover();
        PumpUntilOverlaySettled(overlay, targetOpacity: 0);
        Assert.True(overlay.Opacity < 0.05, "离开图表区域后浮窗应淡出");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Tooltip_follows_mouse_vertically_while_x_stays_node_anchored()
    {
        // 规格 §3(2) 上下跟随（ZCode 观感）：同一节点上鼠标纵向移动 → 气泡 Y 弹簧跟随光标
        // （SmoothDamp 加速+刹车），落定于夹回目标；X 全程锚定节点不随鼠标（左右维持 §3(1)/(6) 逻辑）
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var overlay = chart.FindControl<Border>("HoverTip")!;
        var translate = Assert.IsType<Avalonia.Media.TranslateTransform>(overlay.RenderTransform);
        var first = ChartTestHelpers.FirstRealPoint(points);
        var day = DateTime.Parse(first.Day, CultureInfo.InvariantCulture).ToOADate();

        chart.ShowHoverAt(new ScottPlot.Coordinates(day, first.TotalTokens));
        Dispatcher.UIThread.RunJobs();
        Assert.True(overlay.IsVisible);
        var anchorX = translate.X;
        var startY = translate.Y;

        // 同一节点 X（命中不变）、光标纵向移到轴上方（数据 Y 超上限 → 目标夹回数据区顶部）
        chart.ShowHoverAt(new ScottPlot.Coordinates(day, 1e12));
        for (var i = 0; i < 90; i++)
        {
            chart.TickTipInterpolation();
        }

        var dataRect = chart.Plot.LastRender.DataRect;
        Assert.True(startY - translate.Y > 50,
            $"跟随位移应可感知（不是钉死在节点 Y）：{startY:0.#} → {translate.Y:0.#}");
        Assert.True(Math.Abs(translate.Y - dataRect.Top) < 1.5,
            $"气泡 Y 应跟随光标落定于数据区顶部，实测 {translate.Y:0.#} vs {dataRect.Top:0.#}");
        Assert.True(Math.Abs(translate.X - anchorX) < 1.5, "X 应保持节点锚定，不随鼠标移动");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static Avalonia.Media.Color ToAvalonia(ScottPlot.Color color) =>
        Avalonia.Media.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);

    /// <summary>
    /// 浮窗显隐过渡（chart-hovertip 的 120ms Opacity transition）走满（motion design 测试基建）：
    /// headless 下动画钟只在 RunJobs / 渲染 tick 推进且推进不均匀——隔离跑每批推进 ~30ms，
    /// 全量跑因其他窗口的任务抢占每批推进很小（实测全量跑 20 轮泵不完 120ms 过渡），
    /// 墙钟 Sleep 无效——按显式目标值循环泵到位再补 2 轮让合成器跟上（上限 500 轮防死循环）；
    /// 目标值必须显式传入（HideHover 后 IsVisible 仍 true，读控件状态推不出"淡出"方向）。
    /// </summary>
    private static void PumpUntilOverlaySettled(Border overlay, double targetOpacity)
    {
        var settled = false;
        for (var i = 0; i < 500 && !(settled = Math.Abs(overlay.Opacity - targetOpacity) <= 0.001); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        if (settled)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    // ── 空态（审查 P1-2：空集与"全补零窗口"都呈诚实空态，环境无关）──

    [AvaloniaFact]
    public void Empty_state_hides_axes_and_grid_chrome()
    {
        // P0-3：空数据时 ApplyChartChrome 照跑会把 ScottPlot 默认 ±10 坐标系画出来，
        // 空卡片看起来像"有数据但全是 0"的图。空态 = 纯卡片底 + 居中两行文案，
        // 轴与水平网格一并隐藏；有数据时照旧可见（不把 chrome 永久关掉）。
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = Array.Empty<DailyUsagePoint>() };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        Assert.False(chart.Plot.Axes.Bottom.IsVisible, "空态不应暴露 X 轴刻度体系");
        Assert.False(chart.Plot.Axes.Left.IsVisible, "空态不应暴露 Y 轴刻度体系");
        Assert.False(chart.Plot.Grid.YAxisStyle.IsVisible, "空态不应画水平网格");

        // 同一个实例换数据（RebuildPlot 复用同一 Plot 实例：plot.Clear() 不清轴的可见性，
        // 一次性关掉就再也不回来——这是空态→有数据这条最常见的真实路径）
        chart.DailyPoints = ChartTestHelpers.DailyPoints30();
        Dispatcher.UIThread.RunJobs();

        Assert.True(chart.Plot.Axes.Bottom.IsVisible, "有数据后 X 轴应恢复");
        Assert.True(chart.Plot.Axes.Left.IsVisible, "有数据后 Y 轴应恢复");
        Assert.True(chart.Plot.Grid.YAxisStyle.IsVisible, "有数据后水平网格应恢复");

        // 小时形态换轴路径（BuildHourlyLine 不替换 bottom 轴实例，最易漏）
        chart.Mode = TrendChartMode.HourlyTokens;
        chart.HourlyDistribution = HourlyFixture();
        Dispatcher.UIThread.RunJobs();
        Assert.True(chart.Plot.Axes.Bottom.IsVisible, "小时形态换数据后 X 轴应恢复");
        Assert.True(chart.Plot.Axes.Left.IsVisible);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Empty_data_shows_honest_empty_state_and_handles_hover()
    {
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = Array.Empty<DailyUsagePoint>() };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var emptyText = ChartTestHelpers.FindTextBlock(chart, "暂无数据");
        Assert.NotNull(emptyText);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(emptyText!), "空态文案应有效可见");

        // 空态悬停不命中、不崩
        var exception = Record.Exception(() => chart.ShowHoverAt(new ScottPlot.Coordinates(0, 0)));
        Assert.Null(exception);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(TrendChartMode.DailyTokens)]
    [InlineData(TrendChartMode.CostUsd)]
    public void All_padded_window_shows_honest_empty_state_instead_of_fake_zero_line(TrendChartMode mode)
    {
        // I-028 全补零窗口（有界范围内无 facts）：画全零线是伪信息 → 诚实空态（审查 P1-2 定案）
        var chart = new TrendChart { Mode = mode, DailyPoints = ChartTestHelpers.AllPadded30() };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var emptyText = ChartTestHelpers.FindTextBlock(chart, "暂无数据");
        Assert.NotNull(emptyText);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(emptyText!), "全补零窗口应显示诚实空态");
        Assert.True(TrendChart.IsEffectivelyEmpty(chart.DailyPoints));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── I-028 真实数据联调冒烟（环境无关：只断言渲染不崩 + 控件状态与 facts 形状一致）──

    [AvaloniaFact]
    public void Real_query_service_smoke_renders_consistent_with_facts_shape()
    {
        var query = AppServices.Get<IUsageQueryService>();
        var to = DateOnly.FromDateTime(DateTime.Today);
        var filter = new UsageQueryFilter
        {
            FromDay = UsageQueryFilter.FormatDay(to.AddDays(-29)),
            ToDay = UsageQueryFilter.FormatDay(to),
        };
        // 查询在 worker 线程执行（纯服务代码），结果回 UI 线程建控件
        var points = System.Threading.Tasks.Task.Run(() => query.GetDailySeriesAsync(filter)).GetAwaiter().GetResult();

        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var emptyText = ChartTestHelpers.FindTextBlock(chart, "暂无数据");
        Assert.NotNull(emptyText);

        // I-028：双端有界 → 每个自然日都有点（补零展开），与控件收到的点数一致；
        // 空态可见性 = "全补零窗口"谓词，与 facts 实际形状一致（不假设本机有无数据）
        Assert.Equal(30, points.Count);
        Assert.Equal(TrendChart.IsEffectivelyEmpty(points), ChartTestHelpers.IsEffectivelyVisible(emptyText!));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── IUsageQueryService 桩注入（审查 P1-2：空序列 / 部分数据两条路径，环境无关）──

    /// <summary>IUsageQueryService 测试桩：仅日序列可配置，其余成员不被本组用例触达。</summary>
    private sealed class StubUsageQueryService : IUsageQueryService
    {
        private readonly IReadOnlyList<DailyUsagePoint> _daily;

        public StubUsageQueryService(IReadOnlyList<DailyUsagePoint> daily) => _daily = daily;

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(_daily);

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult { Hours = Array.Empty<HourlyUsageBucket>() });
    }

    [AvaloniaFact]
    public void Query_service_all_padded_series_flows_into_honest_empty_state()
    {
        // 空序列路径：stub 注入全补零窗口（VM 真实查询模式；stub 返回已完成 task，LoadAsync 同步走完）
        var vm = new ChartPreviewViewModel(new StubUsageQueryService(ChartTestHelpers.AllPadded30()))
        {
            UseFixtureData = false,
            RangeDays = 30,
        };
        Assert.NotNull(vm.DailyPoints);
        Assert.Equal(30, vm.DailyPoints!.Count);

        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = vm.DailyPoints };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var emptyText = ChartTestHelpers.FindTextBlock(chart, "暂无数据");
        Assert.NotNull(emptyText);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(emptyText!), "空序列路径应呈诚实空态");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Query_service_partial_series_draws_padded_chart_without_empty_state()
    {
        // 部分数据路径：稀疏 facts → 补零图（空态不出现，补零日淡点可悬停辨识）
        var vm = new ChartPreviewViewModel(new StubUsageQueryService(ChartTestHelpers.DailyPoints30()))
        {
            UseFixtureData = false,
            RangeDays = 30,
        };
        Assert.NotNull(vm.DailyPoints);
        Assert.Equal(30, vm.DailyPoints!.Count);
        Assert.Contains(vm.DailyPoints, p => p.HasData);

        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = vm.DailyPoints };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var emptyText = ChartTestHelpers.FindTextBlock(chart, "暂无数据");
        Assert.NotNull(emptyText);
        Assert.False(ChartTestHelpers.IsEffectivelyVisible(emptyText!), "部分有数据的补零图不是空态");

        // 补零日悬停辨识：文案明示 zero-filled
        TrendChartHoverEventArgs? received = null;
        chart.HoverChanged += (_, e) => received = e;
        var padded = ChartTestHelpers.FirstPaddedPoint(vm.DailyPoints);
        chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(padded.Day, CultureInfo.InvariantCulture).ToOADate(), 0));
        Assert.NotNull(received);
        Assert.Contains(Translations.Instance.ChartTipNoDataZeroFilled, received!.LabelText);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 未知成本契约：tooltip 四态 × 两形态（审查 P1-1，逐态锁定）──

    [AvaloniaFact]
    public void Daily_tooltip_distinguishes_all_four_cost_states()
    {
        const string day = "2026-10-13";
        DailyUsagePoint Point(AggregatedCost cost) => new()
        {
            Day = day,
            InputTokens = 1_234,
            OutputTokens = 567,
            TotalTokens = 1_801,
            HasData = true,
            Cost = cost,
        };

        // (x, false) 已知成本：$x.xx，无 "+"、无 unknown 文案；token 数为紧凑短单位（B-006 问题 3）。
        // 浮窗 token 数随 UI 语言（Q-008），紧凑单位断言显式锁定英文口径（中文口径另有专项用例）
        WithLanguage(AppLanguage.English, () =>
        {
            var known = TrendChart.DailyTooltipText(Point(new AggregatedCost(12.5m, hasUnknownCost: false)));
            Assert.Contains("$12.50", known);
            Assert.Contains("in  1.23K", known);
            Assert.Contains("out 567", known);
            Assert.Contains("tot 1.8K tok", known);
            Assert.DoesNotContain("+", known);
            Assert.DoesNotContain("cost unknown", known);
        });

        // (x, true) 部分未知：$x.xx+（已知成本下限）
        var mixed = TrendChart.DailyTooltipText(Point(new AggregatedCost(12.5m, hasUnknownCost: true)));
        Assert.Contains("$12.50+", mixed);

        // (null, true) 全未知（有用量但计价缺失）：明示 cost unknown，绝不折 0
        var allUnknown = TrendChart.DailyTooltipText(Point(new AggregatedCost(null, hasUnknownCost: true)));
        Assert.Contains(Translations.Instance.ChartTipCostUnknown, allUnknown);
        Assert.DoesNotContain("$", allUnknown);

        // (null, false) 无成本数据：既非 0 也非未知 → 不显示成本行
        var noCost = TrendChart.DailyTooltipText(Point(AggregatedCost.Empty));
        Assert.DoesNotContain("$", noCost);
        Assert.DoesNotContain("cost unknown", noCost);

        // HasData=false 补零日：与成本四态无关，明示 no data
        Assert.Contains(Translations.Instance.ChartTipNoDataZeroFilled, TrendChart.DailyTooltipText(new DailyUsagePoint
        {
            Day = day,
            HasData = false,
            Cost = AggregatedCost.Empty,
        }));
    }

    [AvaloniaFact]
    public void Cost_tooltip_distinguishes_all_four_cost_states()
    {
        const string day = "2026-10-13";
        DailyUsagePoint Point(AggregatedCost cost) => new() { Day = day, HasData = true, Cost = cost };

        // (x, false) → "$x.xx"
        Assert.Contains("$0.00", TrendChart.CostTooltipText(Point(new AggregatedCost(0m, hasUnknownCost: false))));
        // (x, true) → "$x.xx+"
        Assert.Contains("$1.23+", TrendChart.CostTooltipText(Point(new AggregatedCost(1.23m, hasUnknownCost: true))));
        // (null, true) 全未知 → "cost unknown"（不再与无成本数据塌缩成同一句）
        var allUnknown = TrendChart.CostTooltipText(Point(new AggregatedCost(null, hasUnknownCost: true)));
        Assert.Contains(Translations.Instance.ChartTipCostUnknown, allUnknown);
        // (null, false) 无成本数据 → "no cost data"
        var noCost = TrendChart.CostTooltipText(Point(AggregatedCost.Empty));
        Assert.Contains(Translations.Instance.ChartTipNoCostData, noCost);
        Assert.DoesNotContain("cost unknown", noCost);
    }

    // ── 成本形态连线（补零日 $0 填充 + 未知成本日断线，审查 P2-7 残留语义）──

    [AvaloniaFact]
    public void Cost_line_runs_only_span_consecutive_known_days()
    {
        // T T F T T T → (0,1) 与 (3,5) 两段
        var runs = TrendChart.BuildLineRuns(new[] { true, true, false, true, true, true });
        Assert.Equal(new[] { (0, 1), (3, 5) }, runs);

        // 孤立已知日不成线（点标记呈现）
        Assert.Empty(TrendChart.BuildLineRuns(new[] { true }));
        Assert.Empty(TrendChart.BuildLineRuns(new[] { false, true, false }));
        // 全 false / 尾部连续
        Assert.Empty(TrendChart.BuildLineRuns(new[] { false, false, false }));
        Assert.Equal(new[] { (1, 2) }, TrendChart.BuildLineRuns(new[] { false, true, true }));
    }

    [AvaloniaFact]
    public void Cost_mode_line_fills_padded_days_at_zero()
    {
        // 补零日 $0 填充（Overview Cost trend 定案）：fixture 已知日 offsets {2,5,6,11,17,23,29}、
        // 其余全为补零日 → 成线日 = 已知 ∪ 补零 = 全窗口，单段 (0,29) 成线；marker 散点 LineWidth=0
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.CostUsd, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var lineScatters = chart.Plot.PlottableList
            .OfType<ScottPlot.Plottables.Scatter>()
            .Where(s => s.LineWidth > 0)
            .ToList();
        var expectedRuns = TrendChart.BuildLineRuns(points
            .Select(p => p.Cost.KnownCostUsd is { } || p.Cost.IsEmpty)
            .ToList());
        Assert.Equal(new[] { (0, points.Count - 1) }, expectedRuns);
        Assert.Equal(expectedRuns.Count, lineScatters.Count);

        // 补零日淡点落在连线上且仍可悬停，文案保持 "no cost data"（该日无成本数据，0 是补零约定）
        TrendChartHoverEventArgs? received = null;
        chart.HoverChanged += (_, e) => received = e;
        var padded = ChartTestHelpers.FirstPaddedPoint(points);
        chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(padded.Day, CultureInfo.InvariantCulture).ToOADate(), 0));
        Assert.NotNull(received);
        Assert.True(received!.IsActive);
        Assert.Contains(Translations.Instance.ChartTipNoCostData, received.LabelText);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Cost_mode_line_still_breaks_unknown_cost_days()
    {
        // 未知成本契约（未知绝不折 0）：5 天窗口 已知/补零/已知/未知/已知 →
        // 补零日桥接成线段 (0..2)；未知成本日断线，其后孤立已知日不成线（点标记呈现）
        DailyUsagePoint P(string day, AggregatedCost cost, bool hasData) => new()
        {
            Day = day,
            Cost = cost,
            HasData = hasData,
        };
        var points = new[]
        {
            P("2026-10-12", new AggregatedCost(1.0m, hasUnknownCost: false), hasData: true),
            P("2026-10-13", AggregatedCost.Empty, hasData: false),
            P("2026-10-14", new AggregatedCost(2.0m, hasUnknownCost: false), hasData: true),
            P("2026-10-15", new AggregatedCost(null, hasUnknownCost: true), hasData: true),
            P("2026-10-16", new AggregatedCost(3.0m, hasUnknownCost: false), hasData: true),
        };
        var chart = new TrendChart { Mode = TrendChartMode.CostUsd, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var lineScatters = chart.Plot.PlottableList
            .OfType<ScottPlot.Plottables.Scatter>()
            .Where(s => s.LineWidth > 0)
            .ToList();
        Assert.Single(lineScatters); // 仅 (0..2) 一段；未知日断线后，孤立已知日 (4) 不成线

        // 未知成本日仍可悬停，文案 "cost unknown"（(null,true) 与无成本数据不塌缩）
        TrendChartHoverEventArgs? received = null;
        chart.HoverChanged += (_, e) => received = e;
        chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(points[3].Day, CultureInfo.InvariantCulture).ToOADate(), 0));
        Assert.NotNull(received);
        Assert.True(received!.IsActive);
        Assert.Contains(Translations.Instance.ChartTipCostUnknown, received.LabelText);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── I-032 视觉升级：单调平滑曲线（纯函数）──

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void MonotoneDenseCurve_degenerate_input_returns_original(int n)
    {
        var xs = Enumerable.Range(0, n).Select(i => (double)i).ToArray();
        var ys = Enumerable.Range(0, n).Select(i => (double)(i * 100)).ToArray();
        var (outXs, outYs) = TrendChart.MonotoneDenseCurve(xs, ys);
        Assert.Same(xs, outXs);
        Assert.Same(ys, outYs);
    }

    [AvaloniaFact]
    public void MonotoneDenseCurve_non_increasing_x_returns_original()
    {
        var xs = new double[] { 1, 2, 2, 3 };
        var ys = new double[] { 1, 2, 3, 4 };
        var (outXs, outYs) = TrendChart.MonotoneDenseCurve(xs, ys);
        Assert.Same(xs, outXs);
        Assert.Same(ys, outYs);
    }

    [AvaloniaFact]
    public void MonotoneDenseCurve_stays_within_data_envelope_on_spike()
    {
        // 尖峰形状（usage 常态）：长期零 + 单日尖峰 + 回落——Catmull-Rom 会甩出负值/过冲，
        // 单调插值必须贴合包络（峰值不过冲、零段不产生伪负 token）
        var xs = Enumerable.Range(0, 30).Select(i => (double)i).ToArray();
        var ys = new double[30];
        ys[26] = 4.4e8;
        ys[27] = 4.3e8;
        ys[28] = 4.35e8;
        ys[29] = 2.6e8;
        var (denseXs, denseYs) = TrendChart.MonotoneDenseCurve(xs, ys);

        Assert.Equal((30 - 1) * 16 + 1, denseXs.Length);
        Assert.Equal(denseXs.Length, denseYs.Length);
        Assert.Equal(0, denseXs[0]);
        Assert.Equal(29, denseXs[^1]);
        Assert.Equal(4.4e8, denseYs.Max());
        Assert.Equal(0, denseYs.Min());
        for (var i = 1; i < denseXs.Length; i++)
        {
            Assert.True(denseXs[i] > denseXs[i - 1], "密集化采样 x 必须严格递增");
        }
    }

    [AvaloniaFact]
    public void MonotoneDenseCurve_two_points_smooth_without_overshoot()
    {
        var (denseXs, denseYs) = TrendChart.MonotoneDenseCurve([0, 1], [0, 1e6]);
        Assert.Equal(17, denseXs.Length);
        Assert.Equal(0, denseXs[0]);
        Assert.Equal(1, denseXs[^1]);
        Assert.Equal(0, denseYs.Min());
        Assert.Equal(1e6, denseYs.Max());
    }

    // ── I-032 视觉升级：只读交互 + Web 风 chrome + 平滑/面积填充按形态生效 ──

    [AvaloniaFact]
    public void Input_interactions_are_disabled_and_chrome_is_web_style()
    {
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = ChartTestHelpers.DailyPoints30() };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        Assert.False(chart.InteractionsEnabled); // 滚轮缩放/拖拽平移/双击复位全部关闭

        Assert.False(chart.Plot.Grid.XAxisStyle.IsVisible); // 纵向网格关，只剩水平网格
        Assert.True(chart.Plot.Grid.YAxisStyle.IsVisible);
        foreach (var frameWidth in new[]
        {
            chart.Plot.Axes.Bottom.FrameLineStyle.Width,
            chart.Plot.Axes.Top.FrameLineStyle.Width,
            chart.Plot.Axes.Left.FrameLineStyle.Width,
            chart.Plot.Axes.Right.FrameLineStyle.Width,
        })
        {
            Assert.Equal(0f, frameWidth); // 四边无边框
        }

        Assert.Equal(0f, chart.Plot.Axes.Bottom.MajorTickStyle.Length); // 无刻度须

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Pointer_wheel_over_plot_bubbles_out_to_page_containers()
    {
        // 只读视图（I-032）禁了缩放，但 AvaPlot 库默认仍把 PointerWheelChanged 标记 Handled
        // 吞掉（与其输入处理器开关无关）——页面根 ScrollViewer 只响应未处理滚轮，图上滚轮
        // 滚动会整段失效。本测试走真实 headless 输入管线：滚轮命中 AvaPlot 后必须能冒泡
        // 到窗口级容器（handledEventsToo=false，Handled 即视为未到达）。
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = ChartTestHelpers.DailyPoints30() };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var bubbled = false;
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => bubbled = true);

        window.MouseWheel(new Point(window.Width / 2, window.Height / 2), new Vector(0, -120));
        Dispatcher.UIThread.RunJobs();

        Assert.True(bubbled, "滚轮事件被图表吞掉，未冒泡到页面级容器（图上滚轮无法滚动页面）");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Line_series_are_dense_smooth_and_area_fill_depends_on_mode()
    {
        var points = ChartTestHelpers.DailyPoints30();
        var daily = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(daily, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var dailyLine = daily.Plot.PlottableList.OfType<ScottPlot.Plottables.Scatter>().Single(s => s.LineWidth > 0);
        Assert.True(dailyLine.Data.GetScatterPoints().Count > points.Count, "总量折线应经单调密集化平滑");
        Assert.False(dailyLine.FillY, "柱形态不叠面积填充");
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var hourly = new TrendChart { Mode = TrendChartMode.HourlyTokens, HourlyDistribution = HourlyFixture() };
        var hourlyWindow = CreateWindow(hourly, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(hourlyWindow);

        var hourlyLine = hourly.Plot.PlottableList.OfType<ScottPlot.Plottables.Scatter>().Single(s => s.LineWidth > 0);
        Assert.True(hourlyLine.Data.GetScatterPoints().Count > 24, "小时折线应经单调密集化平滑");
        Assert.True(hourlyLine.FillY, "小时形态应有零基线面积填充");
        Assert.True(hourlyLine.FillYColor.Alpha < 255, "面积填充 = 线色半透明");
        hourlyWindow.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 格式化口径（对账一致）──

    // ── 紧凑数值单位制（调用时点按 UI 语言取制；单例语言每用例保存恢复，同 TranslationsTests 约定）──

    private void WithLanguage(AppLanguage language, Action action)
    {
        var original = Translations.Instance.CurrentLanguage;
        Translations.Instance.SetLanguage(language);
        try
        {
            action();
        }
        finally
        {
            Translations.Instance.SetLanguage(original);
        }
    }

    [AvaloniaTheory]
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(1_500, "1.5K")]
    [InlineData(1_261_301, "1.26M")]
    [InlineData(999_999_999, "1B")]   // 尾数取整越档（"1000M"）升入上一档
    [InlineData(1_261_301_001, "1.26B")]
    [InlineData(2_500_000_000_000, "2.5T")]
    [InlineData(1e15, "1Q")]
    public void FormatCompactTokens_english_uses_short_scale_units(double value, string expected) =>
        WithLanguage(AppLanguage.English, () => Assert.Equal(expected, TrendChart.FormatCompactTokens(value)));

    [AvaloniaTheory]
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(9_999, "9,999")]
    [InlineData(12_345, "1.23万")]
    [InlineData(999_999, "100万")]
    [InlineData(1_234_567, "123.5万")]
    [InlineData(58_820_000, "5882万")]     // 尾数 ≥1000 取整（5882万 而非 5.88千万，口语直读）
    [InlineData(12_345_678, "1235万")]
    [InlineData(99_995_000, "1亿")]        // 9999.5万 越档进位
    [InlineData(5_000_000_000, "50亿")]
    [InlineData(1_261_301_001, "12.61亿")]
    [InlineData(15_000_000_000, "150亿")]
    [InlineData(123_400_000_000, "1234亿")]
    [InlineData(999_999_999_999, "1万亿")] // 9999.99…亿 越档进位
    [InlineData(2_500_000_000_000, "2.5万亿")]
    public void FormatCompactTokens_chinese_uses_wan_yi_scale_units(double value, string expected) =>
        WithLanguage(AppLanguage.SimplifiedChinese, () => Assert.Equal(expected, TrendChart.FormatCompactTokens(value)));

    // 日语万进制：万/億/兆 每档 10^4（日式四位分组；千不设档，1万 起缩写），兆为阶梯末端；小数点 "."。
    [AvaloniaTheory]
    [InlineData(0, "0")]
    [InlineData(9_999, "9,999")]
    [InlineData(12_345, "1.23万")]
    [InlineData(99_995_000, "1億")]        // 9999.5万 越档进位
    [InlineData(2_500_000_000_000, "2.5兆")]
    public void FormatCompactTokens_japanese_uses_man_oku_chou_units(double value, string expected) =>
        WithLanguage(AppLanguage.Japanese, () => Assert.Equal(expected, TrendChart.FormatCompactTokens(value)));

    // 韩语万进制：만/억/조（천(千) 不设档，만 起缩写），조为阶梯末端；谚文单位（非汉字 萬）；小数点 "."。
    [AvaloniaTheory]
    [InlineData(0, "0")]
    [InlineData(9_999, "9,999")]
    [InlineData(12_345, "1.23만")]
    [InlineData(300_000_000, "3억")]
    [InlineData(99_995_000, "1억")]        // 9999.5만 越档进位
    [InlineData(2_500_000_000_000, "2.5조")]
    public void FormatCompactTokens_korean_uses_man_eok_jo_units(double value, string expected) =>
        WithLanguage(AppLanguage.Korean, () => Assert.Equal(expected, TrendChart.FormatCompactTokens(value)));

    // 巴葡千进制：mil/mi(milhão)/bi(bilhão)/tri(trilhão)；小数逗号，数字与单位间不换行空格。
    [AvaloniaTheory]
    [InlineData(0, "0")]
    [InlineData(1_500, "1,5\u00A0mil")]
    [InlineData(50_000, "50\u00A0mil")]
    [InlineData(999_999, "1\u00A0mi")]     // 1000 mil 越档进位
    [InlineData(1_500_000, "1,5\u00A0mi")]
    [InlineData(2_500_000_000, "2,5\u00A0bi")]
    [InlineData(3_000_000_000_000, "3\u00A0tri")]
    public void FormatCompactTokens_brazilian_portuguese_uses_mil_mi_bi_units(double value, string expected) =>
        WithLanguage(AppLanguage.BrazilianPortuguese, () => Assert.Equal(expected, TrendChart.FormatCompactTokens(value)));

    // 俄语千进制：тыс./млн/млрд/трлн；小数逗号，数字与单位间不换行空格。
    [AvaloniaTheory]
    [InlineData(0, "0")]
    [InlineData(1_500, "1,5\u00A0тыс.")]
    [InlineData(999_999, "1\u00A0млн")]    // 1000 тыс. 越档进位
    [InlineData(2_500_000, "2,5\u00A0млн")]
    [InlineData(3_000_000_000_000, "3\u00A0трлн")]
    public void FormatCompactTokens_russian_uses_thousand_million_billion_units(double value, string expected) =>
        WithLanguage(AppLanguage.Russian, () => Assert.Equal(expected, TrendChart.FormatCompactTokens(value)));

    // 西语千进制：mil/M(millones)/MM(mil millones/millardo)/B(billón=10^12，西语长级)；小数逗号 + 不换行空格。
    [AvaloniaTheory]
    [InlineData(0, "0")]
    [InlineData(1_500, "1,5\u00A0mil")]
    [InlineData(2_500_000, "2,5\u00A0M")]
    [InlineData(999_999_999, "1\u00A0MM")]  // 1000 M 越档进位
    [InlineData(3_000_000_000, "3\u00A0MM")]
    [InlineData(3_000_000_000_000, "3\u00A0B")]
    public void FormatCompactTokens_spanish_uses_mil_m_mm_b_units(double value, string expected) =>
        WithLanguage(AppLanguage.Spanish, () => Assert.Equal(expected, TrendChart.FormatCompactTokens(value)));

    // 德语千进制：Tsd./Mio./Mrd./Bio.(Billion=10^12，德语长级)；小数逗号 + 不换行空格。
    [AvaloniaTheory]
    [InlineData(0, "0")]
    [InlineData(1_500, "1,5\u00A0Tsd.")]
    [InlineData(2_500_000, "2,5\u00A0Mio.")]
    [InlineData(999_999_999, "1\u00A0Mrd.")] // 1000 Mio. 越档进位
    [InlineData(3_000_000_000, "3\u00A0Mrd.")]
    [InlineData(3_000_000_000_000, "3\u00A0Bio.")]
    public void FormatCompactTokens_german_uses_ts_mio_mrd_units(double value, string expected) =>
        WithLanguage(AppLanguage.German, () => Assert.Equal(expected, TrendChart.FormatCompactTokens(value)));

    // 法语千进制：k/M/Md(milliard)/bn(billion=10^12，法语长级)；小数逗号 + 不换行空格。
    [AvaloniaTheory]
    [InlineData(0, "0")]
    [InlineData(1_500, "1,5\u00A0k")]
    [InlineData(2_500_000, "2,5\u00A0M")]
    [InlineData(999_999_999, "1\u00A0Md")]  // 1000 M 越档进位
    [InlineData(3_000_000_000, "3\u00A0Md")]
    [InlineData(3_000_000_000_000, "3\u00A0bn")]
    public void FormatCompactTokens_french_uses_k_m_md_bn_units(double value, string expected) =>
        WithLanguage(AppLanguage.French, () => Assert.Equal(expected, TrendChart.FormatCompactTokens(value)));

    [AvaloniaTheory]
    [InlineData(12.5, false, "$12.50")]
    [InlineData(0, true, "$0.00+")]
    public void FormatCostUsd_uses_two_decimals_and_unknown_suffix(double known, bool hasUnknown, string expected) =>
        Assert.Equal(expected, TrendChart.FormatCostUsd((decimal)known, hasUnknown));

    [AvaloniaFact]
    public void FormatCostUsd_returns_null_when_no_known_cost() =>
        Assert.Null(TrendChart.FormatCostUsd(null, hasUnknownCost: true));
}
