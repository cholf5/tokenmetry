using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TokenPulse.App.Localization;
using TokenPulse.Core.Models;
using ScottPlot;
using ScottPlot.Plottables;

namespace TokenPulse.App.Controls;

/// <summary>TrendChart 的形态（D-004：三形态参数化，同一绘图管线）。</summary>
public enum TrendChartMode
{
    /// <summary>日趋势：输入/输出堆叠柱 + 总量折线（I-028 日序列，含补零日辨识）。</summary>
    DailyTokens,

    /// <summary>成本折线：按日已知成本（I-028 日序列的 Cost，未知成本语义见格式化）。</summary>
    CostUsd,

    /// <summary>小时折线：0..23 全量桶总 token（I-029 小时分布）。</summary>
    HourlyTokens,
}

/// <summary>悬停状态变化事件（宿主页状态行可订阅；LabelText 与图上 tooltip 同源同格式）。</summary>
public sealed class TrendChartHoverEventArgs : EventArgs
{
    /// <summary>true = 正在悬停某数据点；false = 鼠标离开 / 未命中。</summary>
    public bool IsActive { get; init; }

    /// <summary>命中的数据点下标（未命中为 -1）。</summary>
    public int Index { get; init; } = -1;

    /// <summary>tooltip 文本（未命中为 null）。</summary>
    public string? LabelText { get; init; }
}

/// <summary>
/// D-004 正式趋势图控件（ScottPlot 5）：日趋势（输入/输出堆叠柱 + 总量折线）、成本折线、小时折线
/// 三形态共用同一 AvaPlot 与 hover helper（crosshair + tooltip + 右缘/上缘裁剪翻转）。
/// 设计基准：docs/design/ccusage_analytics_prototype.html（.chart 的 barin/barout/line/point、
/// 图例三色 = :root blue/purple/cyan 变量 → AppAccentBlue/Purple/CyanBrush）。
/// 数据经样式属性传入（无 DataContext 依赖），变更即整图重建；缩放/平移保留 AvaPlot 默认交互。
/// ScottPlot 与 Avalonia 同名类型（Pixel/Color 等）按 D-002 纪律全限定处理。
/// </summary>
public partial class TrendChart : UserControl
{
    /// <summary>悬停拾取半径（屏幕像素），沿用 D-002 spike 与官方 Avalonia demo 的取值。</summary>
    private const float MaxHoverDistance = 15;

    /// <summary>堆叠柱宽（坐标单位；日期轴 1 单位 = 1 天，0.72 ≈ 原型柱宽/间距比例）。</summary>
    private const double DailyBarSize = 0.72;

    public static readonly StyledProperty<TrendChartMode> ModeProperty =
        AvaloniaProperty.Register<TrendChart, TrendChartMode>(nameof(Mode), TrendChartMode.DailyTokens);

    /// <summary>日形态数据源（I-028 GetDailySeriesAsync 输出；双端有界范围内补零日 HasData=false）。</summary>
    public static readonly StyledProperty<IReadOnlyList<DailyUsagePoint>?> DailyPointsProperty =
        AvaloniaProperty.Register<TrendChart, IReadOnlyList<DailyUsagePoint>?>(nameof(DailyPoints));

    /// <summary>小时形态数据源（I-029 GetHourlyDistributionAsync 输出，0..23 全量桶）。</summary>
    public static readonly StyledProperty<HourlyDistributionResult?> HourlyDistributionProperty =
        AvaloniaProperty.Register<TrendChart, HourlyDistributionResult?>(nameof(HourlyDistribution));

    public TrendChartMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public IReadOnlyList<DailyUsagePoint>? DailyPoints
    {
        get => GetValue(DailyPointsProperty);
        set => SetValue(DailyPointsProperty, value);
    }

    public HourlyDistributionResult? HourlyDistribution
    {
        get => GetValue(HourlyDistributionProperty);
        set => SetValue(HourlyDistributionProperty, value);
    }

