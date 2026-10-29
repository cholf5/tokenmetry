using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tokenmetry.App.Localization;
using Tokenmetry.Core.Models;
using ScottPlot;
using ScottPlot.Plottables;

namespace Tokenmetry.App.Controls;

/// <summary>TrendChart 的形态（D-004：三形态参数化，同一绘图管线）。</summary>
public enum TrendChartMode
{
    /// <summary>日趋势：输入/输出堆叠柱 + 总量折线（I-028 日序列，含补零日辨识）。</summary>
    DailyTokens,

    /// <summary>成本折线：按日成本——已知成本 + 补零日 $0 成线（无 facts 真实为零），
    /// 未知成本日断线（语义见 <see cref="BuildCostLine"/>）。</summary>
    CostUsd,

    /// <summary>小时折线：0..23 全量桶总 token（I-029 小时分布）。</summary>
    HourlyTokens,
}

/// <summary>悬停状态变化事件（宿主页状态行可订阅；LabelText 与图上 tooltip 同源同格式）。</summary>
public sealed class TrendChartHoverEventArgs : EventArgs
{
    /// <summary>true = 正在悬停某数据点（图内死区驻留期间保持 true，规格 §3(6)）；false = 鼠标离开图表区域 / 未命中且未驻留。</summary>
    public bool IsActive { get; init; }

    /// <summary>命中的数据点下标（未命中为 -1）。</summary>
    public int Index { get; init; } = -1;

    /// <summary>tooltip 文本（未命中为 null）。</summary>
    public string? LabelText { get; init; }
}

/// <summary>
/// D-004 正式趋势图控件（ScottPlot 5）：日趋势（输入/输出堆叠柱 + 总量折线）、成本折线、小时折线
/// 三形态共用同一 AvaPlot 与 hover helper（crosshair 准星 + Avalonia 浮层浮窗：X 节点锚定经
/// ComputeTooltipOrigin、Y 随光标临界阻尼弹簧跟随（SmoothDamp）、DispatcherTimer 16ms 步进，
/// 样式见 SharedStyles chart-hovertip）。
/// 设计基准：docs/design/ccusage_analytics_prototype.html（.chart 的 barin/barout/line/point、
/// 图例三色 = :root blue/purple/cyan 变量 → AppAccentBlue/Purple/CyanBrush）。
/// 数据经样式属性传入（无 DataContext 依赖），变更即整图重建。
/// I-032 视觉升级：折线单调平滑（Web 同款观感）+ 零基线面积填充 + 只读交互（不提供缩放/平移）。
/// ScottPlot 与 Avalonia 同名类型（Pixel/Color 等）按 D-002 纪律全限定处理。
/// </summary>
public partial class TrendChart : UserControl
{
    /// <summary>悬停拾取半径（屏幕像素），沿用 D-002 spike 与官方 Avalonia demo 的取值。</summary>
    private const float MaxHoverDistance = 15;

    /// <summary>堆叠柱宽（坐标单位；日期轴 1 单位 = 1 天，0.72 ≈ 原型柱宽/间距比例）。</summary>
    private const double DailyBarSize = 0.72;

    /// <summary>Y 上下跟随平滑时间常数（秒，<see cref="SmoothDamp"/> smoothTime）：越大跟随越"懒"、加速/刹车越绵长。</summary>
    private const double TipFollowSmoothTimeSeconds = 0.2;

    /// <summary>插值时钟名义帧长（秒，与构造处 16ms 一致 ≈ 60fps）。</summary>
    private const double TipTickSeconds = 0.016;

    /// <summary>Y 落定速度阈值（px/s）：距离 <0.5px 且低于此速即吸附停表（亚像素，不可感知）。</summary>
    private const double TipSettleSpeedPxPerSec = 30;

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

    /// <summary>底层 ScottPlot Plot（测试用于断言重建后的样式）。</summary>
    public Plot Plot => PlotControl.Plot;

    /// <summary>输入交互开关（I-032 定案：恒 false——只读视图不提供缩放/平移；测试固化）。</summary>
    public bool InteractionsEnabled => PlotControl.UserInputProcessor.IsEnabled;

    /// <summary>悬停命中变化（IsActive=false 表示离开/未命中）。</summary>
    public event EventHandler<TrendChartHoverEventArgs>? HoverChanged;

    private Crosshair? _crosshair;

    // 浮窗插值状态（motion design）：X 目标 = 节点锚定位（ComputeTooltipOrigin，左右不随鼠标），
    // Y 目标 = 光标跟随位（ComputeTooltipFollowY）；X 每帧 lerp 滑行（换点 §3(3)），Y 临界阻尼弹簧跟随
    private Avalonia.Point _tipCurrent;   // 浮窗当前像素位置（插值状态）
    private Avalonia.Point _tipTarget;    // 浮窗目标像素位置
    private double _tipVelocityY;         // Y 跟随弹簧跨帧速度（px/s，SmoothDamp 状态）
    private bool _tipActive;              // 悬停激活中（HideHover 后复位——淡出让 IsVisible 保持
                                          // true，重进判定不能依赖它，否则从旧锚点滑过整图）
    private readonly DispatcherTimer _tipTimer;

    /// <summary>浮窗位移变换（Avalonia XAML 不支持对 Transform x:Name，从 Border.RenderTransform 取）。</summary>
    private Avalonia.Media.TranslateTransform HoverTipTranslate =>
        (Avalonia.Media.TranslateTransform)HoverTip.RenderTransform!;

