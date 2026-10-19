using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Tokenmetry.App.Localization;

namespace Tokenmetry.App.Controls;

/// <summary>环形图单段数据（VM 友好：init-only 小对象，可直接放进 ObservableCollection）。</summary>
public sealed class DonutSegment
{
    public DonutSegment(string name, double value, Color? color = null)
    {
        Name = name;
        Value = value;
        Color = color;
    }

    /// <summary>段名（图例行显示）。</summary>
    public string Name { get; }

    /// <summary>段值（占比 = 值 / 全段值之和）。</summary>
    public double Value { get; }

    /// <summary>可选显式颜色（如 Token 类型构成的固定三色）；缺省按段序取 AppSeries{i%7}Color。</summary>
    public Color? Color { get; }
}

/// <summary>图例行渲染模型（swatch 画刷 + 名称 + 百分比文案）。</summary>
public sealed class DonutLegendItem
{
    public DonutLegendItem(string name, string percentText, IBrush brush)
    {
        Name = name;
        PercentText = percentText;
        Brush = brush;
    }

    public string Name { get; }

    public string PercentText { get; }

    public IBrush Brush { get; }
}

/// <summary>
/// D-005 自绘环形图控件：多段弧（StreamGeometry 圆弧环段，Avalonia 无内置 Arc）+ 中心标题/副标题
/// （Grid 叠层）+ 图例列表（名称 + 百分比，悬停显示绝对值）。数据源为 <see cref="Segments"/>
/// （IEnumerable&lt;DonutSegment&gt;，支持 ObservableCollection 增量刷新）——相比 AttachedProperty
/// 选了"标准样式属性 + VM 集合"方案：与 ItemsControl 生态绑定习惯一致、XAML 直接可写、
/// INotifyCollectionChanged 自动重建（选型记录见 D-005 Development Log 2026-10-13）。
/// 段色缺省按段序映射 AppSeries0..6（超 7 段循环，与原型 colors[i % colors.length] 一致）；
/// 中心文字/空态颜色走 DynamicResource，明暗切换自动生效。
/// </summary>
public partial class DonutChart : UserControl
{
    public static readonly StyledProperty<System.Collections.IEnumerable?> SegmentsProperty =
        AvaloniaProperty.Register<DonutChart, System.Collections.IEnumerable?>(nameof(Segments));

    /// <summary>环中心主文字（如紧凑总量 "8.74M"）。</summary>
    public static readonly StyledProperty<string> CenterTitleProperty =
        AvaloniaProperty.Register<DonutChart, string>(nameof(CenterTitle), string.Empty);

    /// <summary>环中心副文字（如 "Total Tokens"）。</summary>
    public static readonly StyledProperty<string> CenterSubtitleProperty =
        AvaloniaProperty.Register<DonutChart, string>(nameof(CenterSubtitle), "Total Tokens");

    /// <summary>环直径（原型 .donut 142px）。</summary>
    public static readonly StyledProperty<double> DonutSizeProperty =
        AvaloniaProperty.Register<DonutChart, double>(nameof(DonutSize), 142d);

    /// <summary>环厚（原型 stroke-width 13）。</summary>
    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<DonutChart, double>(nameof(RingThickness), 13d);

    public DonutChart()
    {
        InitializeComponent();

        SegmentsProperty.Changed.AddClassHandler<DonutChart>((c, e) =>
        {
            if (e.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= c.OnSegmentsCollectionChanged;
            }

            // 无条件重挂（内部先退订防重复）：脱树状态下订阅无害（Rebuild 幂等），挂树由 override 恢复
            c.HookSegmentsCollection();

            c.Rebuild();
        });
        CenterTitleProperty.Changed.AddClassHandler<DonutChart>((c, _) => c.Rebuild());
        CenterSubtitleProperty.Changed.AddClassHandler<DonutChart>((c, _) => c.Rebuild());
        DonutSizeProperty.Changed.AddClassHandler<DonutChart>((c, _) => c.Rebuild());
        RingThicknessProperty.Changed.AddClassHandler<DonutChart>((c, _) => c.Rebuild());

        // 主题变体变化影响空态环/中心文字颜色（DynamicResource 自动），弧段色为系列色（不翻转），
        // 但 Light/Dark 下 EmptyRing 画刷实例不同——这里无需手工重建，仅需尺寸类重建走属性回调。
    }

