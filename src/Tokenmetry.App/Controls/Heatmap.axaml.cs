using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
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

    /// <summary>渐入：单格时长（ms，Motion.FastMs 档）。</summary>
    public const double CellFadeMs = 120;

    /// <summary>渐入：起步 stagger 间隔（ms）。</summary>
    public const double CellStaggerStartMs = 15;

    /// <summary>渐入：时间线总时长封顶（ms）——格子多时压缩间隔，不无限拉长。</summary>
    public const double StaggerCapMs = 600;

    /// <summary>
    /// stagger 间隔（纯函数）：总时长 = 单格 + (格数-1)×间隔 ≤ 封顶；少格走起步间隔，下限 0。
    /// </summary>
    public static double StaggerIntervalMs(int cellCount) => cellCount <= 1
        ? 0
        : Math.Max(0, Math.Min(CellStaggerStartMs, (StaggerCapMs - CellFadeMs) / (cellCount - 1)));

    /// <summary>第 index 格的渐入延迟（纯函数）= index×间隔。</summary>
    public static double StaggerDelayMs(int cellIndex, int cellCount) =>
        cellIndex * StaggerIntervalMs(cellCount);

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
        HookTranslationsRetranslate();
        Rebuild();
    }

    /// <summary>脱树退订集合与重译订阅（重挂由 OnAttachedToVisualTree 恢复）；作废进行中的渐入时间线。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Cells is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnCellsCollectionChanged;
        }
        Translations.Instance.PropertyChanged -= OnTranslationsPropertyChanged;
        _staggerGeneration++;
    }

    private void HookCellsCollection()
    {
        if (Cells is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnCellsCollectionChanged; // 防重复订阅
            collection.CollectionChanged += OnCellsCollectionChanged;
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
    /// Rebuild——tooltip（<see cref="BuildCellTooltip"/>：token 紧凑单位 + "无数据"样板）是 build-time
    /// 固化字符串，不重跑就陈旧到下一次数据变更。XAML 直绑 Translations 的空态 EmptyText 由全量
    /// PropertyChanged 自动刷新，不依赖本订阅。
    /// </summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Translations.CurrentLanguage))
        {
            return;
        }

        Rebuild();
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

    // ── stagger 渐入（motion design）──

    /// <summary>时间线代数：Rebuild/脱树即作废旧时间线（帧回调闭包据此自杀）。</summary>
    private int _staggerGeneration;

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

        var i = 0;
        var entrances = new List<(Border Cell, double Delay)>(cells.Count);
        foreach (var cell in cells)
        {
            var background = BuildCellBrush(cell, max, baseColor);
            var border = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Background = background,
                Height = CellHeight,
                Margin = new Thickness(CellGap / 2),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            border.Classes.Add("hm-cell");
            // 图表悬浮提示（B-006）：chart-tip 类装饰（App 令牌配色）；文案见 BuildCellTooltip
            ChartTooltips.Set(border, BuildCellTooltip(cell));
            CellsGrid.Children.Add(border);
            RunCellEntrance(border, StaggerDelayMs(i, cells.Count));
            entrances.Add((border, StaggerDelayMs(i, cells.Count)));
            i++;
        }

        StartStagger(entrances);
    }

    /// <summary>
    /// 单格渐入初态（motion design）：Opacity 0 + Scale 0.85（进度由 <see cref="StartStagger"/> 按帧推进）。
    /// 只动 Opacity/RenderTransform，不碰布局属性。
    /// </summary>
    private static void RunCellEntrance(Border cell, double delayMs)
    {
        cell.Opacity = 0;
        cell.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        cell.RenderTransform = TransformOperations.Parse("scale(0.85)");
    }

    /// <summary>
    /// 驱动整板 stagger 渐入：请求合成帧回调，每帧按时间线重算每格 Opacity + TransformOperations
    /// ("scale()")，全部到位即不再请求（静止零开销）。Avalonia 12 的 Animation 管线动不了
    /// RenderTransform（无公开 animator，RunAsync 只收 Visual），DispatcherTimer 在 headless 测试里
    /// 又不随 RunJobs/tick 推进——合成帧回调（TopLevel.RequestAnimationFrame）两侧皆可。
    /// </summary>
    private void StartStagger(IReadOnlyList<(Border Cell, double Delay)> targets)
    {
        _staggerGeneration++;
        if (targets.Count == 0)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            // 无宿主（脱树后属性变更触发 Rebuild 的边界）：直接落到终态，不残留中间态
            foreach (var (cell, _) in targets)
            {
                cell.Opacity = 1;
                cell.RenderTransform = TransformOperations.Parse("scale(1)");
            }

            return;
        }

        var generation = _staggerGeneration;
        var start = (TimeSpan?)null;
        Action<TimeSpan> frame = null!;
        frame = timestamp =>
        {
            if (generation != _staggerGeneration)
            {
                return; // 重建/脱树后旧时间线作废
            }

            start ??= timestamp;
            var elapsed = (timestamp - start.Value).TotalMilliseconds;
            var done = true;
            foreach (var (cell, delay) in targets)
            {
                var t = Math.Clamp((elapsed - delay) / CellFadeMs, 0, 1);
                if (t < 1)
                {
                    done = false;
                }

                var eased = Tokenmetry.App.Styles.Motion.EaseOut.Ease(t);
                cell.Opacity = eased;
                cell.RenderTransform = TransformOperations.Parse($"scale({0.85 + 0.15 * eased:0.####})");
            }

            if (!done)
            {
                topLevel.RequestAnimationFrame(frame);
            }
        };
        topLevel.RequestAnimationFrame(frame);
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
