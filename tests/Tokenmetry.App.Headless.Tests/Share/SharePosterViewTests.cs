using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Headless.Tests.Controls;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Share;

/// <summary>
/// 分享海报视图（P3）：海报是静态产物，最关键的两条是——图能被渲染出来（不是空壳），
/// 以及两个会"骗过人"的地方确实被关掉了：环形图的扫入动画、趋势图的 hover 依赖。
/// </summary>
public class SharePosterViewTests
{
    private static DimensionUsageRow Row(string key, long total, decimal? cost = 0m) => new()
    {
        Key = key,
        InputTokens = total / 10,
        OutputTokens = total / 100,
        CacheReadTokens = total - total / 10 - total / 100,
        TotalTokens = total,
        Cost = new AggregatedCost(cost, hasUnknownCost: cost is null),
        TokenShare = null,
    };

    /// <summary>30 天 fixture + 缓存读取/写入（类型环形要三段都非零才画得出三色）。</summary>
    private static System.Collections.Generic.IReadOnlyList<DailyUsagePoint> DailyWithCache() =>
        ChartTestHelpers.DailyPoints30().Select(p =>
        {
            var cacheRead = p.HasData ? p.InputTokens * 4 : 0;
            var cacheWrite = p.HasData ? p.InputTokens / 8 : 0;
            return new DailyUsagePoint
            {
                Day = p.Day,
                InputTokens = p.InputTokens,
                OutputTokens = p.OutputTokens,
                CacheReadTokens = cacheRead,
                CacheCreationTokens = cacheWrite,
                TotalTokens = p.TotalTokens + cacheRead + cacheWrite,
                Cost = p.Cost,
                HasData = p.HasData,
            };
        }).ToArray();

    private static SharePosterViewModel ViewModel(SharePosterLayout layout = SharePosterLayout.Portrait,
        SharePosterTheme theme = SharePosterTheme.Dark, bool singleDay = false)
    {
        var day = new DateOnly(2026, 10, 17);
        var request = new SharePosterRequest(
            layout, theme, SharePosterProjectMask.Off,
            singleDay ? day : new DateOnly(2026, 1, 28),
            singleDay ? day : new DateOnly(2026, 11, 2));

        var daily = DailyWithCache();

        var data = SharePosterSnapshot.Build(
            request,
            daily,
            [Row("ZCode", 4_535_388_126, 200m), Row("Codex", 366_647_795, null)],
            [Row("glm-5.3-flash", 2_383_429_663, 80m), Row("gpt-5.3-codex", 345_048_948, 90m)],
            [Row("Zearth", 784_282_344, 10m), Row("sven-net", 518_709_985, 5m)],
            sessionCount: 764);

        return new SharePosterViewModel(data);
    }

    private static Window Host(SharePosterView view) => new()
    {
        Width = 1120,
        Height = 1700,
        Content = view,
        RequestedThemeVariant = ThemeVariant.Dark,
        Background = Brushes.Black,
    };

    [AvaloniaFact]
    public void Poster_renders_at_poster_width_with_both_charts_annotated()
    {
        var view = new SharePosterView { DataContext = ViewModel() };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        // UserControl 被窗口拉伸，真正的版面宽度在 PosterRoot 上（导出按它光栅化）
        var root = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PosterRoot");
        Assert.Equal(SharePosterView.PortraitWidth, root.Bounds.Width, 1);

        var charts = view.GetVisualDescendants().OfType<TrendChart>().ToList();
        Assert.Equal(2, charts.Count);
        // 海报没有 hover：两张图都必须自带峰值标注
        Assert.All(charts, c => Assert.False(string.IsNullOrEmpty(c.PeakAnnotationText)));

        window.Close();
    }

    [AvaloniaFact]
    public void Multi_day_header_carries_the_brand_not_the_app_page_title()
    {
        var model = ViewModel();
        var view = new SharePosterView { DataContext = model };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        // 页头 = Logo + 字标（与单日托盘同源）；「总览」是应用内的页面名，海报上不存在这一页
        var brand = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("poster-brand"));
        Assert.Equal("Tokenmetry", brand.Text);

        // 右侧只剩日期范围；整张海报不再出现页标题「总览」
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.DoesNotContain(Tokenmetry.App.Localization.Translations.Instance.NavOverview, texts);
        Assert.Contains(model.RangeText, texts);

