using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
        Rebuild();
    }

    /// <summary>脱树退订集合订阅（重挂由 OnAttachedToVisualTree 恢复）。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Segments is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnSegmentsCollectionChanged;
        }
    }

    private void HookSegmentsCollection()
    {
        if (Segments is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnSegmentsCollectionChanged; // 防重复订阅
            collection.CollectionChanged += OnSegmentsCollectionChanged;
        }
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

        for (var i = 0; i < segments.Count; i++)
        {
            var (start, sweep) = sweeps[i];
            var brush = new SolidColorBrush(ResolveColor(segments[i], i));
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = BuildRingSegment(centerX, centerY, outerRadius, innerRadius, start, sweep),
                Fill = brush,
                // 段间无缝拼接（原型 stroke-dasharray 相邻段无缝）
                Stroke = null,
            };
            ToolTip.SetTip(path, $"{segments[i].Name}: {segments[i].Value.ToString("N0", CultureInfo.InvariantCulture)} tok");
            ArcCanvas.Children.Add(path);

            var percent = total > 0 ? segments[i].Value / total * 100 : 0;
            legendItems.Add(new DonutLegendItem(
                segments[i].Name,
                percent.ToString("F1", CultureInfo.InvariantCulture) + "%",
                brush));
        }

        LegendList.ItemsSource = legendItems;
        CenterTitleText.Text = CenterTitle;
        CenterSubtitleText.Text = CenterSubtitle;
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
