using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using TokenPulse.App.Controls;
using TokenPulse.App.DependencyInjection;
using TokenPulse.App.Localization;
using TokenPulse.App.ViewModels;
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;
using Xunit;

namespace TokenPulse.App.Headless.Tests.Controls;

/// <summary>
/// D-004 TrendChart 的 headless 验证：
/// 1. 三形态渲染出图（系列色像素存在）+ 明暗两套；
/// 2. hover helper：裁剪翻转纯函数四象限、悬停事件与 tooltip 文案（token 千分位 / 成本四态口径）、
///    补零日文案辨识、成本形态断线；
/// 3. 明暗切换触发整图重建（轴文字色随令牌变化）；
/// 4. 空数据诚实空态（空集与全补零窗口，环境无关）；
/// 5. I-028 真实日序列联调冒烟（不依赖本机 facts 是否有数据）。
/// </summary>
public class TrendChartTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENPULSE_CHART_PREVIEW_DIR");

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
            && bg is IBrush brush ? brush : new SolidColorBrush(Color.Parse("#111c2e"));

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

    // ── hover helper：翻转纯函数（TC-002 遗留的右缘/上缘裁剪）──

    [AvaloniaFact]
    public void Tooltip_alignment_flips_at_right_and_top_edges()
    {
        const double left = 0, top = 0, width = 400, height = 200;

        // 左下区：气泡向右上伸（LowerLeft）
        Assert.Equal(ScottPlot.Alignment.LowerLeft, TrendChart.ComputeTooltipAlignment(100, 150, left, top, width, height));
        // 左上区：气泡向右下伸（UpperLeft，避免顶出数据区）
        Assert.Equal(ScottPlot.Alignment.UpperLeft, TrendChart.ComputeTooltipAlignment(100, 50, left, top, width, height));
        // 右下区：气泡向左上伸（LowerRight，避免右缘裁剪）
        Assert.Equal(ScottPlot.Alignment.LowerRight, TrendChart.ComputeTooltipAlignment(300, 150, left, top, width, height));
        // 右上区（最不利点）：气泡向左下伸（UpperRight）
        Assert.Equal(ScottPlot.Alignment.UpperRight, TrendChart.ComputeTooltipAlignment(300, 50, left, top, width, height));
        // 中心线上取任意一侧均为合法象限（稳定性）
        Assert.Equal(ScottPlot.Alignment.LowerLeft, TrendChart.ComputeTooltipAlignment(200, 200, left, top, width, height));
    }

    // ── hover helper：悬停管线（事件 + tooltip 文案口径）──

    [AvaloniaFact]
    public void Hover_on_real_day_fires_event_with_reconciliation_formatted_text()
    {
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        TrendChartHoverEventArgs? received = null;
        chart.HoverChanged += (_, e) => received = e;

        var real = ChartTestHelpers.FirstRealPoint(points);
        chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(real.Day, CultureInfo.InvariantCulture).ToOADate(), 0));

        Assert.NotNull(received);
        Assert.True(received!.IsActive);
        Assert.Contains("tot " + real.TotalTokens.ToString("N0", CultureInfo.InvariantCulture) + " tok", received.LabelText);
        Assert.Contains("in  " + real.InputTokens.ToString("N0", CultureInfo.InvariantCulture), received.LabelText);
        Assert.Contains($"${real.Cost.KnownCostUsd!.Value.ToString("F2", CultureInfo.InvariantCulture)}", received.LabelText);
        Assert.False(received.LabelText!.Contains('+'), "非未知成本日不应带 '+' 下限标记");

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
        Assert.Contains("no data (zero-filled)", received.LabelText);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hover_outside_data_hides_hover()
    {
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.DailyTokens, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        var lastEvent = default(TrendChartHoverEventArgs);
        chart.HoverChanged += (_, e) => lastEvent = e;

        // 远离数据 x 轴的位置（窗口最早日期再往前 10 年）→ 不命中 → IsActive=false
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
        Assert.Equal(Color.Parse("#66788f"), ToAvalonia(lightTickColor)); // Light AppMutedTextBrush
        Assert.NotEqual(darkTickColor, lightTickColor);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static Avalonia.Media.Color ToAvalonia(ScottPlot.Color color) =>
        Avalonia.Media.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);

    // ── 空态（审查 P1-2：空集与"全补零窗口"都呈诚实空态，环境无关）──

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
        Assert.Contains("no data (zero-filled)", received!.LabelText);

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

        // (x, false) 已知成本：$x.xx，无 "+"、无 unknown 文案
        var known = TrendChart.DailyTooltipText(Point(new AggregatedCost(12.5m, hasUnknownCost: false)));
        Assert.Contains("$12.50", known);
        Assert.DoesNotContain("+", known);
        Assert.DoesNotContain("cost unknown", known);

        // (x, true) 部分未知：$x.xx+（已知成本下限）
        var mixed = TrendChart.DailyTooltipText(Point(new AggregatedCost(12.5m, hasUnknownCost: true)));
        Assert.Contains("$12.50+", mixed);

        // (null, true) 全未知（有用量但计价缺失）：明示 cost unknown，绝不折 0
        var allUnknown = TrendChart.DailyTooltipText(Point(new AggregatedCost(null, hasUnknownCost: true)));
        Assert.Contains("cost unknown", allUnknown);
        Assert.DoesNotContain("$", allUnknown);

        // (null, false) 无成本数据：既非 0 也非未知 → 不显示成本行
        var noCost = TrendChart.DailyTooltipText(Point(AggregatedCost.Empty));
        Assert.DoesNotContain("$", noCost);
        Assert.DoesNotContain("cost unknown", noCost);

        // HasData=false 补零日：与成本四态无关，明示 no data
        Assert.Contains("no data (zero-filled)", TrendChart.DailyTooltipText(new DailyUsagePoint
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
        Assert.Contains("cost unknown", allUnknown);
        // (null, false) 无成本数据 → "no cost data"
        var noCost = TrendChart.CostTooltipText(Point(AggregatedCost.Empty));
        Assert.Contains("no cost data", noCost);
        Assert.DoesNotContain("cost unknown", noCost);
    }

    // ── 成本形态断线（审查 P2-7）：折线只连连续已知成本日 ──

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
    public void Cost_mode_line_does_not_connect_unknown_cost_days()
    {
        var points = ChartTestHelpers.DailyPoints30();
        var chart = new TrendChart { Mode = TrendChartMode.CostUsd, DailyPoints = points };
        var window = CreateWindow(chart, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        // fixture 已知日 offsets {2,5,6,11,17,23,29} → 仅 (5,6) 一段 ≥2 天成线；marker 散点 LineWidth=0
        var lineScatters = chart.Plot.PlottableList
            .OfType<ScottPlot.Plottables.Scatter>()
            .Where(s => s.LineWidth > 0)
            .ToList();
        var expectedRuns = TrendChart.BuildLineRuns(
            points.Select(p => p.Cost.KnownCostUsd is { }).ToList());
        Assert.Single(expectedRuns);
        Assert.Equal(expectedRuns.Count, lineScatters.Count);

        // 断线不牺牲可悬停性：(null,false) 成本日（补零日）仍可命中，文案 "no cost data"
        TrendChartHoverEventArgs? received = null;
        chart.HoverChanged += (_, e) => received = e;
        var padded = ChartTestHelpers.FirstPaddedPoint(points);
        chart.ShowHoverAt(new ScottPlot.Coordinates(DateTime.Parse(padded.Day, CultureInfo.InvariantCulture).ToOADate(), 0));
        Assert.NotNull(received);
        Assert.True(received!.IsActive);
        Assert.Contains("no cost data", received.LabelText);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 格式化口径（对账一致）──

    [AvaloniaTheory]
    [InlineData(0, "0")]
    [InlineData(1_234_567, "1,234,567")]
    [InlineData(-42, "-42")]
    public void FormatTokens_uses_thousands_separator(long tokens, string expected) =>
        Assert.Equal(expected, TrendChart.FormatTokens(tokens));

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
    [InlineData(9_999, "9999")]
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

    [AvaloniaTheory]
    [InlineData(12.5, false, "$12.50")]
    [InlineData(0, true, "$0.00+")]
    public void FormatCostUsd_uses_two_decimals_and_unknown_suffix(double known, bool hasUnknown, string expected) =>
        Assert.Equal(expected, TrendChart.FormatCostUsd((decimal)known, hasUnknown));

    [AvaloniaFact]
    public void FormatCostUsd_returns_null_when_no_known_cost() =>
        Assert.Null(TrendChart.FormatCostUsd(null, hasUnknownCost: true));
}
