using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Views;

/// <summary>
/// 分享海报视图：纯展示，数据来自 <see cref="SharePosterViewModel"/> 的不可变快照。
/// 主题由请求决定而不是跟随应用——海报可能在应用处于夜间时按日间导出，所以整棵子树挂在一个
/// <see cref="ThemeVariantScope"/> 下，否则取到的会是应用当前主题的令牌（品牌标识尤其明显：
/// Light 档是黑标，Dark 档是白标，反色就会看不见）。
/// <para>
/// 版式（竖版 / 横版）决定画布宽度与六个面板的栅格落位：面板本体在 XAML 里只声明一次
/// （声明的是竖版落位），横版由这里改栅格定义 + 重排 Grid.Row/Column，不复制第二套标记。
/// </para>
/// </summary>
public partial class SharePosterView : UserControl
{
    /// <summary>竖版画布宽度（导出按 2× 光栅化）。</summary>
    public const double PortraitWidth = 1080;

    /// <summary>横版画布宽度（设计 §3.3：1800 宽，导出 3600）。</summary>
    public const double LandscapeWidth = 1800;

    /// <summary>热力格子边长 / 间隙（格距 4 = Margin 2×2）/ 列槽宽（格 + 间隙，月份标签行按它对列）。</summary>
    public const int HeatCellSize = 27;
    public const int HeatCellGap = 4;
    public const int HeatColumnWidth = 31;