        // Logo 真的取到了图片资源（不是空芯片）
        var logo = view.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("poster-logo"));
        Assert.Contains(logo.GetVisualDescendants().OfType<Image>(), i => i.Source is not null);

        window.Close();
    }

    /// <summary>
    /// 页头只有一个阅读起点：字标与日期范围共用一个左边界（Logo 右侧），且不右对齐——
    /// 海报上没有第二个从右边起排的元素，日期挂在右上角就没有参照系。
    /// </summary>
    [AvaloniaFact]
    public void Header_date_range_shares_the_wordmark_left_edge()
    {
        var view = new SharePosterView { DataContext = ViewModel() };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var root = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PosterRoot");
        var brand = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("poster-brand"));
        var range = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("poster-subtitle"));
        var logo = view.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("poster-logo"));

        var brandX = brand.TranslatePoint(default, root)!.Value.X;
        var rangeX = range.TranslatePoint(default, root)!.Value.X;
        var logoRight = logo.TranslatePoint(default, root)!.Value.X + logo.Bounds.Width;

        Assert.Equal(brandX, rangeX, 0.5);
        Assert.True(brandX > logoRight, "字标应该在 Logo 右侧，不压在图上");
        // 左起排：右对齐的日期会贴回右上角，那正是要去掉的那个效果
        Assert.True(range.TextAlignment is TextAlignment.Left or TextAlignment.Start, "日期范围左起排，不贴右上角");

        window.Close();
    }

    [AvaloniaFact]
    public void Landscape_renders_1800_wide_with_two_rows_of_three_panels()
    {
        var view = new SharePosterView
        {
            DataContext = ViewModel(layout: SharePosterLayout.Landscape),
        };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var root = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PosterRoot");
        Assert.Equal(SharePosterView.LandscapeWidth, root.Bounds.Width, 1);

        // 横版不是竖版的缩放：两行三列，类型环形从第二行提到第一行、成本趋势并入第二行。
        // 两种版式各带一套固定定义的容器，切换只移动面板 + 换可见性。
        var top = Row(view, "LandscapeRow1");
        var middle = Row(view, "LandscapeRow2");
        Assert.Equal(3, top.ColumnDefinitions.Count);
        Assert.Equal(3, middle.ColumnDefinitions.Count);
        Assert.True(top.IsVisible);
        Assert.True(middle.IsVisible);
        Assert.False(Row(view, "PortraitRow1").IsVisible);
        Assert.False(Row(view, "PortraitRow2").IsVisible);
        Assert.False(Row(view, "PortraitRow3").IsVisible);

        // 行内比例：趋势 2.1 / Agent 1 / 类型 1；模型 1 / 项目 1 / 成本 1.2（设计 §3.3）
        AssertRoughRatio(top, expectedFirst: 2.1, otherIndex: 1, expectedOther: 1.0);
        AssertRoughRatio(middle, expectedFirst: 1.0, otherIndex: 2, expectedOther: 1.2);

        var panels = view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("poster-panel"))
            .ToList();
        Assert.Equal(6, panels.Count);
        Assert.Equal(3, top.Children.Count);
        Assert.Equal(3, middle.Children.Count);

        // 图表仍按海报尺寸渲染（高度走横版样式组，不是竖版的 240）
        var charts = view.GetVisualDescendants().OfType<TrendChart>().ToList();
        Assert.All(charts, c => Assert.Equal(250, c.Bounds.Height, 0));
        Assert.All(view.GetVisualDescendants().OfType<DonutChart>(), d => Assert.Equal(168, d.DonutSize, 0));

        window.Close();
    }

    [AvaloniaFact]
    public void Peak_annotation_is_a_point_with_guide_and_transparent_label()
    {
        var view = new SharePosterView { DataContext = ViewModel() };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        foreach (var chart in view.GetVisualDescendants().OfType<TrendChart>())
        {
            var labels = chart.Plot.GetPlottables<ScottPlot.Plottables.Text>().ToList();
            var label = Assert.Single(labels);
            // 文字直接落在图上（海报没有 hover）：不能是 ScottPlot 标注默认的黄底高亮
            Assert.Equal(ScottPlot.Colors.Transparent, label.LabelBackgroundColor);
            // 峰值点 + 从零轴到峰值的虚线：读者要能把数字对应到哪一根柱
            Assert.NotEmpty(chart.Plot.GetPlottables<ScottPlot.Plottables.Marker>());
            Assert.NotEmpty(chart.Plot.GetPlottables<ScottPlot.Plottables.VerticalLine>());
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Light_poster_cards_get_the_light_shadow_dark_ones_get_none()
    {
        var light = new SharePosterView { DataContext = ViewModel(theme: SharePosterTheme.Light) };
        var lightWindow = Host(light);
        using var lightFrame = ChartTestHelpers.RenderFrame(lightWindow);
        Assert.All(Cards(light), card => Assert.True(card.BoxShadow.Count > 0, "日间海报卡片应带极轻投影"));
        lightWindow.Close();

        var dark = new SharePosterView { DataContext = ViewModel(theme: SharePosterTheme.Dark) };
        var darkWindow = Host(dark);
        using var darkFrame = ChartTestHelpers.RenderFrame(darkWindow);
        Assert.All(Cards(dark), card => Assert.Equal(0, card.BoxShadow.Count));
        darkWindow.Close();

        static System.Collections.Generic.List<Border> Cards(SharePosterView view) => view
            .GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("kpi") || b.Classes.Contains("poster-panel"))
            .ToList();
    }

    [AvaloniaFact]
    public void Model_rows_carry_built_in_vendor_logos()
    {
        var view = new SharePosterView { DataContext = ViewModel() };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        // 模型行 = 品牌芯片 + 名称（与总览页同一条 ModelChip 链），项目行保持纯文本
        var chips = view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("poster-model-chip"))
            .ToList();
        Assert.Equal(2, chips.Count);   // fixture 两个模型行

        // 厂商伪类由附加属性驱动：glm / openai 命中各自样式组
        Assert.Contains(chips, c => c.Classes.Contains("model-glm"));
        Assert.Contains(chips, c => c.Classes.Contains("model-openai"));

        // logo 真的取到了 DrawingImage（不是空心芯片）：至少一个可见 Image 有 Source
        var logos = chips
            .SelectMany(c => c.GetVisualDescendants().OfType<Image>())
            .Where(i => i.Classes.Contains("model-logo"))
            .ToList();
        Assert.Contains(logos, i => i.Source is not null);

        window.Close();
    }

    [AvaloniaFact]
    public void Switching_layout_back_and_forth_leaves_no_residue()
    {
        var view = new SharePosterView { DataContext = ViewModel(layout: SharePosterLayout.Landscape) };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);
        Assert.Equal(SharePosterView.LandscapeWidth, view.PosterRootControl.Bounds.Width, 1);

        view.DataContext = ViewModel(layout: SharePosterLayout.Portrait);
        using var portraitFrame = ChartTestHelpers.RenderFrame(window);

        Assert.Equal(SharePosterView.PortraitWidth, view.PosterRootControl.Bounds.Width, 1);
        Assert.DoesNotContain("land", view.PosterRootControl.Classes);
        Assert.True(Row(view, "PortraitRow1").IsVisible);
        Assert.True(Row(view, "PortraitRow2").IsVisible);
        Assert.True(Row(view, "PortraitRow3").IsVisible);
        Assert.False(Row(view, "LandscapeRow1").IsVisible);
        Assert.False(Row(view, "LandscapeRow2").IsVisible);
        Assert.Equal(2, Row(view, "PortraitRow1").Children.Count);
        Assert.Equal(2, Row(view, "PortraitRow2").Children.Count);
        Assert.Equal(2, Row(view, "PortraitRow3").Children.Count);

        window.Close();
    }

    private static Grid Row(SharePosterView view, string name) =>
        (Grid)view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == name);

    /// <summary>断言同一行内两列的实际宽度比（布局完成后才有效）。</summary>
    private static void AssertRoughRatio(Grid row, double expectedFirst, int otherIndex, double expectedOther)
    {
        var first = row.ColumnDefinitions[0].ActualWidth;
        var other = row.ColumnDefinitions[otherIndex].ActualWidth;
        Assert.True(first > 0 && other > 0, "行栅格应完成布局");
        Assert.InRange(other / first, expectedOther / expectedFirst * 0.94, expectedOther / expectedFirst * 1.06);
    }

    /// <summary>单日托盘 fixture：5 个 Agent / 4 个模型，验证 Top4 / Top3 截断与品牌标。</summary>
    private static SharePosterViewModel TrayViewModel()
    {
        var day = new DateOnly(2026, 10, 17);
        var request = new SharePosterRequest(
            SharePosterLayout.Portrait, SharePosterTheme.Dark, SharePosterProjectMask.Off, day, day);

        var data = SharePosterSnapshot.Build(
            request,
            DailyWithCache(),
            [
                Row("ZCode", 5_000_000, 50m), Row("Codex", 4_000_000, 40m), Row("DSH", 3_000_000, 30m),
                Row("Cindy", 2_000_000, 20m), Row("OpenCode", 1_000_000, 10m),
            ],
            [
                Row("glm-5.3-flash", 4_000_000, 40m), Row("gpt-5.3-codex", 3_000_000, 30m),
                Row("deepseek-flash", 2_000_000, 20m), Row("qwen3-max", 1_000_000, 10m),
            ],
            [Row("Zearth", 1_000_000, 10m)],
            sessionCount: 20);

        return new SharePosterViewModel(data);
    }

    [AvaloniaFact]
    public void Single_day_window_renders_the_tray_base_instead_of_the_overview_grid()
    {
        var view = new SharePosterView { DataContext = TrayViewModel() };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var tray = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TrayRoot");
        var overview = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PosterRoot");

        // 单日 = 信息窗基底（设计 §3.1），总览栅格整体隐藏；导出取的是托盘根
        Assert.True(tray.IsVisible);
        Assert.False(overview.IsVisible);
        Assert.Equal(SharePosterView.PortraitWidth, tray.Bounds.Width, 1);
        Assert.Same(tray, view.PosterRootControl);

        // 主要 Agent Top4、模型用量 Top3，各自带品牌标（Agent 走 AgentChip、模型走 ModelChip）
        var chips = view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("tray-chip"))
            .ToList();
        Assert.Equal(4, chips.Count(c => c.Classes.Contains("agent-chip")));
        Assert.Equal(3, chips.Count(c => c.Classes.Contains("model-chip")));
        Assert.Contains(chips, c => c.Classes.Contains("agent-zai"));

        window.Close();
    }

    [AvaloniaFact]
    public void Donuts_render_final_arcs_without_sweep_animation()
    {
        var view = new SharePosterView { DataContext = ViewModel() };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var donuts = view.GetVisualDescendants().OfType<DonutChart>().ToList();
        Assert.Equal(2, donuts.Count);
        Assert.All(donuts, d => Assert.False(d.AnimateSweep));

        // 离屏渲染时扫入动画不跑——若没关，弧段几何会停在退化状态，这里会是 0
        var arcs = donuts
            .SelectMany(d => d.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>())
            .Where(p => p.Data is not null)
            .Count(p => p.Data!.Bounds.Width > 1 && p.Data.Bounds.Height > 1);
        Assert.True(arcs >= 5, $"环形图应画出至少 5 段实弧，实测 {arcs}");

        window.Close();
    }

    [AvaloniaFact]
    public void Follow_app_theme_inherits_and_tracks_the_app_variant()
    {
        // FollowApp = 不钉主题（RequestedThemeVariant 为 null），应用切明暗时海报与投影跟着变
        var view = new SharePosterView { DataContext = ViewModel(theme: SharePosterTheme.FollowApp) };
        var window = new Window
        {
            Width = 1120,
            Height = 1700,
            Content = view,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = Brushes.Black,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scope = view.GetVisualDescendants().OfType<ThemeVariantScope>().First();
        Assert.Null(scope.RequestedThemeVariant);

        static System.Collections.Generic.List<Border> Cards(SharePosterView v) => v
            .GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("kpi") || b.Classes.Contains("poster-panel"))
            .ToList();
        Assert.All(Cards(view), c => Assert.Equal(0, c.BoxShadow.Count));

        window.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.All(Cards(view), c => Assert.True(c.BoxShadow.Count > 0, "跟随日间后卡片应带投影"));
        window.Close();
    }

    [AvaloniaFact]
    public void Light_theme_request_switches_the_subtree_not_the_app()
    {
        var view = new SharePosterView { DataContext = ViewModel(theme: SharePosterTheme.Light) };
        var window = Host(view);   // 应用侧仍是 Dark
        using var frame = ChartTestHelpers.RenderFrame(window);

        var scope = view.GetVisualDescendants().OfType<ThemeVariantScope>().First();
        Assert.Equal(ThemeVariant.Light, scope.RequestedThemeVariant);

        window.Close();
    }
}