    /// <summary>底层 ScottPlot Plot（宿主页缩放复位等场景；测试用于断言重建后的样式）。</summary>
    public Plot Plot => PlotControl.Plot;

    /// <summary>悬停命中变化（IsActive=false 表示离开/未命中）。</summary>
    public event EventHandler<TrendChartHoverEventArgs>? HoverChanged;

    private Crosshair? _crosshair;
    private Tooltip? _tooltip;

    /// <summary>悬停候选点（下标与 _hoverLabels 一一对应；拾取在像素 X 空间做最近点匹配）。</summary>
    private Coordinates[] _hoverCoords = Array.Empty<Coordinates>();
    private string[] _hoverLabels = Array.Empty<string>();

    public TrendChart()
    {
        InitializeComponent();

        ModeProperty.Changed.AddClassHandler<TrendChart>((c, _) => c.RebuildPlot());
        DailyPointsProperty.Changed.AddClassHandler<TrendChart>((c, _) => c.RebuildPlot());
        HourlyDistributionProperty.Changed.AddClassHandler<TrendChart>((c, _) => c.RebuildPlot());

        // ScottPlot 不支持 Avalonia 动态资源：主题变体变化时整图重建（chrome 色重新现取令牌）。
        // 订阅控件自身事件（EffectiveThemeVariant 继承 App/Window 的设置，含 app.TrySetTheme 路径）。
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        AttachedToVisualTree += (_, _) => RebuildPlot();
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => RebuildPlot();

    // ════════════════════════════ 绘图 ════════════════════════════

    /// <summary>整图重建：清空 → 按形态布数据 → 取令牌画 chrome → 共享 hover plottable → 刷新。</summary>
    public void RebuildPlot()
    {
        var plot = PlotControl.Plot;
        plot.Clear();
        _crosshair = null;
        _tooltip = null;
        _hoverCoords = Array.Empty<Coordinates>();
        _hoverLabels = Array.Empty<string>();

        // 先布数据（DateTimeTicksBottom 会替换 bottom 轴实例），再统一上 chrome，
        // 保证轴/网格/文字样式作用在最终轴实例上。
        switch (Mode)
        {
            case TrendChartMode.DailyTokens:
                BuildDailyTokens(plot);
                break;
            case TrendChartMode.CostUsd:
                BuildCostLine(plot);
                break;
            case TrendChartMode.HourlyTokens:
                BuildHourlyLine(plot);
                break;
            default:
                break;
        }

        ApplyChartChrome(plot);

        // 共享 hover 视觉（crosshair + tooltip），初始隐藏
        _crosshair = plot.Add.Crosshair(0, 0);
        _crosshair.IsVisible = false;
        _crosshair.LineWidth = 1;
        _crosshair.LinePattern = LinePattern.Dotted;
        _crosshair.LineColor = TokenColor("AppMutedTextBrush", new Color(130, 147, 173));
        _crosshair.MarkerShape = MarkerShape.OpenCircle;
        _crosshair.MarkerSize = 10;

        _tooltip = plot.Add.Tooltip(0, 0, string.Empty, 0, 0);
        _tooltip.IsVisible = false;
        _tooltip.LabelPadding = 6;

        EmptyState.IsVisible = _hoverLabels.Length == 0;
        PlotControl.Refresh();
    }

    /// <summary>
    /// 诚实空态语义（审查 P1-2 定案）：双端有界窗口内 <b>所有</b>点 HasData=false（I-028 全补零窗口）
    /// 等同"该时间范围内无数据"，与真正空集一样显示空态——补零是"有数据窗口内的空日"辨识手法，
    /// 全空窗口没有可画的数据，画全零线是伪信息。部分有数据则正常画补零图。
    /// </summary>
    public static bool IsEffectivelyEmpty(IReadOnlyList<DailyUsagePoint>? points) =>
        points is null || points.Count == 0 || points.All(p => !p.HasData);

    /// <summary>轴/网格/文字/背景 chrome（I-027 令牌，按当前 ActualThemeVariant 现取；缺资源时用原型 Dark 值兜底）。</summary>
    private void ApplyChartChrome(Plot plot)
    {
        // 透明背景：让 Avalonia 侧的卡片渐变（AppCardBackgroundBrush）透出来
        plot.FigureBackground.Color = new Color(0, 0, 0, 0);
        plot.DataBackground.Color = new Color(0, 0, 0, 0);

        var grid = TokenColor("AppChartGridLineBrush", new Color(35, 52, 75));
        var axis = TokenColor("AppChartAxisLineBrush", new Color(53, 70, 94));
        var text = TokenColor("AppMutedTextBrush", new Color(130, 147, 173));

        // 网格 ← 原型 .gridline #23344b（只保留主网格，与原型一致）
        plot.Grid.MajorLineColor = grid;
        plot.Grid.MajorLineWidth = 1f;
        plot.Grid.MinorLineColor = grid;
        plot.Grid.MinorLineWidth = 0f;

        // 轴（frame / 刻度 / 文字）← 原型 .axis #35465e 与 .chart text #8293ad
        foreach (var panel in new IAxis[] { plot.Axes.Bottom, plot.Axes.Left })
        {
            panel.FrameLineStyle.Color = axis;
            panel.MajorTickStyle.Color = axis;
            panel.MinorTickStyle.Color = axis;
            panel.TickLabelStyle.ForeColor = text;
            panel.TickLabelStyle.FontSize = 10f; // 原型 .chart text{font-size:10px}
        }
    }

    /// <summary>从 Tokens.axaml 现取画刷颜色（普通资源不随明暗翻转，主题翻转资源按变体命中）。</summary>
    private Color TokenColor(string key, Color fallback)
    {
        if (Application.Current?.TryGetResource(key, ActualThemeVariant, out var value) == true
            && value is Avalonia.Media.ISolidColorBrush brush)
        {
            var c = brush.Color;
            return new Color(c.R, c.G, c.B, c.A);
        }

        return fallback;
    }

    /// <summary>日趋势：输入/输出堆叠柱（蓝/紫）+ 总量折线（青）+ 补零日辨识（淡化小点）。</summary>
    private void BuildDailyTokens(Plot plot)
    {
        var points = DailyPoints ?? Array.Empty<DailyUsagePoint>();
        if (IsEffectivelyEmpty(points))
        {
            return; // 全补零窗口 = 无数据，诚实空态（见 IsEffectivelyEmpty）
        }

        var blue = TokenColor("AppAccentBlueBrush", new Color(76, 141, 255));    // 原型 var(--blue) 图例「输入」
        var purple = TokenColor("AppAccentPurpleBrush", new Color(167, 121, 255)); // 原型 var(--purple) 图例「输出」
        var cyan = TokenColor("AppAccentCyanBrush", new Color(87, 199, 232));    // 原型 var(--cyan) 图例「总量」
        var grid = TokenColor("AppChartGridLineBrush", new Color(35, 52, 75));
        var panel = TokenColor("AppPanelBrush", new Color(17, 28, 46));

        var xs = points.Select(p => DateTime.Parse(p.Day, CultureInfo.InvariantCulture).ToOADate()).ToArray();

        // 堆叠柱：输入从 0 起，输出以输入顶为 ValueBase（真堆叠；原型 .barout 的半透明覆盖是 SVG 实现技巧）
        var inputBars = new List<Bar>();
        var outputBars = new List<Bar>();
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!p.HasData)
            {
                continue; // 补零日 token=0，柱高为零；辨识走折线上的淡化小点
            }

            inputBars.Add(new Bar
            {
                Position = xs[i],
                Value = p.InputTokens,
                ValueBase = 0,
                Size = DailyBarSize,
                FillColor = blue,
                LineWidth = 0f,
            });
            outputBars.Add(new Bar
            {
                Position = xs[i],
                Value = p.OutputTokens,
                ValueBase = p.InputTokens,
                Size = DailyBarSize,
                FillColor = purple,
                LineWidth = 0f,
            });
        }

