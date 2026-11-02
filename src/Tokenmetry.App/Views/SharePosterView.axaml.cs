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

    public SharePosterView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            ApplyRequestedTheme();
            ApplyLayout();
        };
    }

    /// <summary>导出时要光栅化的节点（版面根，不含外面用来适配预览的缩放容器）。</summary>
    public Control PosterRootControl => PosterRoot;

    /// <summary>版式 → 画布宽度（纯函数，测试与导出共用）。</summary>
    public static double WidthFor(SharePosterLayout layout) =>
        layout == SharePosterLayout.Landscape ? LandscapeWidth : PortraitWidth;

    private SharePosterLayout CurrentLayout =>
        (DataContext as SharePosterViewModel)?.Data.Request.Layout ?? SharePosterLayout.Portrait;

    private void ApplyRequestedTheme()
    {
        var theme = (DataContext as SharePosterViewModel)?.Data.Request.Theme ?? SharePosterTheme.FollowApp;
        var dark = theme switch
        {
            SharePosterTheme.Dark => true,
            SharePosterTheme.Light => false,
            _ => Application.Current?.ActualThemeVariant == ThemeVariant.Dark,
        };

        ThemeScope.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        ApplyPanelShadows(dark ? ThemeVariant.Dark : ThemeVariant.Light);
    }

    /// <summary>
    /// 日间海报的卡片投影（设计 §3.5 第 6 条）：浅底上卡片与页面底几乎同色，不分层会糊成一片。
    /// 夜间不给投影（深底上根本看不见，给了只是白白多一层像素）；透明度与偏移全在
    /// <c>AppPosterShadowColor</c> 令牌里，此处只负责拼 BoxShadows——XAML 无法只对 BoxShadow 的
    /// 颜色部分接 DynamicResource（同 D-016 弹窗壳层的既有做法）。
    /// </summary>
    private void ApplyPanelShadows(ThemeVariant variant)
    {
        var shadow = default(BoxShadows);
        // 资源查找覆盖两种时机：DataContextChanged 发生在挂树前（控件自身的资源链还没接到 App），
        // 这时回落到 Application 的资源宿主显式按变体取色。
        var found = TryGetResource("AppPosterShadowColor", variant, out var resource)
                    || (Application.Current?.TryGetResource("AppPosterShadowColor", variant, out resource) ?? false);
        if (found && resource is Color color && color.A > 0)
        {
            var hex = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            shadow = BoxShadows.Parse($"0 1 2 0 {hex}, 0 8 24 0 {hex}");
        }

        foreach (var panel in CardSurfaces())
        {
            panel.BoxShadow = shadow;
        }
    }

    private IEnumerable<Border> CardSurfaces() =>
        KpiGrid.Children.OfType<Border>().Concat(
            new[] { TrendPanel, AgentPanel, ModelPanel, TypePanel, ProjectPanel, CostPanel });

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
}
