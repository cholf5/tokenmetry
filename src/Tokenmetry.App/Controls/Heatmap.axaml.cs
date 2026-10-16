using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tokenmetry.App.Localization;

namespace Tokenmetry.App.Controls;

/// <summary>热力图单格数据（VM 友好：init-only 小对象）。</summary>
public sealed class HeatmapCell
{
    /// <summary>格子标签（悬停 tooltip 首段，日历场景为 yyyy-MM-dd）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>当日数值（强度分子）。</summary>
    public double Value { get; init; }

    /// <summary>false = 范围内补零/无数据的空日（呈空态底色而非最低强度色）。</summary>
    public bool HasData { get; init; }
}

/// <summary>
/// D-005 自绘活跃日历热力图：按日强度着色格子 + 悬停显示当日数值（ToolTip）。
/// 强度色阶对齐原型（rgba(76,141,255, 0.12..0.9)）＝ <c>AppSeries0Color</c> 的透明度渐变
/// （系列色本就是"跟数据语义走不翻转"的令牌，无需新增）；格子底/边框走明暗令牌自动翻转。
/// 列数/行数：Columns 可配（默认 7，原型 7×5=35 格），行数 = ceil(格子数 / Columns)。
/// </summary>
public partial class Heatmap : UserControl
{
    /// <summary>强度 → 透明度映射（原型 background:rgba(76,141,255, .12 + v*.78)）。</summary>
    public const double MinAlpha = 0.12;
    public const double MaxAlpha = 0.90;

    /// <summary>格子间距（原型 gap:6px，均分到格子的四周 Margin 3px）。</summary>
    public const double CellGap = 6;

    public static readonly StyledProperty<System.Collections.IEnumerable?> CellsProperty =
        AvaloniaProperty.Register<Heatmap, System.Collections.IEnumerable?>(nameof(Cells));

    /// <summary>列数（原型 7 列日历）；行数由格子数自动推导。</summary>
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<Heatmap, int>(nameof(Columns), 7);

    /// <summary>格子高（原型 27px）。</summary>
    public static readonly StyledProperty<double> CellHeightProperty =
        AvaloniaProperty.Register<Heatmap, double>(nameof(CellHeight), 27d);

    public Heatmap()
    {
        InitializeComponent();

        CellsProperty.Changed.AddClassHandler<Heatmap>((c, e) =>
        {
            if (e.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= c.OnCellsCollectionChanged;
            }

            // 无条件重挂（内部先退订防重复）：脱树状态下订阅无害（Rebuild 幂等），挂树由 override 恢复
            c.HookCellsCollection();

            c.Rebuild();
        });
        ColumnsProperty.Changed.AddClassHandler<Heatmap>((c, _) => c.Rebuild());
        CellHeightProperty.Changed.AddClassHandler<Heatmap>((c, _) => c.Rebuild());
    }

    /// <summary>挂树即重建并（重）挂集合订阅；先退订再订阅防重复（审查 P2-2：与 Detached 对称，支持重挂）。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookCellsCollection();
        Rebuild();
    }

    /// <summary>脱树退订集合订阅（重挂由 OnAttachedToVisualTree 恢复）。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Cells is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnCellsCollectionChanged;
        }
    }

    private void HookCellsCollection()
    {
        if (Cells is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnCellsCollectionChanged; // 防重复订阅
            collection.CollectionChanged += OnCellsCollectionChanged;
        }
    }

    public System.Collections.IEnumerable? Cells
    {
        get => GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double CellHeight
    {
        get => GetValue(CellHeightProperty);
        set => SetValue(CellHeightProperty, value);
    }

    private void OnCellsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    /// <summary>
    /// 强度色纯函数（headless 单测覆盖）：ratio ∈ [0,1] 线性映射到 [MinAlpha, MaxAlpha] 透明度，
    /// 色相保持基准色（同色系 alpha 渐变 → 深浅与强度单调一致）。ratio 越大颜色越实。
    /// </summary>
    public static Color IntensityColor(Color baseColor, double ratio)
    {
        var clamped = Math.Clamp(ratio, 0, 1);
        var alpha = (byte)Math.Round(255 * (MinAlpha + (MaxAlpha - MinAlpha) * clamped));
        return Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
    }

    /// <summary>重建格子（数据/属性变化与首次挂树时调用）。</summary>
    public void Rebuild()
    {
        var cells = (Cells ?? Array.Empty<object>())
            .OfType<HeatmapCell>()
            .ToList();

        CellsGrid.Children.Clear();
        CellsGrid.Columns = Math.Max(1, Columns);
        EmptyText.IsVisible = cells.Count == 0;
        if (cells.Count == 0)
        {
            return;
        }

        var max = cells.Where(c => c.HasData).Select(c => c.Value).DefaultIfEmpty(0).Max();
        var baseColor = ResolveSeriesColor();
        var cardLine = ResolveBrush("AppCardLineBrush", Color.Parse("#22324a"));

        foreach (var cell in cells)
        {
            var background = BuildCellBrush(cell, max, baseColor);
            var border = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = cardLine,
                Background = background,
                Height = CellHeight,
                Margin = new Thickness(CellGap / 2),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            // 图表悬浮提示（B-006）：chart-tip 类装饰（App 令牌配色）；文案见 BuildCellTooltip
            ChartTooltips.Set(border, BuildCellTooltip(cell));
            CellsGrid.Children.Add(border);
        }
    }

    /// <summary>格子底色：有数据日按强度取系列色 alpha 渐变；无数据日取次面板底色（空态可辨）。</summary>
    private IBrush BuildCellBrush(HeatmapCell cell, double max, Color seriesColor)
    {
        if (!cell.HasData)
        {
            return ResolveBrush("AppPanelAltBrush", Color.Parse("#142137"));
        }

        var ratio = max > 0 ? cell.Value / max : 0;
        return new SolidColorBrush(IntensityColor(seriesColor, ratio));
    }

    /// <summary>悬停 tooltip：当日数值（token 紧凑单位，随 UI 语言的万/亿或 K/M 口径）或"无数据"。</summary>
    public static string BuildCellTooltip(HeatmapCell cell) => cell.HasData
        ? $"{cell.Label} · {TrendChart.FormatCompactTokens(cell.Value)} tok"
        : string.Format(Translations.Instance.HeatmapNoDataTooltip, cell.Label);

    /// <summary>强度色基准 = AppSeries0Color（原型 rgba(76,141,255,α) 即 #4c8dff；普通资源不随明暗翻转）。</summary>
    private Color ResolveSeriesColor()
    {
        if (Application.Current?.TryGetResource("AppSeries0Color", ActualThemeVariant, out var value) == true
            && value is Color color)
        {
            return color;
        }

        return Color.Parse("#4c8dff");
    }

    private IBrush ResolveBrush(string key, Color fallback)
    {
        if (Application.Current?.TryGetResource(key, ActualThemeVariant, out var value) == true
            && value is ISolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}