        if (inputBars.Count > 0)
        {
            plot.Add.Bars(inputBars.ToArray());
        }

        if (outputBars.Count > 0)
        {
            plot.Add.Bars(outputBars.ToArray());
        }

        // 总量折线（全部点连续——日历窗口语义：补零日的 0 让 x 轴回归真实日历，不做断线美化）
        var totals = points.Select(p => (double)p.TotalTokens).ToArray();
        var totalLine = plot.Add.Scatter(xs, totals);
        totalLine.LineWidth = 2f;
        totalLine.Color = cyan;
        totalLine.MarkerShape = MarkerShape.None;

        // 补零日辨识手法：真实日 = 数据色实心点；补零日 = 网格色淡化小点（I-028 HasData=false 可视化）
        var realCoords = new List<Coordinates>();
        var paddedCoords = new List<Coordinates>();
        for (var i = 0; i < points.Count; i++)
        {
            (points[i].HasData ? realCoords : paddedCoords).Add(new Coordinates(xs[i], totals[i]));
        }

        var realMarkers = plot.Add.ScatterPoints(realCoords, cyan);
        realMarkers.LineWidth = 0;
        realMarkers.MarkerShape = MarkerShape.FilledCircle;
        realMarkers.MarkerSize = 5f;
        realMarkers.MarkerLineColor = panel;
        realMarkers.MarkerLineWidth = 1.5f;