    /// <summary>挂树即重建并（重）挂集合订阅；先退订再订阅防重复（审查 P2-2：与 Detached 对称，支持重挂）。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookSegmentsCollection();
        HookTranslationsRetranslate();
        Rebuild();
    }

    /// <summary>脱树退订集合与重译订阅（重挂由 OnAttachedToVisualTree 恢复）。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Segments is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnSegmentsCollectionChanged;
        }
        Translations.Instance.PropertyChanged -= OnTranslationsPropertyChanged;
        _sweepTimer?.Stop();
    }

    private void HookSegmentsCollection()
    {
        if (Segments is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnSegmentsCollectionChanged; // 防重复订阅
            collection.CollectionChanged += OnSegmentsCollectionChanged;
        }
    }

    /// <summary>重译订阅（B-007）：挂树/脱树对称；先退订再订阅防重复（挂树可能多次发生）。</summary>
    private void HookTranslationsRetranslate()
    {
        Translations.Instance.PropertyChanged -= OnTranslationsPropertyChanged;
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    /// <summary>
    /// 语言切换重译（B-007）：以 PropertyChanged(CurrentLanguage) 作为「每次切换一次」的信号重跑
    /// Rebuild——build-time 固化文案不重跑就陈旧到下一次数据变更，覆盖两处：分段 tooltip 的
    /// token 紧凑单位口径（万/亿 vs K/M）与空态中心副题（Translations.Instance.NoData）。
    /// XAML 直绑 Translations 的空态 EmptyText 由全量 PropertyChanged 自动刷新，不依赖本订阅。
    /// </summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Translations.CurrentLanguage))
        {
            return;
        }

        Rebuild();
    }

    public System.Collections.IEnumerable? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public string CenterTitle
    {
        get => GetValue(CenterTitleProperty);
        set => SetValue(CenterTitleProperty, value);
    }

    public string CenterSubtitle
    {
        get => GetValue(CenterSubtitleProperty);
        set => SetValue(CenterSubtitleProperty, value);
    }

    public double DonutSize
    {
        get => GetValue(DonutSizeProperty);
        set => SetValue(DonutSizeProperty, value);
    }

    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    private void OnSegmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    // ── 扫入动画（motion design）──
    private DispatcherTimer? _sweepTimer;

    /// <summary>扫入签名（段值序列）：值未变（如语言切换重译）不重播动画。</summary>
    private string? _lastSweepSignature;

    // ════════════════════════════ 几何与渲染 ════════════════════════════

    /// <summary>
    /// 段值 → 起始角/扫角（度，0° = 顶部、顺时针，原型 svg rotate(-90deg) 的等价实现）。
    /// 全零 / 空集返回空列表（控件呈空态）；末段扫角吃掉累计舍入，保证角度和恒为 360°。
    /// </summary>
    public static IReadOnlyList<(double StartDegrees, double SweepDegrees)> BuildSweeps(IReadOnlyList<double> values)
    {
        var total = values.Sum();
        if (values.Count == 0 || total <= 0)
        {
            return Array.Empty<(double, double)>();
        }

        var result = new List<(double Start, double Sweep)>(values.Count);
        double offset = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var value = Math.Max(0, values[i]);
            var sweep = i == values.Count - 1 ? 360 - offset : value / total * 360;
            result.Add((offset, sweep));
            offset += sweep;
        }

        return result;
    }

    /// <summary>扫入动画：单段时长（ms，Motion.SlowMs 档）。</summary>
    public const double SweepDurationMs = 240;

    /// <summary>扫入动画：段间 stagger（ms）。</summary>
    public const double SweepStaggerMs = 40;

    /// <summary>
    /// 第 index 段在时间线 elapsedMs 的当前扫角（纯函数）：段延迟 = index×stagger，
    /// 单段窗口内 CubicEaseOut 从 0 长到 finalSweep（与全局缓动同源）。
    /// </summary>
    public static double SweepAt(double elapsedMs, int index, double finalSweep)
    {
        var t = Math.Clamp((elapsedMs - index * SweepStaggerMs) / SweepDurationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        return finalSweep * eased;
    }

    /// <summary>时间线总时长（ms）：最后一段完成时刻。</summary>
    public static double SweepTotalMs(int segmentCount) =>
        segmentCount <= 0 ? 0 : (segmentCount - 1) * SweepStaggerMs + SweepDurationMs;

    /// <summary>重建弧段 + 图例 + 中心/空态可见性（数据/属性变化与首次挂树时调用）。</summary>
    public void Rebuild()
    {
        var segments = (Segments ?? Array.Empty<object>())
            .OfType<DonutSegment>()
            .Where(s => s.Value >= 0)
            .ToList();

        var size = DonutSize;
        var thickness = Math.Min(RingThickness, size / 4);
        DonutHost.Width = size;
        DonutHost.Height = size;
        ArcCanvas.Width = size;
        ArcCanvas.Height = size;

        ArcCanvas.Children.Clear();

        var sweeps = BuildSweeps(segments.Select(s => s.Value).ToList());
        var isEmpty = sweeps.Count == 0;

        EmptyRing.IsVisible = isEmpty;
        EmptyRing.StrokeThickness = thickness;
        var ringMid = size / 2 - 2 - thickness / 2;
        EmptyRing.Width = ringMid * 2;
        EmptyRing.Height = ringMid * 2;
        EmptyText.IsVisible = isEmpty;

        if (isEmpty)
        {
            LegendList.ItemsSource = null;
            CenterTitleText.Text = CenterTitle;
            CenterSubtitleText.Text = Translations.Instance.NoData;
            return;
        }

        var centerX = size / 2;
        var centerY = size / 2;
        var outerRadius = size / 2 - 2;
        var innerRadius = Math.Max(1, outerRadius - thickness);
        // total>0 由 BuildSweeps 非空保证；此处不设 Math.Max(1,·) 下限——
        // 单段小值（total<1）必须按真实占比显示 100%，而非被下限拉成 50%（审查 P2-1）
        var total = segments.Sum(s => s.Value);
        var legendItems = new List<DonutLegendItem>();

        // 扫入签名（motion design）：段值序列未变（语言切换重译等）不重播动画
        var signature = string.Join("|", segments.Select(s => s.Value.ToString("R", CultureInfo.InvariantCulture)));
        var animate = signature != _lastSweepSignature;
        _lastSweepSignature = signature;

        var sweepPaths = new List<(Avalonia.Controls.Shapes.Path Path, double Start, double Final)>();
        for (var i = 0; i < segments.Count; i++)
        {
            var (start, sweep) = sweeps[i];
            var brush = new SolidColorBrush(ResolveColor(segments[i], i));
            var path = new Avalonia.Controls.Shapes.Path
            {
                Fill = brush,
                // 段间无缝拼接（原型 stroke-dasharray 相邻段无缝）
                Stroke = null,
            };
            sweepPaths.Add((path, start, sweep));
            path.Data = BuildRingSegment(centerX, centerY, outerRadius, innerRadius, start,
                animate ? SweepAt(0, i, sweep) : sweep);
            // 图表悬浮提示（B-006）：chart-tip 类装饰（App 令牌配色）+ token 紧凑单位（随 UI 语言万/亿或 K/M）
            ChartTooltips.Set(path, $"{segments[i].Name}: {TrendChart.FormatCompactTokens(segments[i].Value)} tok");
            ArcCanvas.Children.Add(path);

            var percent = total > 0 ? segments[i].Value / total * 100 : 0;
            legendItems.Add(new DonutLegendItem(
                segments[i].Name,
                percent.ToString("F1", CultureInfo.InvariantCulture) + "%",
                brush));
        }

        if (animate)
        {
            StartSweep(sweepPaths, centerX, centerY, outerRadius, innerRadius);
            // 中心数字与弧段同步淡入（克制：只此一处用整组 opacity，不做逐段）。
            // RunFadeIn 落定本地终值 1——裸 FadeIn.RunAsync 完成后回落到本地 0，数字会消失。
            // 注意不能写 Styles.Motion：Control.Styles 实例属性会遮蔽命名空间段
            Tokenmetry.App.Styles.Motion.RunFadeIn(CenterTitleText, Tokenmetry.App.Styles.Motion.NormalMs);
            Tokenmetry.App.Styles.Motion.RunFadeIn(CenterSubtitleText, Tokenmetry.App.Styles.Motion.NormalMs);
        }

        LegendList.ItemsSource = legendItems;
        CenterTitleText.Text = CenterTitle;
        CenterSubtitleText.Text = CenterSubtitle;
    }

    /// <summary>驱动扫入：DispatcherTimer 每 tick 重生成弧段几何，全部到位即停。</summary>
    private void StartSweep(
        List<(Avalonia.Controls.Shapes.Path Path, double Start, double Final)> targets,
        double centerX, double centerY, double outerRadius, double innerRadius)
    {
        _sweepTimer?.Stop();
        if (targets.Count == 0)
        {
            return;
        }

        var elapsed = 0.0;
        var total = SweepTotalMs(targets.Count);
        // 先声明再赋值：lambda 内引用 timer，不能在 timer 自身初始化式里引用（CS0841）
        DispatcherTimer timer = null!;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            elapsed += 16;
            var done = true;
            for (var i = 0; i < targets.Count; i++)
            {
                var (path, start, final) = targets[i];
                var sweep = SweepAt(elapsed, i, final);
                if (sweep < final)
                {
                    done = false;
                }

                path.Data = BuildRingSegment(centerX, centerY, outerRadius, innerRadius, start, sweep);
            }

            if (done)
            {
                timer.Stop();
                if (_sweepTimer == timer)
                {
                    _sweepTimer = null;
                }
            }
        });
        _sweepTimer = timer;
        timer.Start();
    }

    /// <summary>段色：显式 Color 优先，否则按段序取 AppSeries{i%7}Color（不随明暗翻转的普通资源）。</summary>
    private Color ResolveColor(DonutSegment segment, int index)
    {
        if (segment.Color is { } explicitColor)
        {
            return explicitColor;
        }

        var key = $"AppSeries{index % 7}Color";
        if (Application.Current?.TryGetResource(key, ActualThemeVariant, out var value) == true
            && value is Color color)
        {
            return color;
        }

        return Colors.Gray;
    }

    /// <summary>
    /// 圆弧环段路径：外弧（顺时针）→ 内边连线 → 内弧（逆时针）→ 闭合。
    /// 单段 100%（扫角 ≈ 360°）时 ArcSegment 起终点重合会退化为零长，拆成两个 180° 半弧。
    /// </summary>
    public static StreamGeometry BuildRingSegment(
        double centerX, double centerY, double outerRadius, double innerRadius,
        double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();
        var sweep = Math.Clamp(sweepDegrees, 0.0001, 360);
        var fullCircle = sweep >= 359.999;
        var splitAngles = fullCircle
            ? new[] { startDegrees, startDegrees + 180, startDegrees + 360 }
            : new[] { startDegrees, startDegrees + sweep };

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOn(centerX, centerY, outerRadius, splitAngles[0]), isFilled: true);
            for (var i = 1; i < splitAngles.Length; i++)
            {
                ctx.ArcTo(
                    PointOn(centerX, centerY, outerRadius, splitAngles[i]),
                    new Size(outerRadius, outerRadius),
                    rotationAngle: 0,
                    isLargeArc: !fullCircle && sweep > 180,
                    SweepDirection.Clockwise);
            }

            ctx.LineTo(PointOn(centerX, centerY, innerRadius, splitAngles[^1]));
            for (var i = splitAngles.Length - 2; i >= 0; i--)
            {
                ctx.ArcTo(
                    PointOn(centerX, centerY, innerRadius, splitAngles[i]),
                    new Size(innerRadius, innerRadius),
                    rotationAngle: 0,
                    isLargeArc: !fullCircle && sweep > 180,
                    SweepDirection.CounterClockwise);
            }

            ctx.EndFigure(isClosed: true);
        }

        return geometry;
    }

    /// <summary>环上点：0° = 顶部（12 点方向）、顺时针为正（与原型 svg rotate(-90deg) 后的弧序一致）。</summary>
    public static Point PointOn(double centerX, double centerY, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(
            centerX + radius * Math.Sin(radians),
            centerY - radius * Math.Cos(radians));
    }
}