    /// <summary>悬停候选点（下标与 _hoverLabels 一一对应；拾取在像素 X 空间做最近点匹配）。</summary>
    private Coordinates[] _hoverCoords = Array.Empty<Coordinates>();
    private string[] _hoverLabels = Array.Empty<string>();

    public TrendChart()
    {
        InitializeComponent();

        // I-032 只读视图定案：不提供缩放/平移（Web 端同款静态观感），关闭 AvaPlot 默认输入响应
        //（滚轮缩放 / 拖拽平移 / 双击复位）。悬停走本控件自有 Avalonia 事件，不受影响。
        PlotControl.UserInputProcessor.Disable();

        // AvaPlot 库行为：OnPointerWheelChanged 恒设 e.Handled=true（HandleMouseWheelEvent 默认
        // true，与其输入处理器开关无关）——不禁吞则页面根 ScrollViewer 在图上收不到滚轮，
        // 缩放已禁的场景下事件纯属被浪费，必须放行冒泡。
        PlotControl.HandleMouseWheelEvent = false;

        ModeProperty.Changed.AddClassHandler<TrendChart>((c, _) => c.RebuildPlot());
        DailyPointsProperty.Changed.AddClassHandler<TrendChart>((c, _) => c.RebuildPlot());
        HourlyDistributionProperty.Changed.AddClassHandler<TrendChart>((c, _) => c.RebuildPlot());

        // ScottPlot 不支持 Avalonia 动态资源：主题变体变化时整图重建（chrome 色重新现取令牌）。
        // 订阅控件自身事件（EffectiveThemeVariant 继承 App/Window 的设置，含 app.TrySetTheme 路径）。
        ActualThemeVariantChanged += OnActualThemeVariantChanged;

        // 浮窗插值时钟：仅在悬停激活/未到位时运行，静止即停（零开销）；16ms ≈ 60fps
        _tipTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => TickTipInterpolation());
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => RebuildPlot();