        var paddedMarkers = plot.Add.ScatterPoints(paddedCoords, grid);
        paddedMarkers.LineWidth = 0;
        paddedMarkers.MarkerShape = MarkerShape.FilledCircle;
        paddedMarkers.MarkerSize = 3f;

        _hoverCoords = xs.Select((x, i) => new Coordinates(x, totals[i])).ToArray();
        _hoverLabels = points.Select(DailyTooltipText).ToArray();

        plot.Axes.DateTimeTicksBottom();
        StyleTokenAxisFormatter(plot.Axes.Left, compact: true);
    }

    /// <summary>
    /// 成本折线（审查 P2-7 视觉诚实性）：折线只连<b>已知成本</b>日（<see cref="BuildLineRuns"/> 分段，
    /// null 成本日不再画到零轴）；已知日实心点、未知/无成本日淡点（位置锚在 0，供悬停定位）。
    /// 四态语义见 <see cref="CostTooltipText"/>：未知成本绝不静默折算为 0。
    /// </summary>
    private void BuildCostLine(Plot plot)
    {
        var points = DailyPoints ?? Array.Empty<DailyUsagePoint>();
        if (IsEffectivelyEmpty(points))
        {
            return; // 全补零窗口 = 无数据，诚实空态（见 IsEffectivelyEmpty）
        }

        var cyan = TokenColor("AppAccentCyanBrush", new Color(87, 199, 232));
        var grid = TokenColor("AppChartGridLineBrush", new Color(35, 52, 75));
        var panel = TokenColor("AppPanelBrush", new Color(17, 28, 46));

        var xs = points.Select(p => DateTime.Parse(p.Day, CultureInfo.InvariantCulture).ToOADate()).ToArray();
        // y 取已知成本（无已知成本的日为 0，仅作淡点/悬停锚点，不进连线）
        var ys = points.Select(p => (double)(p.Cost.KnownCostUsd ?? 0m)).ToArray();
        var knownFlags = points.Select(p => p.Cost.KnownCostUsd is { }).ToArray();

        // 连线只覆盖"连续已知成本"的段（≥2 天才成线；孤立已知日靠实心点呈现）
        foreach (var (start, end) in BuildLineRuns(knownFlags))
        {
            var run = plot.Add.Scatter(xs[start..(end + 1)], ys[start..(end + 1)]);
            run.LineWidth = 2f;
            run.Color = cyan;
            run.MarkerShape = MarkerShape.None;
        }

        var knownCoords = new List<Coordinates>();
        var unknownCoords = new List<Coordinates>();
        for (var i = 0; i < points.Count; i++)
        {
            (knownFlags[i] ? knownCoords : unknownCoords).Add(new Coordinates(xs[i], ys[i]));
        }

        var knownMarkers = plot.Add.ScatterPoints(knownCoords, cyan);
        knownMarkers.LineWidth = 0;
        knownMarkers.MarkerShape = MarkerShape.FilledCircle;
        knownMarkers.MarkerSize = 5f;
        knownMarkers.MarkerLineColor = panel;
        knownMarkers.MarkerLineWidth = 1.5f;

        var unknownMarkers = plot.Add.ScatterPoints(unknownCoords, grid);
        unknownMarkers.LineWidth = 0;
        unknownMarkers.MarkerShape = MarkerShape.FilledCircle;
        unknownMarkers.MarkerSize = 3f;

        _hoverCoords = xs.Select((x, i) => new Coordinates(x, ys[i])).ToArray();
        _hoverLabels = points.Select(CostTooltipText).ToArray();

        plot.Axes.DateTimeTicksBottom();
        StyleTokenAxisFormatter(plot.Axes.Left, dollar: true);
    }

    /// <summary>
    /// 连线分段纯函数（审查 P2-7 单测覆盖）：连续 true 的区段（双闭区间）中长度 ≥2 的段才能成线；
    /// 孤立 true 点不成线（由调用方用点标记呈现）。
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> BuildLineRuns(IReadOnlyList<bool> flags)
    {
        var runs = new List<(int Start, int End)>();
        var start = -1;
        for (var i = 0; i < flags.Count; i++)
        {
            if (flags[i])
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                if (i - start >= 2)
                {
                    runs.Add((start, i - 1));
                }

                start = -1;
            }
        }

        if (start >= 0 && flags.Count - start >= 2)
        {
            runs.Add((start, flags.Count - 1));
        }

        return runs;
    }

    /// <summary>小时折线：0..23 全量桶（I-029）总 token；粗粒度会话不在图上（页面层展示 unknown 占比）。</summary>
    private void BuildHourlyLine(Plot plot)
    {
        var hours = HourlyDistribution?.Hours ?? Array.Empty<HourlyUsageBucket>();
        if (hours.Count == 0)
        {
            return;
        }

        var cyan = TokenColor("AppAccentCyanBrush", new Color(87, 199, 232));
        var panel = TokenColor("AppPanelBrush", new Color(17, 28, 46));

        var xs = hours.Select(h => (double)h.Hour).ToArray();
        var ys = hours.Select(h => (double)h.TotalTokens).ToArray();

        var hourlyLine = plot.Add.Scatter(xs, ys);
        hourlyLine.LineWidth = 2f;
        hourlyLine.Color = cyan;
        hourlyLine.MarkerShape = MarkerShape.FilledCircle;
        hourlyLine.MarkerSize = 4f;
        hourlyLine.MarkerLineColor = panel;
        hourlyLine.MarkerLineWidth = 1f;

        _hoverCoords = xs.Select((x, i) => new Coordinates(x, ys[i])).ToArray();
        _hoverLabels = hours.Select(h =>
            $"{h.Hour:00}:00\n{FormatTokens(h.TotalTokens)} tok\n{h.SessionCount:N0} sessions").ToArray();

        plot.Axes.SetLimitsX(-0.5, 23.5);
        StyleTokenAxisFormatter(plot.Axes.Left, compact: true);
        StyleTokenAxisFormatter(plot.Axes.Bottom, integer: true);
    }

    /// <summary>y 轴（或小时形态的 x 轴）数字刻度格式：美元前缀 / 紧凑 token / 整数小时。</summary>
    private void StyleTokenAxisFormatter(IAxis axis, bool dollar = false, bool compact = false, bool integer = false)
    {
        if (axis.TickGenerator is not ScottPlot.TickGenerators.NumericAutomatic ticks)
        {
            return;
        }

        if (dollar)
        {
            ticks.LabelFormatter = v => "$" + FormatCompactAxisValue(v);
        }
        else if (compact)
        {
            ticks.LabelFormatter = FormatCompactAxisValue;
        }
        else if (integer)
        {
            ticks.LabelFormatter = v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            ticks.LabelFormatter = v => FormatCompactAxisValue(v);
        }
    }

    // ════════════════════════════ hover helper ════════════════════════════

    private void OnPlotPointerMoved(object? sender, PointerEventArgs e) => ShowHoverAt(e.GetPosition(this));

    private void OnPlotPointerExited(object? sender, RoutedEventArgs e) => HideHover();

    /// <summary>控件相对坐标（Avalonia Point）→ 数据坐标 → 悬停管线。PointerMoved 与测试共用。</summary>
    public void ShowHoverAt(Avalonia.Point position) =>
        ShowHoverAt(PlotControl.Plot.GetCoordinates(new Pixel(position.X, position.Y)));

    /// <summary>按数据坐标更新悬停视觉（crosshair + tooltip + 事件）；坐标换算后走同一管线。</summary>
    public void ShowHoverAt(Coordinates targetLocation)
    {
        if (_crosshair is null || _tooltip is null
            || PlotControl.Plot.LastRender.DataRect.Width <= 0
            || _hoverLabels.Length == 0)
        {
            HideHover();
            return;
        }

        // 拾取在像素 X 空间做最近点匹配（不依赖特定 plottable）——成本形态断线后，
        // null 成本日仍可悬停（审查 P2-7/P1-1 配套）；命中语义与 GetNearestX 一致（半径内最近 X，平手取更早）。
        var limits = PlotControl.Plot.Axes.GetLimits();
        var dataRect = PlotControl.Plot.LastRender.DataRect;
        var index = PickNearestIndex(MapToPixelX(targetLocation.X, limits, dataRect), limits, dataRect);
        if (index is null)
        {
            HideHover();
            return;
        }

        var coords = _hoverCoords[index.Value];
        _crosshair.IsVisible = true;
        _crosshair.Position = coords;

        // 右缘/上缘裁剪翻转（D-002 遗留收口）：把数据点映到像素，按象限选气泡伸出方向
        var alignment = ComputeTooltipAlignment(
            MapToPixelX(coords.X, limits, dataRect),
            MapToPixelY(coords.Y, limits, dataRect),
            dataRect.Left, dataRect.Top, dataRect.Width, dataRect.Height);

        _tooltip.IsVisible = true;
        _tooltip.TipLocation = coords;
        _tooltip.LabelLocation = coords;
        _tooltip.LabelAlignment = alignment;
        _tooltip.LabelText = _hoverLabels[index.Value];

        HoverChanged?.Invoke(this, new TrendChartHoverEventArgs
        {
            IsActive = true,
            Index = index.Value,
            LabelText = _hoverLabels[index.Value],
        });
        PlotControl.Refresh();
    }

    /// <summary>像素 X 空间最近点拾取（<see cref="MaxHoverDistance"/> 内，平手取更早索引；未命中返回 null）。</summary>
    private int? PickNearestIndex(double mousePixelX, AxisLimits limits, ScottPlot.PixelRect dataRect)
    {
        double best = MaxHoverDistance;
        int? bestIndex = null;
        for (var i = 0; i < _hoverCoords.Length; i++)
        {
            var distance = Math.Abs(MapToPixelX(_hoverCoords[i].X, limits, dataRect) - mousePixelX);
            if (distance <= best)
            {
                best = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private void HideHover()
    {
        if (_crosshair is not null)
        {
            _crosshair.IsVisible = false;
        }

        if (_tooltip is not null)
        {
            _tooltip.IsVisible = false;
        }

        HoverChanged?.Invoke(this, new TrendChartHoverEventArgs { IsActive = false, Index = -1, LabelText = null });
        PlotControl.Refresh();
    }

    /// <summary>
    /// tooltip 裁剪翻转纯函数（headless 单测覆盖）：锚点像素落在数据区右半 → 气泡向左伸（*Right），
    /// 上半 → 气泡向下伸（Upper*）。ScottPlot Label 对齐语义（D-002 实测）：锚点是气泡的对应角，
    /// UpperLeft = 气泡自锚点向右下延伸、LowerLeft = 向右上延伸，故上象限选 Upper*、右象限选 *Right。
    /// </summary>
    public static Alignment ComputeTooltipAlignment(
        double pointPixelX, double pointPixelY,
        double dataLeft, double dataTop, double dataWidth, double dataHeight)
    {
        var rightHalf = pointPixelX > dataLeft + dataWidth / 2;
        var topHalf = pointPixelY < dataTop + dataHeight / 2;
        return (topHalf, rightHalf) switch
        {
            (true, false) => Alignment.UpperLeft,
            (true, true) => Alignment.UpperRight,
            (false, false) => Alignment.LowerLeft,
            (false, true) => Alignment.LowerRight,
        };
    }

    private static double MapToPixelX(double x, AxisLimits limits, ScottPlot.PixelRect dataRect) =>
        dataRect.Left + ((x - limits.XRange.Min) / limits.XRange.Span) * dataRect.Width;

    private static double MapToPixelY(double y, AxisLimits limits, ScottPlot.PixelRect dataRect) =>
        dataRect.Bottom - ((y - limits.YRange.Min) / limits.YRange.Span) * dataRect.Height;

    // ════════════════════════════ 文案与格式（对账口径） ════════════════════════════

    /// <summary>
    /// 日形态 tooltip 文本（ScottPlot 内渲染，保持 ASCII 避免 CJK 字形缺失；中文说明走事件/状态行）。
    /// 成本行按 <see cref="AggregatedCost"/> 四态区分（审查 P1-1，契约"不要只读 KnownCostUsd"）：
    /// (x,*) 已知成本（+ 下限标记）；(null,true) 全未知 → "cost unknown"；(null,false) 无成本数据 → 不显示成本行。
    /// </summary>
    public static string DailyTooltipText(DailyUsagePoint p)
    {
        if (!p.HasData)
        {
            return $"{p.Day}\nno data (zero-filled)";
        }

        var lines = new List<string>
        {
            p.Day,
            $"in  {FormatTokens(p.InputTokens)}",
            $"out {FormatTokens(p.OutputTokens)}",
            $"tot {FormatTokens(p.TotalTokens)} tok",
        };

        if (p.Cost.KnownCostUsd is { } known)
        {
            lines.Add(FormatCostUsd(known, p.Cost.HasUnknownCost)!);
        }
        else if (p.Cost.HasUnknownCost)
        {
            lines.Add("cost unknown");
        }
        // (null, false)：范围内没有任何成本数据，不显示成本行（既非 0 也非未知）

        return string.Join('\n', lines);
    }

    /// <summary>
    /// 成本形态 tooltip 文本（审查 P1-1 四态锁定）：
    /// (x,false) "$x.xx"；((x,true) "$x.xx+"（已知成本下限）；(null,true) "cost unknown"（有用量但计价全缺失）；
    /// (null,false) "no cost data"（无成本数据）——(null) 的两种语义不再塌缩成同一句。
    /// </summary>
    public static string CostTooltipText(DailyUsagePoint p) => p.Cost.KnownCostUsd is { } known
        ? $"{p.Day}\n{FormatCostUsd(known, p.Cost.HasUnknownCost)}"
        : p.Cost.HasUnknownCost
            ? $"{p.Day}\ncost unknown"
            : $"{p.Day}\nno cost data";

    /// <summary>token 千分位（对账口径）；ScottPlot 轴刻度/tooltip 共用。</summary>
    public static string FormatTokens(long tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// 成本两位小数 USD（对账口径）。HasUnknownCost=true 时以 "+" 结尾表示"已知成本下限，实际更高"；
    /// 无任何已知成本返回 null（调用方决定是否展示成本行）。未知成本绝不静默折算为 0。
    /// </summary>
    public static string? FormatCostUsd(decimal? knownUsd, bool hasUnknownCost)
    {
        if (knownUsd is not { } value)
        {
            return null;
        }

        return hasUnknownCost
            ? $"${value.ToString("F2", CultureInfo.InvariantCulture)}+"
            : $"${value.ToString("F2", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// 紧凑数值，按 UI 语言取单位制（调用时点读 Translations，语言切换后的下一次重渲染即换单位）：
    /// 英文千进制 K/M/B/T/Q（1B 显示 1B 而非 1000M）；中文万进制 万/亿/万亿（口语直读——5882万
    /// 而非 5.88千万，1.23亿 而非 123百万；万进制是中文数字的原生分组）。尾数 ≥1000 取整、≥100
    /// 一位小数、其余两位（封顶 3~4 位有效数字）；尾数取整越档（如 9999.5万 → "10000万"）升入
    /// 上一档（"1亿"），杜绝 "10000万"/"1000M" 式越档尾数。仅 Avalonia 文本消费；ScottPlot 轴刻度
    /// 走 <see cref="FormatCompactAxisValue"/>（无 CJK 字形，恒为 ASCII 单位）。
    /// </summary>
    public static string FormatCompactTokens(double value) =>
        FormatCompact(value,
            Translations.Instance.CurrentLanguage == AppLanguage.SimplifiedChinese ? ChineseUnits : EnglishUnits,
            tickPrecision: false);

    /// <summary>轴刻度紧凑数值（ScottPlot 渲染无 CJK 字形，恒为 ASCII 短单位；刻度精度略降）。</summary>
    private static string FormatCompactAxisValue(double value) => FormatCompact(value, EnglishUnits, tickPrecision: true);

    /// <summary>单位档：生效阈值 / 除数 / 单位 / 尾数上限（取整尾数达到上限即升入上一档；阶梯末端不设限）。</summary>
    private readonly record struct CompactUnit(double Threshold, double Divisor, string Unit, double MantissaMax);

    // 千进制（英文习惯）：K/M/B/T 每档 10^3，Q 为阶梯末端。
    private static readonly CompactUnit[] EnglishUnits =
    [
        new(1e3, 1e3, "K", 1e3),
        new(1e6, 1e6, "M", 1e3),
        new(1e9, 1e9, "B", 1e3),
        new(1e12, 1e12, "T", 1e3),
        new(1e15, 1e15, "Q", double.MaxValue),
    ];

    // 万进制（中文习惯）：万/亿 每档 10^4（十亿写作 10亿、五千万写作 5000万，不设独立档），万亿为阶梯末端。
    private static readonly CompactUnit[] ChineseUnits =
    [
        new(1e4, 1e4, "万", 1e4),
        new(1e8, 1e8, "亿", 1e4),
        new(1e12, 1e12, "万亿", double.MaxValue),
    ];

    private static string FormatCompact(double value, CompactUnit[] units, bool tickPrecision)
    {
        var abs = Math.Abs(value);
        var index = Array.FindLastIndex(units, unit => abs >= unit.Threshold);
        if (index < 0)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // 尾数取整达到本档上限（9999.5万 → "10000万"，同英文 "1000M"）时升入上一档再缩放。
        if (index + 1 < units.Length && Math.Round(abs / units[index].Divisor) >= units[index].MantissaMax)
        {
            index++;
        }

        var scaled = value / units[index].Divisor;
        var format = tickPrecision ? "0.#"
            : Math.Abs(scaled) >= 1000 ? "0"
            : Math.Abs(scaled) >= 100 ? "0.#"
            : "0.##";
        return scaled.ToString(format, CultureInfo.InvariantCulture) + units[index].Unit;
    }
}