    public SharePosterView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            ApplyRequestedTheme();
            ApplyLayout();
        };
        // 跟随应用：对话框开着时切应用主题，海报与投影跟着变（静态海报本身不需要重绘）
        ActualThemeVariantChanged += (_, _) =>
        {
            if (IsFollowingAppTheme)
            {
                ApplyPanelShadows(ActualThemeVariant);
            }
        };
    }

    /// <summary>导出时要光栅化的节点（当前基底的根，不含外面用来适配预览的缩放容器）。</summary>
    public Control PosterRootControl =>
        (DataContext as SharePosterViewModel)?.Data.Request.Style switch
        {
            SharePosterStyle.Tray => TrayRoot,
            SharePosterStyle.Heatmap => HeatRoot,
            _ => PosterRoot,
        };

    /// <summary>信息卡样式走托盘基底（固定 1080 竖版），总览样式走总览基底（1080 / 1800）——只看样式，与范围无关。</summary>
    public bool IsTrayStyle => (DataContext as SharePosterViewModel)?.IsTrayStyle ?? false;

    /// <summary>打卡热力图样式走热力基底（固定 1800 横版，能力表锁定）。</summary>
    public bool IsHeatmapStyle => (DataContext as SharePosterViewModel)?.IsHeatmapStyle ?? false;

    /// <summary>版式 → 画布宽度（纯函数，测试与导出共用）。</summary>
    public static double WidthFor(SharePosterLayout layout) =>
        layout == SharePosterLayout.Landscape ? LandscapeWidth : PortraitWidth;

    private SharePosterLayout CurrentLayout =>
        (DataContext as SharePosterViewModel)?.Data.Request.Layout ?? SharePosterLayout.Portrait;

    /// <summary>托盘构成条的三段宽度：按输入/输出/缓存 token 比例给星号（全 0 时等分）。</summary>
    private void ApplyTrayTypeBar()
    {
        var types = (DataContext as SharePosterViewModel)?.TrayTypes;
        var values = types is { Count: >= 3 }
            ? types.Take(3).Select(t => t.Value).ToArray()
            : [0d, 0d, 0d];
        var total = values.Sum();
        var widths = total > 0
            ? values.Select(v => (v / total).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "*")
            : ["*", "*", "*"];
        TrayTypeBar.ColumnDefinitions = new ColumnDefinitions(string.Join(",", widths));
    }

    private bool IsFollowingAppTheme =>
        ((DataContext as SharePosterViewModel)?.Data.Request.Theme ?? SharePosterTheme.FollowApp)
        == SharePosterTheme.FollowApp;

    private void ApplyRequestedTheme()
    {
        var theme = (DataContext as SharePosterViewModel)?.Data.Request.Theme ?? SharePosterTheme.FollowApp;
        if (theme == SharePosterTheme.FollowApp)
        {
            // 跟随应用 = 不钉主题，交给继承：应用切明暗时海报子树自己跟着换令牌
            ThemeScope.RequestedThemeVariant = null;
            ApplyPanelShadows(ActualThemeVariant);
            return;
        }

        var dark = theme == SharePosterTheme.Dark;
        ThemeScope.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        ApplyPanelShadows(dark ? ThemeVariant.Dark : ThemeVariant.Light);
    }

    /// <summary>
    /// 日间海报的卡片投影（设计 §3.5 第 6 条）：浅底上卡片与页面底几乎同色，不分层会糊成一片。
    /// 夜间不给投影（深底上根本看不见，给了只是白白多一层像素）。投影颜色与浓度全在
    /// <c>AppPosterShadowColor</c> 令牌里，此处负责拼出 <see cref="DropShadowEffect"/>——XAML 无法
    /// 只对效果的颜色部分接 DynamicResource。
    /// <para>
    /// 用 Effect 而不是 <c>Border.BoxShadow</c> 是硬约束：BoxShadow 在 <c>RenderTargetBitmap.Render</c>
    /// 的立即渲染路径下会把「泄漏的缩放变换」带给其后绘制的兄弟内容（首个带影卡片之后整张海报
    /// 被放大错位，见导出回归测试），Avalonia 12.1.4 实测如此；Effect 两条渲染路径（合成器预览、
    /// RTB 导出）都正确。投影形态取双影中较大的一层（偏移并入模糊），浓度取令牌 alpha。
    /// </para>
    /// </summary>
    private void ApplyPanelShadows(ThemeVariant variant)
    {
        // 资源查找覆盖两种时机：DataContextChanged 发生在挂树前（控件自身的资源链还没接到 App），
        // 这时回落到 Application 的资源宿主显式按变体取色。
        var found = TryGetResource("AppPosterShadowColor", variant, out var resource)
                    || (Application.Current?.TryGetResource("AppPosterShadowColor", variant, out resource) ?? false);
        DropShadowEffect? effect = null;
        if (found && resource is Color color && color.A > 0)
        {
            effect = new DropShadowEffect
            {
                Color = Color.FromRgb(color.R, color.G, color.B),
                Opacity = color.A / 255d,
                BlurRadius = 20,
            };
        }

        foreach (var panel in CardSurfaces())
        {
            panel.Effect = effect;
        }
    }

    private IEnumerable<Border> CardSurfaces() =>
        KpiGrid.Children.OfType<Border>().Concat(
            new Border[] { TrendPanel, AgentPanel, ModelPanel, TypePanel, ProjectPanel, CostPanel, TrayCard, HeatCard });

    /// <summary>
    /// 按版式重排栅格。两套版面的差别不只是换列宽：面板归属也换行（类型环形、成本趋势从第二排列
    /// 提到第一列/第二列），所以每行是一个独立 Grid，横版时把面板移到目标行里。
    /// <list type="bullet">
    /// <item>竖版（1080）：三行——趋势 1.45 / Agent 0.95；模型 / 类型 1:1；项目 / 成本 1:1。</item>
    /// <item>横版（1800）：两行——趋势 2.1 / Agent 1 / 类型 1；模型 1 / 项目 1 / 成本 1.2（设计 §3.3）。</item>
    /// </list>
    /// 竖版第一行偏离设计稿的 1.55/0.85 是有意的：190px 环形后面只剩 ~65px，图例被裁成「Z…」；
    /// 1.45/0.95 下趋势区 581px 与基准稿的 577 基本一致。
    /// 尺度差异（字号 / 图表高度 / 环形直径）由 <c>poster-root.land</c> 的样式组承担，这里只管布局。
    /// </summary>
    private void ApplyLayout()
    {
        if (IsTrayStyle)
        {
            // 托盘基底是固定 1080 竖卡片：不受版式选项影响（对话框在信息卡样式下也禁了横版）
            TrayRoot.Width = PortraitWidth;
            PosterRoot.Width = PortraitWidth;
            PosterRoot.Classes.Remove("land");
            ApplyTrayTypeBar();
            return;
        }

        if (IsHeatmapStyle)
        {
            // 热力基底是固定 1800 横版：能力表只登记了横版，版式选项对它同样无效
            HeatRoot.Width = LandscapeWidth;
            ApplyHeatGrid();
            return;
        }

        var landscape = CurrentLayout == SharePosterLayout.Landscape;
        PosterRoot.Width = WidthFor(CurrentLayout);

        // 先换容器可见性、再把面板搬进目标容器：两种版式各带一套固定列定义的容器，
        // 运行期不动任何 ColumnDefinitions（Avalonia Grid 在定义数量变化时会踩测量缓存索引越界）。
        PortraitRow1.IsVisible = !landscape;
        PortraitRow2.IsVisible = !landscape;
        PortraitRow3.IsVisible = !landscape;
        LandscapeRow1.IsVisible = landscape;
        LandscapeRow2.IsVisible = landscape;

        if (landscape)
        {
            if (!PosterRoot.Classes.Contains("land"))
            {
                PosterRoot.Classes.Add("land");
            }

            Place(TrendPanel, LandscapeRow1, 0);
            Place(AgentPanel, LandscapeRow1, 1);
            Place(TypePanel, LandscapeRow1, 2);
            Place(ModelPanel, LandscapeRow2, 0);
            Place(ProjectPanel, LandscapeRow2, 1);
            Place(CostPanel, LandscapeRow2, 2);
        }
        else
        {
            PosterRoot.Classes.Remove("land");
            Place(TrendPanel, PortraitRow1, 0);
            Place(AgentPanel, PortraitRow1, 1);
            Place(ModelPanel, PortraitRow2, 0);
            Place(TypePanel, PortraitRow2, 1);
            Place(ProjectPanel, PortraitRow3, 0);
            Place(CostPanel, PortraitRow3, 1);
        }

        static void Place(Control panel, Grid row, int column)
        {
            // 面板在两次版式间换行：先从旧行摘下再挂到目标行（图表随之重建，见控件 OnAttachedToVisualTree）
            if (panel.Parent is Panel previous && !ReferenceEquals(previous, row))
            {
                previous.Children.Remove(panel);
            }

            if (!row.Children.Contains(panel))
            {
                row.Children.Add(panel);
            }

            Grid.SetRow(panel, 0);
            Grid.SetColumn(panel, column);
        }
    }

    /// <summary>
    /// 热力图的月份标签行、星期标签列与按天格子：格子是数据形状（最多 52×7 = 364 个），
    /// 在 XAML 里逐个声明没有意义，按 VM 算好的 <see cref="SharePosterViewModel.Heat"/> 生成——
    /// 颜色一律走 Classes（heat-0..4）接 ThemeDictionaries 令牌，代码里不出现色值。
    /// 列槽宽固定为 CellSlot，月份标签行与格子共用同一列划分，标签才能对上列。
    /// </summary>
    private void ApplyHeatGrid()
    {
        if (DataContext is not SharePosterViewModel vm)
        {
            return;
        }

        var heat = vm.Heat;

        // 月份标签：每列一个定宽槽，标签跨到下一个标签列（左对齐，海报没有右起排元素）
        var slots = string.Join(",", Enumerable.Repeat(HeatColumnWidth, heat.Weeks));
        HeatMonthRow.ColumnDefinitions = new ColumnDefinitions(slots);
        HeatMonthRow.Children.Clear();
        foreach (var label in heat.MonthLabels)
        {
            var text = new TextBlock
            {
                Classes = { "heat-month-label" },
                Text = label.Text,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            };
            Grid.SetColumn(text, label.StartCol);
            if (label.Span > 1)
            {
                Grid.SetColumnSpan(text, label.Span);
            }

            HeatMonthRow.Children.Add(text);
        }

        // 星期标签列：周一起 7 行，只标一/三/五（同 GitHub 的密度），空行也占位对齐
        HeatWeekdayCol.Children.Clear();
        for (var weekday = 0; weekday < 7; weekday++)
        {
            HeatWeekdayCol.Children.Add(new Border
            {
                Height = HeatCellSize + HeatCellGap,
                Child = new TextBlock
                {
                    Classes = { "heat-weekday-label" },
                    Text = heat.WeekdayLabels[weekday] ?? string.Empty,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            });
        }

        // 按天格子：行主序（先星期后周列）与 BuildCells 的顺序一致
        HeatGrid.Columns = heat.Weeks;
        HeatGrid.Children.Clear();
        foreach (var cell in heat.Cells)
        {
            var border = new Border
            {
                Width = HeatCellSize,
                Height = HeatCellSize,
                Margin = new Thickness(HeatCellGap / 2d),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            };
            border.Classes.Add(cell.IsBlank ? "heat-blank" : $"heat-{cell.Level}");
            if (cell.IsPeak)
            {
                border.Classes.Add("heat-peak");
            }

            HeatGrid.Children.Add(border);
        }
    }
}