    /// <summary>
    /// 挂树即重建并（重）挂重译订阅；先退订再订阅防重复（同 DonutChart/Heatmap 纪律，支持重挂）。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Translations.Instance.PropertyChanged -= OnTranslationsPropertyChanged;
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
        RebuildPlot();
    }

    /// <summary>脱树退订重译订阅（Translations 是应用级单例，不随控件释放，防泄漏）。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Translations.Instance.PropertyChanged -= OnTranslationsPropertyChanged;
    }

    /// <summary>
    /// 语言切换重译（B-007 同 DonutChart/Heatmap 纪律）：以 PropertyChanged(CurrentLanguage) 作为
    /// 「每次切换一次」的信号重跑 RebuildPlot——悬停文案 <see cref="_hoverLabels"/> 在重建时按当时
    /// 语言固化（token 紧凑单位随 UI 语言取原生大数单位制），不重跑就陈旧到下一次数据变更。主题切换
    /// 走同一条重建管线，XAML 直绑 Translations 的空态文案由全量 PropertyChanged 自动刷新。
    /// </summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage))
        {
            RebuildPlot();
        }
    }

    // ════════════════════════════ 绘图 ════════════════════════════

    /// <summary>整图重建：清空 → 按形态布数据 → 取令牌画 chrome → 共享 hover plottable → 刷新。</summary>
    public void RebuildPlot()
    {
        var plot = PlotControl.Plot;
        plot.Clear();
        _crosshair = null;
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

        // 共享 hover 视觉：crosshair 准星画在图内（ScottPlot plottable），tooltip 走 Avalonia
        // 浮层（HoverTip，见 ShowHoverAt / TickTipInterpolation），此处随重建一并归位
        _crosshair = plot.Add.Crosshair(0, 0);
        _crosshair.IsVisible = false;
        _crosshair.LineWidth = 1;
        _crosshair.LinePattern = LinePattern.Dotted;
        _crosshair.LineColor = TokenColor("AppMutedTextBrush", new Color(130, 147, 173));
        _crosshair.MarkerShape = MarkerShape.OpenCircle;
        _crosshair.MarkerSize = 10;

        _tipTimer.Stop();
        HoverTip.IsVisible = false;
        HoverTip.Opacity = 0;
        _tipActive = false;
        _tipVelocityY = 0;

        // 空态皮肤：ScottPlot 无数据时坐标轴默认停在 -10..10，会露出一套无意义的坐标系。
        // 空态只留 EmptyState 文案，轴与网格一并隐藏；有数据重建时恢复（清屏不重置轴可见性）。
        var isEmpty = _hoverLabels.Length == 0;
        EmptyState.IsVisible = isEmpty;
        plot.Axes.Bottom.IsVisible = !isEmpty;
        plot.Axes.Left.IsVisible = !isEmpty;
        plot.Grid.YAxisStyle.IsVisible = !isEmpty;
        PlotControl.Refresh();
    }

    /// <summary>
    /// 诚实空态语义（审查 P1-2 定案）：双端有界窗口内 <b>所有</b>点 HasData=false（I-028 全补零窗口）
    /// 等同"该时间范围内无数据"，与真正空集一样显示空态——补零是"有数据窗口内的空日"辨识手法，
    /// 全空窗口没有可画的数据，画全零线是伪信息。部分有数据则正常画补零图。
    /// </summary>
    public static bool IsEffectivelyEmpty(IReadOnlyList<DailyUsagePoint>? points) =>
        points is null || points.Count == 0 || points.All(p => !p.HasData);

    /// <summary>
    /// 轴/网格/文字/背景 chrome（I-027 令牌，按当前 ActualThemeVariant 现取；缺资源时用原型 Dark 值兜底）。
    /// I-032 Web 观感：只有水平主网格（纵向网格关）、无边框、无刻度须；底部留极小边距——面积填充
    /// 与零值线段贴近下缘，同时让 y 轴「0」刻度与首个月份标签错开不重叠。
    /// </summary>
    private void ApplyChartChrome(Plot plot)
    {
        // 透明背景：让 Avalonia 侧的卡片渐变（AppCardBackgroundBrush）透出来
        plot.FigureBackground.Color = new Color(0, 0, 0, 0);
        plot.DataBackground.Color = new Color(0, 0, 0, 0);

        var grid = TokenColor("AppChartGridLineBrush", new Color(44, 52, 63));
        var text = TokenColor("AppMutedTextBrush", new Color(130, 147, 173));

        // 网格 ← 原型 .gridline #23344b（Q-019 降饱和现值见 AppChartGridLineBrush；只保留水平主网格，纵向网格随 I-032 关闭）
        plot.Grid.XAxisStyle.IsVisible = false;
        plot.Grid.YAxisStyle.MajorLineStyle.Color = grid;
        plot.Grid.YAxisStyle.MajorLineStyle.Width = 1f;
        plot.Grid.YAxisStyle.MinorLineStyle.Width = 0f;

        // 边框/刻度须全部隐藏（AppChartAxisLineBrush 不再用于边框；令牌保留供其余消费方使用），
        // 刻度文字 ← 原型 .chart text #8293ad
        foreach (var panel in new IAxis[] { plot.Axes.Bottom, plot.Axes.Left, plot.Axes.Top, plot.Axes.Right })
        {
            panel.FrameLineStyle.Width = 0f;
            panel.MajorTickStyle.Length = 0f;
            panel.MinorTickStyle.Length = 0f;
            panel.TickLabelStyle.ForeColor = text;
            panel.TickLabelStyle.FontSize = 10f; // 原型 .chart text{font-size:10px}
        }

        plot.Axes.Margins(left: 0.02, right: 0.02, bottom: 0.03, top: 0.1);
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

    // ════════════════════════════ 单调平滑曲线（I-032） ════════════════════════════

    /// <summary>
    /// 单调三次 Hermite 插值（Fritsch–Carlson 限幅，d3 curveMonotoneX 同源算法）密集化采样——
    /// Web 图表同款平滑曲线，且<b>绝不越过数据包络</b>。usage 序列的常态形状是长期零值 + 单日尖峰，
    /// Catmull-Rom 在此形状下会甩到零轴以下（伪负 token）并在峰顶过冲，故不用。
    /// x 必须严格递增（日序列 OADate / 小时 0..23 / 成本 run 均满足）；点数不足或 x 非严格递增时
    /// 原样返回（退化为直线连接，绝不抛异常）。每段密采样 <paramref name="segmentsPerSpan"/> 份
    /// 供直线渲染出平滑观感。
    /// </summary>
    public static (double[] Xs, double[] Ys) MonotoneDenseCurve(double[] xs, double[] ys, int segmentsPerSpan = 16)
    {
        if (xs.Length != ys.Length || xs.Length < 2)
        {
            return (xs, ys);
        }

        var n = xs.Length;
        var dx = new double[n - 1];
        var slope = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            dx[i] = xs[i + 1] - xs[i];
            if (dx[i] <= 0)
            {
                return (xs, ys); // 非严格递增（异常输入）：退化为直线，不除零
            }

            slope[i] = (ys[i + 1] - ys[i]) / dx[i];
        }

        // 端点切线 = 单侧差商；内点切线 = 加权调和平均（相邻斜率异号或为零 → 平坦，避免假极值）
        var tangent = new double[n];
        tangent[0] = slope[0];
        tangent[n - 1] = slope[n - 2];
        for (var i = 1; i < n - 1; i++)
        {
            if (slope[i - 1] * slope[i] <= 0)
            {
                tangent[i] = 0;
            }
            else
            {
                var w1 = 2 * dx[i] + dx[i - 1];
                var w2 = dx[i] + 2 * dx[i - 1];
                tangent[i] = (w1 + w2) / (w1 / slope[i - 1] + w2 / slope[i]);
            }
        }

        // Fritsch–Carlson 限幅：切线相对割线过陡时向内收缩，保证段内无过冲
        for (var i = 0; i < n - 1; i++)
        {
            if (Math.Abs(slope[i]) < 1e-12)
            {
                tangent[i] = 0;
                tangent[i + 1] = 0;
                continue;
            }

            var a = tangent[i] / slope[i];
            var b = tangent[i + 1] / slope[i];
            var s = a * a + b * b;
            if (s > 9)
            {
                var tau = 3 / Math.Sqrt(s);
                tangent[i] = tau * a * slope[i];
                tangent[i + 1] = tau * b * slope[i];
            }
        }

        var denseXs = new double[(n - 1) * segmentsPerSpan + 1];
        var denseYs = new double[denseXs.Length];
        var k = 0;
        for (var i = 0; i < n - 1; i++)
        {
            for (var j = 0; j < segmentsPerSpan; j++)
            {
                var h = (double)j / segmentsPerSpan;
                var h2 = h * h;
                var h3 = h2 * h;
                denseXs[k] = xs[i] + h * dx[i];
                denseYs[k] = (2 * h3 - 3 * h2 + 1) * ys[i]
                    + (h3 - 2 * h2 + h) * dx[i] * tangent[i]
                    + (-2 * h3 + 3 * h2) * ys[i + 1]
                    + (h3 - h2) * dx[i] * tangent[i + 1];
                k++;
            }
        }

        denseXs[k] = xs[n - 1];
        denseYs[k] = ys[n - 1];
        return (denseXs, denseYs);
    }

    /// <summary>
    /// 平滑折线（单调密集化）+ 可选零基线面积填充（Web 图表 areaStyle 观感；填充色 = 线色 20% 不透明）。
    /// 悬停拾取不受影响：hover 坐标始终锚在原始数据点（见各 Build* 对 _hoverCoords 的赋值）。
    /// </summary>
    private static Scatter AddSmoothLine(Plot plot, double[] xs, double[] ys, Color color, bool areaFill)
    {
        var (denseXs, denseYs) = MonotoneDenseCurve(xs, ys);
        var line = plot.Add.Scatter(denseXs, denseYs);
        line.LineWidth = 2.5f;
        line.Color = color;
        line.MarkerShape = MarkerShape.None;
        if (areaFill)
        {
            line.FillY = true;
            line.FillYValue = 0;
            line.FillYColor = color.WithAlpha(0.2);
        }

        return line;
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
        var grid = TokenColor("AppChartGridLineBrush", new Color(44, 52, 63));
        var panel = TokenColor("AppPanelBrush", new Color(23, 28, 37));

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

        // 总量折线（全部点连续——日历窗口语义：补零日的 0 让 x 轴回归真实日历，不做断线美化）；
        // 单调平滑（I-032），柱形背景之上不叠面积填充
        var totals = points.Select(p => (double)p.TotalTokens).ToArray();
        AddSmoothLine(plot, xs, totals, cyan, areaFill: false);

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
    /// 成本折线（审查 P2-7 视觉诚实性 + 补零日 $0 填充）：折线连接<b>已知成本</b>日与<b>补零日</b>
    /// （<see cref="BuildLineRuns"/> 分段）——补零日（<see cref="AggregatedCost.IsEmpty"/>，窗口内无任何
    /// facts）的真实成本就是 $0，随日历窗口补 0 成线（同 DailyTokens 形态的补零约定，淡化小点辨识）；
    /// 有用量但成本全未知的日（(null,true)）仍断线不画到零轴——未知成本绝不静默折算为 0。
    /// 已知日实心点、补零/未知日淡点（未知日位置锚在 0，仅供悬停定位）。四态语义见 <see cref="CostTooltipText"/>。
    /// </summary>
    private void BuildCostLine(Plot plot)
    {
        var points = DailyPoints ?? Array.Empty<DailyUsagePoint>();
        if (IsEffectivelyEmpty(points))
        {
            return; // 全补零窗口 = 无数据，诚实空态（见 IsEffectivelyEmpty）
        }

        var cyan = TokenColor("AppAccentCyanBrush", new Color(87, 199, 232));
        var grid = TokenColor("AppChartGridLineBrush", new Color(44, 52, 63));
        var panel = TokenColor("AppPanelBrush", new Color(23, 28, 37));

        var xs = points.Select(p => DateTime.Parse(p.Day, CultureInfo.InvariantCulture).ToOADate()).ToArray();
        // y 取已知成本；补零日为 0（真实为零，进连线），未知成本日为 0（仅作淡点/悬停锚点，不进连线）
        var ys = points.Select(p => (double)(p.Cost.KnownCostUsd ?? 0m)).ToArray();
        var knownFlags = points.Select(p => p.Cost.KnownCostUsd is { }).ToArray();
        // 成线日 = 已知成本日 ∪ 补零日（IsEmpty：既无已知也无未知 → 无任何成本 facts → 真实 $0）；
        // 未知成本日（有用量但无价，(null,true)）断线
        var connectFlags = points.Select(p => p.Cost.KnownCostUsd is { } || p.Cost.IsEmpty).ToArray();

        // 连线覆盖"连续成线日"的段（≥2 天才成线；孤立成线日靠实心点呈现）；平滑（I-032）。
        // 面积填充刻意不用：成本峰稀疏时补零段贴零轴，填充会把无用量日涂成实块、暗示并不存在的
        // 连续花费（视觉诚实性同审查 P2-7 的取向）。
        foreach (var (start, end) in BuildLineRuns(connectFlags))
        {
            AddSmoothLine(plot, xs[start..(end + 1)], ys[start..(end + 1)], cyan, areaFill: false);
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
        var panel = TokenColor("AppPanelBrush", new Color(23, 28, 37));

        var xs = hours.Select(h => (double)h.Hour).ToArray();
        var ys = hours.Select(h => (double)h.TotalTokens).ToArray();

        // 平滑曲线 + 面积填充（I-032）；标记点独立成散点，仍锚在真实小时值上
        AddSmoothLine(plot, xs, ys, cyan, areaFill: true);

        var hourCoords = xs.Select((x, i) => new Coordinates(x, ys[i])).ToArray();
        var hourMarkers = plot.Add.ScatterPoints(hourCoords, cyan);
        hourMarkers.LineWidth = 0;
        hourMarkers.MarkerShape = MarkerShape.FilledCircle;
        hourMarkers.MarkerSize = 4f;
        hourMarkers.MarkerLineColor = panel;
        hourMarkers.MarkerLineWidth = 1f;

        _hoverCoords = xs.Select((x, i) => new Coordinates(x, ys[i])).ToArray();
        var t = Translations.Instance;
        var unit = t.ChartTipTokenUnit;
        _hoverLabels = hours.Select(h =>
            $"{h.Hour:00}:00\n{FormatCompactTokens(h.TotalTokens)} {unit}\n" +
            string.Format(t.ChartTipSessionsFormat, h.SessionCount.ToString("N0", CultureInfo.InvariantCulture))).ToArray();

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

    /// <summary>
    /// 按数据坐标更新悬停视觉（crosshair 钉节点 + 浮窗 + 事件）；坐标换算后走同一管线。
    /// 浮窗 X 锚定命中节点不随鼠标，Y 随光标上下跟随（规格 §3(2)）；targetLocation 兼作
    /// 光标位（PointerMoved 路径像素↔数据线性往返无损，测试直接构造数据坐标）。
    /// </summary>
    public void ShowHoverAt(Coordinates targetLocation)
    {
        if (_crosshair is null
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
        var mousePixelY = MapToPixelY(targetLocation.Y, limits, dataRect);
        var index = PickNearestIndex(MapToPixelX(targetLocation.X, limits, dataRect), limits, dataRect);
        if (index is null)
        {
            // 驻留不变式（规格 §3(6) 反闪烁）：浮窗一旦显示，鼠标仍在图内（PointerMoved 未命中新点）
            // 就保持现状——crosshair、浮窗文案、事件都不动；离开图表区域（PointerExited→HideHover）才淡出。
            // X 驻留旧节点，Y 仍随光标上下（§3(2) 上下跟随）；未驻留时未命中维持原清除语义（幂等 no-op）。
            if (!_tipActive)
            {
                HideHover();
                return;
            }

            FollowTipY(mousePixelY, dataRect);
            return;
        }

        var coords = _hoverCoords[index.Value];
        _crosshair.IsVisible = true;
        _crosshair.Position = coords;

        // 浮窗（motion design）：X = 数据点像素锚点的错位气泡位（右偏 14px、右半翻转、数据区
        // 夹回，左右不随鼠标）；Y = 光标跟随（§3(2) 上下跟随，ComputeTooltipFollowY 垂直居中夹回）。
        // 换点不闪现——目标变了但插值时钟从当前位置继续滑过去；首次出现直接落在目标位只做淡入。
        var pixel = new Avalonia.Point(
            MapToPixelX(coords.X, limits, dataRect),
            MapToPixelY(coords.Y, limits, dataRect));
        var label = _hoverLabels[index.Value];
        if (HoverTipText.Text != label)
        {
            HoverTipText.Text = label;
        }

        HoverTip.Measure(Size.Infinity);
        var tipSize = HoverTip.DesiredSize;
        var origin = ComputeTooltipOrigin(pixel.X, pixel.Y, dataRect.Left, dataRect.Top,
            dataRect.Width, dataRect.Height, tipSize.Width, tipSize.Height);
        _tipTarget = new Avalonia.Point(
            origin.X,
            ComputeTooltipFollowY(mousePixelY, dataRect.Top, dataRect.Height, tipSize.Height));
        if (!_tipActive)
        {
            // 首次出现（或隐藏后重进）：直接落在目标位只做淡入（规格 §3(4) 隐藏后复位插值状态）
            SnapTipToTarget();
        }

        _tipActive = true;
        HoverTip.IsVisible = true;
        HoverTip.Opacity = 1;
        _tipTimer.Start();

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

    /// <summary>
    /// 结束悬停并淡出浮窗（规格 §3(4)(6)：唯一隐藏入口——鼠标离开图表区域 PointerExited 触发，
    /// 测试共用同一入口）。与 <see cref="ShowHoverAt(Avalonia.Point)"/> 成对：PointerMoved 未命中
    /// 不走这里（驻留不变式），只有真正离开图表控件才收尾。
    /// </summary>
    public void HideHover()
    {
        if (_crosshair is not null)
        {
            _crosshair.IsVisible = false;
        }

        _tipTimer.Stop();
        // 淡出：IsVisible 保持 true、Opacity→0（chart-hovertip transition 120ms）；opacity 0 的
        // 浮层 IsHitTestVisible=False 不影响交互，代价可忽略，换取平滑淡出
        HoverTip.Opacity = 0;
        _tipActive = false;
        _tipVelocityY = 0; // 弹簧状态一并复位（重进走 §3(4) 吸附，不带残余速度）

        HoverChanged?.Invoke(this, new TrendChartHoverEventArgs { IsActive = false, Index = -1, LabelText = null });
        PlotControl.Refresh();
    }

    /// <summary>浮窗直接落到位并清弹簧速度（首次出现/重进，规格 §3(4) 复位插值状态——不滑入）。</summary>
    private void SnapTipToTarget()
    {
        _tipCurrent = _tipTarget;
        _tipVelocityY = 0;
        HoverTipTranslate.X = _tipCurrent.X;
        HoverTipTranslate.Y = _tipCurrent.Y;
    }

    /// <summary>驻留中未命中（规格 §3(6)）的上下跟随：X 保持旧节点锚定不动，仅重定 Y 目标随光标。</summary>
    private void FollowTipY(double mousePixelY, ScottPlot.PixelRect dataRect)
    {
        HoverTip.Measure(Size.Infinity);
        var tipSize = HoverTip.DesiredSize;
        _tipTarget = new Avalonia.Point(
            _tipTarget.X,
            ComputeTooltipFollowY(mousePixelY, dataRect.Top, dataRect.Height, tipSize.Height));
        _tipTimer.Start();
    }

    /// <summary>
    /// 浮窗每帧步进（DispatcherTimer 16ms 驱动；headless 测试直接步进）：X 换点滑行维持逐帧
    /// lerp(0.25)（§3(3)，左右逻辑不变），Y 上下跟随走临界阻尼弹簧（§3(2)，加速+刹车、绝不过冲）；
    /// X/Y 距离与 Y 速度都低于阈值即吸附到位并停表（亚像素不可感知，静止零开销）。
    /// </summary>
    public void TickTipInterpolation()
    {
        var dx = _tipTarget.X - _tipCurrent.X;
        var dy = _tipTarget.Y - _tipCurrent.Y;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5 && Math.Abs(_tipVelocityY) < TipSettleSpeedPxPerSec)
        {
            SnapTipToTarget();
            _tipTimer.Stop();
        }
        else
        {
            _tipCurrent = new Avalonia.Point(
                _tipCurrent.X + dx * 0.25,
                SmoothDamp(_tipCurrent.Y, _tipTarget.Y, ref _tipVelocityY, TipFollowSmoothTimeSeconds, TipTickSeconds));
            HoverTipTranslate.X = _tipCurrent.X;
            HoverTipTranslate.Y = _tipCurrent.Y;
        }
    }

    /// <summary>浮窗与数据点的水平间距（px）：气泡错开圆点，不盖点。</summary>
    public const double TooltipGapPx = 14;

    /// <summary>
    /// 浮窗左上角像素位置（纯函数，headless 单测覆盖）：默认在数据点右侧
    /// <see cref="TooltipGapPx"/> 处、垂直居中；点落在数据区右半时气泡翻到左侧；
    /// 越界时夹回数据区（右缘翻转后仍越界 → 贴右缘内侧）。取代旧 ComputeTooltipAlignment
    /// 四象限枚举——Avalonia 浮层直接用像素坐标，不再依赖 ScottPlot 对齐语义。
    /// </summary>
    public static Avalonia.Point ComputeTooltipOrigin(
        double pointX, double pointY,
        double dataLeft, double dataTop, double dataWidth, double dataHeight,
        double tooltipWidth, double tooltipHeight)
    {
        var rightHalf = pointX > dataLeft + dataWidth / 2;
        var x = rightHalf ? pointX - TooltipGapPx - tooltipWidth : pointX + TooltipGapPx;
        var y = pointY - tooltipHeight / 2;
        x = Math.Clamp(x, dataLeft, Math.Max(dataLeft, dataLeft + dataWidth - tooltipWidth));
        y = Math.Clamp(y, dataTop, Math.Max(dataTop, dataTop + dataHeight - tooltipHeight));
        return new Avalonia.Point(x, y);
    }

    /// <summary>
    /// 上下跟随的浮窗目标 Y（规格 §3(2)，纯函数，headless 单测覆盖）：气泡垂直居中于鼠标
    /// 光标像素位，越界夹回数据区（与 <see cref="ComputeTooltipOrigin"/> 的 Y 夹回同语义）。
    /// 左右不在此——X 仍锚定数据节点（§3(1)/(6)，不随鼠标）。
    /// </summary>
    public static double ComputeTooltipFollowY(double mousePixelY, double dataTop, double dataHeight, double tooltipHeight) =>
        Math.Clamp(mousePixelY - tooltipHeight / 2, dataTop, Math.Max(dataTop, dataTop + dataHeight - tooltipHeight));

    /// <summary>
    /// 单轴临界阻尼平滑跟随（Unity SmoothDamp 同源算法，纯函数，headless 单测覆盖）：从静止
    /// 起步加速、临近目标刹车、绝不过冲，约 3×smoothTime 落定——替换逐帧 lerp(0.25) 做 Y 跟随：
    /// lerp 起步速度突跳、只有刹车没有加速，观感生硬（规格 §3(2) 要求"加速和刹车"）。
    /// <paramref name="velocity"/> 为跨帧弹簧状态（px/s，调用方持有）；deltaTime 为名义帧长。
    /// </summary>
    public static double SmoothDamp(double current, double target, ref double velocity, double smoothTime, double deltaTime)
    {
        if (deltaTime <= 0)
        {
            return current;
        }

        smoothTime = Math.Max(0.0001, smoothTime);
        var omega = 2 / smoothTime;
        var x = omega * deltaTime;
        var exp = 1 / (1 + x + 0.48 * x * x + 0.235 * x * x * x);
        var change = current - target;
        var temp = (velocity + omega * change) * deltaTime;
        velocity = (velocity - omega * temp) * exp;
        return target + (change + temp) * exp;
    }

    private static double MapToPixelX(double x, AxisLimits limits, ScottPlot.PixelRect dataRect) =>
        dataRect.Left + ((x - limits.XRange.Min) / limits.XRange.Span) * dataRect.Width;

    private static double MapToPixelY(double y, AxisLimits limits, ScottPlot.PixelRect dataRect) =>
        dataRect.Bottom - ((y - limits.YRange.Min) / limits.YRange.Span) * dataRect.Height;

    // ════════════════════════════ 文案与格式（对账口径） ════════════════════════════

    /// <summary>
    /// 日形态 tooltip 文本（Avalonia 浮层 HoverTip 渲染，非 ScottPlot 内绘）。token 数走
    /// <see cref="FormatCompactTokens"/> 随 UI 语言取原生大数单位制（Q-008：浮窗曾是
    /// ScottPlot 自绘时代的恒 ASCII 约束随浮层迁移失效）；输入/输出/总量/单位与补零/成本态文案
    /// 全部走 Translations（B-007 重译订阅保证切换语言后重建）。token 数为紧凑短单位（B-006
    /// 问题 3：千分位全量 5-9 位数字在悬浮气泡里不可读），成本行按 <see cref="AggregatedCost"/>
    /// 四态区分（审查 P1-1，契约"不要只读 KnownCostUsd"）：
    /// (x,*) 已知成本（+ 下限标记）；(null,true) 全未知 → 成本未知；(null,false) 无成本数据 → 不显示成本行。
    /// </summary>
    public static string DailyTooltipText(DailyUsagePoint p)
    {
        var t = Translations.Instance;
        if (!p.HasData)
        {
            return $"{p.Day}\n{t.ChartTipNoDataZeroFilled}";
        }

        var lines = new List<string>
        {
            p.Day,
            $"{t.ChartTipIn}  {FormatCompactTokens(p.InputTokens)}",
            $"{t.ChartTipOut} {FormatCompactTokens(p.OutputTokens)}",
            $"{t.ChartTipTotal} {FormatCompactTokens(p.TotalTokens)} {t.ChartTipTokenUnit}",
        };

        if (p.Cost.KnownCostUsd is { } known)
        {
            lines.Add(FormatCostUsd(known, p.Cost.HasUnknownCost)!);
        }
        else if (p.Cost.HasUnknownCost)
        {
            lines.Add(t.ChartTipCostUnknown);
        }
        // (null, false)：范围内没有任何成本数据，不显示成本行（既非 0 也非未知）

        return string.Join('\n', lines);
    }

    /// <summary>
    /// 成本形态 tooltip 文本（审查 P1-1 四态锁定）：
    /// (x,false) "$x.xx"；((x,true) "$x.xx+"（已知成本下限）；(null,true) 成本未知（有用量但计价全缺失）；
    /// (null,false) 无成本数据——(null) 的两种语义不再塌缩成同一句。
    /// </summary>
    public static string CostTooltipText(DailyUsagePoint p) => p.Cost.KnownCostUsd is { } known
        ? $"{p.Day}\n{FormatCostUsd(known, p.Cost.HasUnknownCost)}"
        : p.Cost.HasUnknownCost
            ? $"{p.Day}\n{Translations.Instance.ChartTipCostUnknown}"
            : $"{p.Day}\n{Translations.Instance.ChartTipNoCostData}";

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
    /// 紧凑数值，按 UI 语言取各语种原生大数单位制（调用时点读 Translations，语言切换后的下一次重渲染
    /// 即换单位制）：中文万进制 万/亿/万亿（口语直读——5882万 而非 5.88千万；万进制是中文数字的原生
    /// 分组）；日语同万进制 万/億/兆，韩语 만/억/조（千/천 不设档，1万/만 起缩写）；英语千进制
    /// K/M/B/T/Q；其余语种千进制配本语种惯用缩写——pt-BR mil/mi/bi/tri、ru тыс./млн/млрд/трлн、
    /// es mil/M/MM/B、de Tsd./Mio./Mrd./Bio.、fr k/M/Md/bn（西德法长级：B/Bio./bn 均指 10^12）。
    /// 尾数数字按该语种 CultureInfo 渲染（逗号小数语种出 "1,5 Mio."；西文五语种数字与单位间为不换行
    /// 空格防折行）。尾数 ≥1000 取整、≥100 一位小数、其余两位（封顶 3~4 位有效数字）；尾数取整越档
    /// （如 9999.5万 → "10000万"）升入上一档（"1億"），杜绝 "10000万"/"1000M" 式越档尾数。Avalonia
    /// 文本消费（KPI/列表/环心/环形图与热力图 tooltip、TrendChart 悬停浮层）；ScottPlot 轴刻度走
    /// <see cref="FormatCompactAxisValue"/>（无 CJK 字形，恒为 ASCII 单位）。
    /// </summary>
    public static string FormatCompactTokens(double value)
    {
        var style = StyleFor(Translations.Instance.CurrentLanguage);
        return FormatCompact(value, style.Units, style.Culture, tickPrecision: false);
    }

    /// <summary>轴刻度紧凑数值（ScottPlot 渲染无 CJK 字形，恒为 ASCII 短单位；刻度精度略降）。</summary>
    private static string FormatCompactAxisValue(double value) =>
        FormatCompact(value, EnglishUnits, CultureInfo.InvariantCulture, tickPrecision: true);

    /// <summary>单位档：生效阈值 / 除数 / 单位 / 尾数上限（取整尾数达到上限即升入上一档；阶梯末端不设限）。</summary>
    private readonly record struct CompactUnit(double Threshold, double Divisor, string Unit, double MantissaMax);

    /// <summary>语言样式：单位阶梯 + 尾数数字的渲染文化（各语种习惯的小数点符号）。</summary>
    private readonly record struct CompactStyle(CompactUnit[] Units, CultureInfo Culture);

    // 千进制（英文习惯）：K/M/B/T 每档 10^3，Q 为阶梯末端。恒 ASCII（Invariant 文化、无空格），
    // 兼作 ScottPlot 轴刻度与托盘的口径。
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

    // 万进制（繁体，zh-Hant）：档位与简体一致，仅字形繁体化（萬/億）——图表数值混入简体字形即漏译。
    private static readonly CompactUnit[] TraditionalChineseUnits =
    [
        new(1e4, 1e4, "萬", 1e4),
        new(1e8, 1e8, "億", 1e4),
        new(1e12, 1e12, "萬億", double.MaxValue),
    ];

    // 万进制（日语）：万/億/兆 每档 10^4（日式四位分组，千不设档——1万 起缩写），兆为阶梯末端。
    private static readonly CompactUnit[] JapaneseUnits =
    [
        new(1e4, 1e4, "万", 1e4),
        new(1e8, 1e8, "億", 1e4),
        new(1e12, 1e12, "兆", double.MaxValue),
    ];

    // 万进制（韩语）：만/억/조（천(千) 不设档——만 起缩写），조为阶梯末端。单位用谚文，非汉字 萬/億。
    private static readonly CompactUnit[] KoreanUnits =
    [
        new(1e4, 1e4, "만", 1e4),
        new(1e8, 1e8, "억", 1e4),
        new(1e12, 1e12, "조", double.MaxValue),
    ];

    // 千进制（西文语种）：档位每档 10^3，缩写随各语种惯用形；数字与单位间用不换行空格（防 KPI/
    // tooltip 内折行，法/德/俄文排版亦要求单位前不断行）。西语长级 billón=10^12（故 B 非 10^9）、
    // 德语长级 Billion=10^12（Bio.）、法语长级 billion=10^12（bn）；10^9 为 mil millones/milliarde/
    // milliard（MM/Mrd./Md）。
    private const string Nbsp = "\u00A0";

    private static readonly CompactUnit[] BrazilianPortugueseUnits =
    [
        new(1e3, 1e3, Nbsp + "mil", 1e3),
        new(1e6, 1e6, Nbsp + "mi", 1e3),
        new(1e9, 1e9, Nbsp + "bi", 1e3),
        new(1e12, 1e12, Nbsp + "tri", double.MaxValue),
    ];

    private static readonly CompactUnit[] RussianUnits =
    [
        new(1e3, 1e3, Nbsp + "тыс.", 1e3),
        new(1e6, 1e6, Nbsp + "млн", 1e3),
        new(1e9, 1e9, Nbsp + "млрд", 1e3),
        new(1e12, 1e12, Nbsp + "трлн", double.MaxValue),
    ];

    private static readonly CompactUnit[] SpanishUnits =
    [
        new(1e3, 1e3, Nbsp + "mil", 1e3),
        new(1e6, 1e6, Nbsp + "M", 1e3),
        new(1e9, 1e9, Nbsp + "MM", 1e3),
        new(1e12, 1e12, Nbsp + "B", double.MaxValue),
    ];

    private static readonly CompactUnit[] GermanUnits =
    [
        new(1e3, 1e3, Nbsp + "Tsd.", 1e3),
        new(1e6, 1e6, Nbsp + "Mio.", 1e3),
        new(1e9, 1e9, Nbsp + "Mrd.", 1e3),
        new(1e12, 1e12, Nbsp + "Bio.", double.MaxValue),
    ];

    private static readonly CompactUnit[] FrenchUnits =
    [
        new(1e3, 1e3, Nbsp + "k", 1e3),
        new(1e6, 1e6, Nbsp + "M", 1e3),
        new(1e9, 1e9, Nbsp + "Md", 1e3),
        new(1e12, 1e12, Nbsp + "bn", double.MaxValue),
    ];

    // 尾数数字文化（各语种小数点习惯；GetCultureInfo 自带运行时缓存，此处静态持有免逐次查表）。
    private static readonly CultureInfo ZhHansCulture = CultureInfo.GetCultureInfo("zh-Hans");
    private static readonly CultureInfo ZhHantCulture = CultureInfo.GetCultureInfo("zh-Hant");
    private static readonly CultureInfo JaCulture = CultureInfo.GetCultureInfo("ja");
    private static readonly CultureInfo KoCulture = CultureInfo.GetCultureInfo("ko");
    private static readonly CultureInfo PtBrCulture = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru");
    private static readonly CultureInfo EsCulture = CultureInfo.GetCultureInfo("es");
    private static readonly CultureInfo DeCulture = CultureInfo.GetCultureInfo("de");
    private static readonly CultureInfo FrCulture = CultureInfo.GetCultureInfo("fr");

    /// <summary>语言 → 原生大数单位制（D-024）：中日韩走万进制（单位字形随语种），其余语种走千进制
    /// 并配本语种缩写与数字文化。</summary>
    private static CompactStyle StyleFor(AppLanguage language) => language switch
    {
        AppLanguage.SimplifiedChinese => new(ChineseUnits, ZhHansCulture),
        AppLanguage.TraditionalChinese => new(TraditionalChineseUnits, ZhHantCulture),
        AppLanguage.Japanese => new(JapaneseUnits, JaCulture),
        AppLanguage.Korean => new(KoreanUnits, KoCulture),
        AppLanguage.BrazilianPortuguese => new(BrazilianPortugueseUnits, PtBrCulture),
        AppLanguage.Russian => new(RussianUnits, RuCulture),
        AppLanguage.Spanish => new(SpanishUnits, EsCulture),
        AppLanguage.German => new(GermanUnits, DeCulture),
        AppLanguage.French => new(FrenchUnits, FrCulture),
        _ => new(EnglishUnits, CultureInfo.InvariantCulture),
    };

    private static string FormatCompact(double value, CompactUnit[] units, CultureInfo culture, bool tickPrecision)
    {
        var abs = Math.Abs(value);
        var index = Array.FindLastIndex(units, unit => abs >= unit.Threshold);
        if (index < 0)
        {
            // 未达首个单位档（<1000 / <10000）时保留千分位：紧凑格式化不得把 6750 读成 6750；
            // 这是唯一一处「全量数字」出口，KPI/表格/热力图在同一函数下保持一致可读性。
            // 分组符与小数符跟随该语种 culture（de/fr 为 6.750 / 6 750 的本地写法）。
            return value.ToString("#,0.##", culture);
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
        return scaled.ToString(format, culture) + units[index].Unit;
    }
}
