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

    /// <summary>当前版式的画布宽度（导出按它光栅化）。</summary>
    public double PosterWidth => WidthFor(CurrentLayout);

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
    /// 按版式重排栅格：
    /// <list type="bullet">
    /// <item>竖版（1080）：三行两列——趋势 1.45 / Agent .95；模型 / 类型；项目 / 成本。</item>
    /// <item>横版（1800）：两行三列——趋势 2.1 / Agent 1 / 类型 1；模型 1 / 项目 1 / 成本 1.2。</item>
    /// </list>
    /// 两套版面的面板归属不同（类型环形、成本趋势换行），所以不是单纯换列宽。尺度差异（字号 /
    /// 图表高度 / 环形直径）由 <c>poster-root.land</c> 的样式组承担，这里只管布局与画布宽度。
    /// </summary>
    private void ApplyLayout()
    {
        var landscape = CurrentLayout == SharePosterLayout.Landscape;
        PosterRoot.Width = WidthFor(CurrentLayout);

        if (landscape)
        {
            if (!PosterRoot.Classes.Contains("land"))
            {
                PosterRoot.Classes.Add("land");
            }
            PanelGrid.ColumnDefinitions = new ColumnDefinitions("2.1*,1*,1*");
            PanelGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
        }
        else
        {
            PosterRoot.Classes.Remove("land");
            PanelGrid.ColumnDefinitions = new ColumnDefinitions("1.45*,0.95*,0");
            PanelGrid.RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
        }

        Place(TrendPanel, 0, 0);
        Place(AgentPanel, 0, 1);
        Place(TypePanel, landscape ? 0 : 1, landscape ? 2 : 1);
        Place(ModelPanel, 1, 0);
        Place(ProjectPanel, landscape ? 1 : 2, landscape ? 1 : 0);
        Place(CostPanel, landscape ? 1 : 2, landscape ? 2 : 1);

        static void Place(Control panel, int row, int column)
        {
            Grid.SetRow(panel, row);
            Grid.SetColumn(panel, column);
        }
    }
}
