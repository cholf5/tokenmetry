using System;
using System.Globalization;
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
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
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
        SharePosterTheme theme = SharePosterTheme.Dark, bool singleDay = false,
        SharePosterStyle? style = null, HourlyDistributionResult? hourly = null,
        double margin = SharePosterRequest.DefaultMargin)
    {
        var day = new DateOnly(2026, 10, 17);
        var request = new SharePosterRequest(
            layout, theme, SharePosterProjectMask.Off,
            singleDay ? day : new DateOnly(2026, 1, 28),
            singleDay ? day : new DateOnly(2026, 11, 2),
            style ?? (singleDay ? SharePosterStyle.Tray : SharePosterStyle.Overview),
            Margin: margin);

        var daily = DailyWithCache();

        var data = SharePosterSnapshot.Build(
            request,
            daily,
            [Row("ZCode", 4_535_388_126, 200m), Row("Codex", 366_647_795, null)],
            [Row("glm-5.3-flash", 2_383_429_663, 80m), Row("gpt-5.3-codex", 345_048_948, 90m)],
            [Row("Zearth", 784_282_344, 10m), Row("sven-net", 518_709_985, 5m)],
            sessionCount: 764,
            hourly: hourly,
            hourlyCutoffHour: hourly is null ? null : 12);

        return new SharePosterViewModel(data);
    }

    /// <summary>里程碑英雄区的自建快照：30 天全活跃日序列，总量精确可控——字号各档都要钉住紧凑值字串的量级。</summary>
    private static SharePosterViewModel MilestoneViewModel(
        long totalTokens, double margin = SharePosterRequest.DefaultMargin,
        SharePosterTheme theme = SharePosterTheme.Dark)
    {
        var request = new SharePosterRequest(
            SharePosterLayout.Portrait, theme, SharePosterProjectMask.Off,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30), SharePosterStyle.Milestone,
            Margin: margin);

        var daily = Enumerable.Range(0, 30).Select(i => new DailyUsagePoint
        {
            Day = new DateOnly(2026, 10, 1).AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            InputTokens = totalTokens / 30,
            TotalTokens = totalTokens / 30,
            HasData = true,
        }).ToArray();

        var data = SharePosterSnapshot.Build(
            request, daily,
            [Row("ZCode", totalTokens)],
            [Row("glm-5.3-flash", totalTokens)],
            [Row("Zearth", totalTokens)],
            sessionCount: 87);

        return new SharePosterViewModel(data);
    }

    /// <summary>24 个小时桶的 fixture：token 与已知成本峰值都钉在 9 点（总览基底单日窗口的小时形态用）。</summary>
    private static HourlyDistributionResult Hourly() => new()
    {
        Hours = Enumerable.Range(0, 24)
            .Select(h => new HourlyUsageBucket
            {
                Hour = h,
                TotalTokens = h == 9 ? 900 : h % 3,
                SessionCount = h % 2,
                Cost = new AggregatedCost(h == 9 ? 0.9m : 0.01m * h, false),
            })
            .ToArray(),
    };

    private static Window Host(SharePosterView view) => new()
    {
        Width = 1120,
        Height = 1700,
        Content = view,
        RequestedThemeVariant = ThemeVariant.Dark,
        Background = Brushes.Black,
    };

    private static Window HostWide(SharePosterView view) => new()
    {
        Width = 1900,
        Height = 1000,
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
    public void Heatmap_style_renders_the_heat_base_with_generated_cells()
    {
        var vm = ViewModel(layout: SharePosterLayout.Landscape, style: SharePosterStyle.Heatmap);
        var view = new SharePosterView { DataContext = vm };
        var window = HostWide(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        // 导出根切到热力基底：画布宽随窗口周数收放（fixture ~41 周 → 上限内、不满 1800）
        Assert.True(view.IsHeatmapStyle);
        Assert.Same(
            view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "HeatRoot"),
            view.PosterRootControl);
        Assert.Equal(SharePosterHeatmap.CanvasWidthFor(vm.Heat.Weeks), view.PosterRootControl.Bounds.Width, 1);

        // 格子 = 窗口列数 × 7 行，fixture 是最近 30 天 → 有深档也有峰值描边
        var grid = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.UniformGrid>()
            .First(u => u.Name == "HeatGrid");
        Assert.Equal(vm.Heat.Weeks, grid.Columns);
        Assert.Equal(vm.Heat.Cells.Count, grid.Children.Count);
        Assert.Contains(grid.Children.OfType<Border>(), b => b.Classes.Contains("heat-peak"));
        Assert.Contains(grid.Children.OfType<Border>(), b => b.Classes.Contains("heat-4"));
        Assert.Contains(grid.Children.OfType<Border>(), b => b.Classes.Contains("heat-0"));

        // 卡片有标题与说明行（语言无关：文案与 Translations 同源）
        var texts = view.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.Contains(texts, t => t.Text == Translations.Instance.PanelActivityCalendar);
        Assert.Contains(texts, t => t.Text == Translations.Instance.SharePosterHeatCaption);

        // 格子挂了 tooltip：有数据的格子有值，留白格不挂（fixture 范围对齐周一 → 首列有留白）
        var cells = grid.Children.OfType<Border>().ToList();
        Assert.Contains(cells, b => ToolTip.GetTip(b) is not null);
        Assert.All(cells.Where(b => b.Classes.Contains("heat-blank")), b => Assert.Null(ToolTip.GetTip(b)));

        // 色阶图例（5 个芯片，圈定当前基底——节律基底也有同款图例）+ 峰值/活跃天数脚注都真的在版面上
        Assert.Equal(5, view.PosterRootControl.GetVisualDescendants().OfType<Border>()
            .Count(b => b.Classes.Contains("heat-chip")));
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.HeatPeakText);

        window.Close();
    }

    [AvaloniaFact]
    public void Narrow_heat_range_narrows_the_canvas_and_places_stats_beside_the_grid()
    {
        // 单日窗口（用户实拍的最差情形：周六一天 → 周一对齐后 1 列、6 格范围外）：
        // 画布收窄（1 周 = 560 下限）——不再满 1800 留大段空白；脚注三格搬进卡片与日历并排
        // （海报不再窄而长），底部横排收起
        var day = new DateOnly(2026, 11, 7); // 周六
        var request = new SharePosterRequest(
            SharePosterLayout.Landscape, SharePosterTheme.Dark, SharePosterProjectMask.Off,
            day, day, SharePosterStyle.Heatmap);
        var daily = new[]
        {
            new DailyUsagePoint
            {
                Day = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                TotalTokens = 900_000,
                HasData = true,
            },
        };
        var data = SharePosterSnapshot.Build(request, daily, [], [], [], sessionCount: 2);
        var vm = new SharePosterViewModel(data);
        var view = new SharePosterView { DataContext = vm };
        var window = new Window
        {
            Width = 1200,
            Height = 1500,
            Content = view,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = Brushes.Black,
        };
        using var frame = ChartTestHelpers.RenderFrame(window);

        Assert.Equal(1, vm.Heat.Weeks);
        Assert.Equal(SharePosterHeatmap.CanvasWidthFor(1), view.PosterRootControl.Bounds.Width, 1);

        // 并排脚注在卡内可见、底部横排收起；卡片铺满画布内宽（右缘余量落在卡内当留白）
        Assert.True(view.GetVisualDescendants().OfType<StackPanel>().First(s => s.Name == "HeatStatsCol").IsVisible);
        Assert.False(view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "HeatStatsRow").IsVisible);
        var card = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "HeatCard");
        Assert.Equal(view.PosterRootControl.Bounds.Width - 2 * SharePosterRequest.DefaultMargin, card.Bounds.Width, 1);

        // 范围外格子是可见空槽（不是透明）：单日窗口 7 格里 6 格留槽、1 格有数据
        var grid = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.UniformGrid>()
            .First(u => u.Name == "HeatGrid");
        Assert.Equal(6, grid.Children.OfType<Border>().Count(b => b.Classes.Contains("heat-blank")));
        Assert.All(
            grid.Children.OfType<Border>().Where(b => b.Classes.Contains("heat-blank")),
            b => Assert.NotEqual(0d, b.Bounds.Width));

        window.Close();
    }

    [AvaloniaFact]
    public void Rhythm_style_renders_the_rhythm_base_with_the_7x24_matrix()
    {
        // 节律矩阵不走日序列：给一份确定性的星期 × 小时矩阵（有深档也有峰值）
        var rhythm = new WeeklyRhythmResult
        {
            Cells = Enumerable.Range(0, 7)
                .SelectMany(w => Enumerable.Range(0, 24).Select(h => new WeeklyRhythmCell
                {
                    Weekday = w,
                    Hour = h,
                    TotalTokens = h is >= 8 and <= 20 && (w * 24 + h) % 3 != 0
                        ? (w * 24 + h) % 7 * 120_000L + 50_000L
                        : 0,
                }))
                .ToList(),
        };
        var request = new SharePosterRequest(
            SharePosterLayout.Landscape, SharePosterTheme.Dark, SharePosterProjectMask.Off,
            new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2), SharePosterStyle.Rhythm);
        var data = SharePosterSnapshot.Build(
            request, ChartTestHelpers.DailyPoints30(),
            [Row("ZCode", 4_535_388_126, 200m)],
            [Row("glm-5.3-flash", 2_383_429_663, 80m)],
            [Row("Zearth", 784_282_344, 10m)],
            sessionCount: 764, weeklyRhythm: rhythm);
        var vm = new SharePosterViewModel(data);
        var view = new SharePosterView { DataContext = vm };
        var window = HostWide(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        // 导出根切到节律基底、画布随 7×24 网格与页边距收（不再钉 1800）
        Assert.True(view.IsRhythmStyle);
        Assert.Same(
            view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "RhythmRoot"),
            view.PosterRootControl);
        Assert.Equal(SharePosterHeatmap.WeekdayGutter + 24 * SharePosterView.RhythmColumnWidth
                     + SharePosterHeatmap.CanvasChromeFor(SharePosterRequest.DefaultMargin),
                     view.PosterRootControl.Bounds.Width, 1);

        // 168 格全量生成；fixture 最近 30 天含真实数据 → 有深档与峰值描边
        var grid = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.UniformGrid>()
            .First(u => u.Name == "RhythmGrid");
        Assert.Equal(24, grid.Columns);
        Assert.Equal(vm.Rhythm.Cells.Count, grid.Children.Count);
        Assert.Contains(grid.Children.OfType<Border>(), b => b.Classes.Contains("heat-peak"));
        Assert.Contains(grid.Children.OfType<Border>(), b => b.Classes.Contains("heat-4"));

        // 卡片有标题与说明行；168 格全量挂 tooltip（空档格也如实给「无数据」）
        var texts = view.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.Contains(texts, t => t.Text == Translations.Instance.SharePosterStyleRhythm);
        Assert.Contains(texts, t => t.Text == Translations.Instance.SharePosterRhythmCaption);
        Assert.Equal(
            vm.Rhythm.Cells.Count,
            grid.Children.OfType<Border>().Count(b => ToolTip.GetTip(b) is not null));

        // 小时刻度行与峰值文案在版面上
        var hourRow = view.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "RhythmHourRow");
        Assert.Equal(24, hourRow.ColumnDefinitions.Count);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.RhythmPeakText);

        window.Close();
    }

    [AvaloniaFact]
    public void Milestone_style_renders_the_narrative_base_with_hero_numbers()
    {
        var vm = ViewModel(layout: SharePosterLayout.Landscape, style: SharePosterStyle.Milestone);
        var view = new SharePosterView { DataContext = vm };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        // 导出根切到叙事基底、固定 1080 竖版（起值横版被能力表收口后仍以竖版排）
        Assert.True(view.IsMilestoneStyle);
        Assert.Same(
            view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "MilestoneRoot"),
            view.PosterRootControl);
        Assert.Equal(SharePosterView.PortraitWidth, view.PosterRootControl.Bounds.Width, 1);

        // 修订九（HTML 稿重排）：页头报告小签、英雄锁定值两段、完整数字行、note 行、统计两卡、
        // 模型卡（品牌徽标吃 glm 伪类）——文案与 VM/Translations 同源即语言无关。
        // 修订十：页脚整行删除（品牌只住页头，同其余四张版式），海报收在模型卡 + 页边距。
        var texts = view.PosterRootControl.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(Tokenmetry.App.Localization.Translations.Instance.SharePosterUsageReport, texts);
        Assert.Contains(vm.MilestoneHeroValueText, texts);
        Assert.Equal(vm.HasMilestoneHeroUnit, texts.Contains(vm.MilestoneHeroUnitText));
        Assert.Contains(vm.MilestoneExactTokensText, texts);
        Assert.Contains(vm.MilestoneTopModelName, texts);
        Assert.Contains(vm.MilestoneTopModelCountText, texts);
        Assert.DoesNotContain(texts, t => t == "TOKENMETRY");
        Assert.DoesNotContain(texts, t => t == "tokenmetry.app");
        // note 行 = 单 TextBlock 彩色 Run（一段基线，混排 CJK 不沉底）：段文案全在 Run 上
        var note = view.PosterRootControl.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("milestone-note"));
        var noteRuns = note.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text).ToList();
        Assert.Contains(Tokenmetry.App.Localization.Translations.Instance.KpiPeakDay, noteRuns);
        Assert.Contains(vm.MilestonePeakDateText, noteRuns);
        Assert.Contains(vm.MilestonePeakValueText, noteRuns);
        Assert.Contains(Tokenmetry.App.Localization.Translations.Instance.SharePosterMilestoneStreak, noteRuns);
        Assert.Contains(vm.MilestoneStreakText, noteRuns);

        // 统计两卡 + 模型卡真的在版面上，模型徽标吃到厂商伪类（logo 链生效）
        var borders = view.PosterRootControl.GetVisualDescendants().OfType<Border>().ToList();
        Assert.Equal(2, borders.Count(b => b.Classes.Contains("milestone-stat")));
        Assert.Single(borders, b => b.Classes.Contains("milestone-model-card"));
        Assert.Contains(borders, b => b.Classes.Contains("milestone-badge") && b.Classes.Contains("model-glm"));

        // 纯排版：基底子树内零图表；暗色主题下卡片无投影（投影只对日间，CardSurfaces 统一收）
        Assert.DoesNotContain(view.PosterRootControl.GetVisualDescendants().OfType<TrendChart>(), _ => true);
        Assert.DoesNotContain(view.PosterRootControl.GetVisualDescendants().OfType<Border>(), b => b.Effect is not null);

        window.Close();
    }

    /// <summary>
    /// 里程碑英雄区（修订九定稿）：修订八的右列两格被 HTML 稿的答案取代——英雄通栏，最活跃日/
    /// 最长连续收进 note 行。典型量级英雄保持定尺寸 176（守卫不留本地值），锁定值（尾数 + 间距 +
    /// 单位）不越版心；长串合法上限（long 满量程）在最大版心（margin 0）下也放得下、不误缩。
    /// 收缩路径本身由 <see cref="Hero_font_size_shrinks_only_when_the_lockup_overflows"/> 钉住。
    /// </summary>
    [AvaloniaFact]
    public void Milestone_hero_spans_the_content_width_and_keeps_the_designed_size()
    {
        // 典型档：总量 ~3.34e9 →「33.4」远小于版心 → 字号保持样式值 176（守卫不留本地值）
        var typicalVm = MilestoneViewModel(3_340_000_000);
        var typicalView = new SharePosterView { DataContext = typicalVm };
        var typicalWindow = Host(typicalView);
        using var typicalFrame = ChartTestHelpers.RenderFrame(typicalWindow);
        var typicalHero = typicalView.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Name == "MilestoneHeroText");
        var typicalUnit = typicalView.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Name == "MilestoneHeroUnit");
        Assert.Equal(SharePosterView.MilestoneHeroFontSize, typicalHero.FontSize, 1);

        // 锁定值落在版心内（锁定值容器与版心同原点：尾数右缘 + 间距 + 单位宽 ≤ 内容区宽）
        var contentWidth = SharePosterView.PortraitWidth - 2 * SharePosterRequest.DefaultMargin;
        var lockupWidth = typicalHero.Bounds.Right + SharePosterView.MilestoneHeroUnitSpacing
                          + typicalUnit.Bounds.Width;
        Assert.True(
            lockupWidth <= contentWidth + 1,
            $"hero lockup {lockupWidth} should stay inside content width {contentWidth}");

        // note 行装着总量之后的两个叙事事实（单 TextBlock 彩色 Run，一段基线）：
        // 最活跃日（标签 + 日期 + 蓝色值）与最长连续
        var note = typicalView.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("milestone-note"));
        var noteRuns = note.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text).ToList();
        Assert.Contains(Tokenmetry.App.Localization.Translations.Instance.KpiPeakDay, noteRuns);
        Assert.Contains(typicalVm.MilestonePeakDateText, noteRuns);
        Assert.Contains(typicalVm.MilestonePeakValueText, noteRuns);
        Assert.Contains(Tokenmetry.App.Localization.Translations.Instance.SharePosterMilestoneStreak, noteRuns);
        Assert.Contains(typicalVm.MilestoneStreakText, noteRuns);
        typicalWindow.Close();

        // 长串档：long 满量程（zh 万进制下最宽的合法字串）× 最大版心（margin 0）→ 仍 176、不越版心
        var longView = new SharePosterView
        {
            DataContext = MilestoneViewModel(long.MaxValue, margin: 0),
        };
        var longWindow = Host(longView);
        using var longFrame = ChartTestHelpers.RenderFrame(longWindow);
        var longHero = longView.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Name == "MilestoneHeroText");
        var longUnit = longView.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Name == "MilestoneHeroUnit");
        Assert.Equal(SharePosterView.MilestoneHeroFontSize, longHero.FontSize, 1);
        var longLockup = longHero.Bounds.Right + SharePosterView.MilestoneHeroUnitSpacing
                         + longUnit.Bounds.Width;
        Assert.True(
            longLockup <= SharePosterView.PortraitWidth + 1,
            $"longest lockup {longLockup} should stay inside full-width content {SharePosterView.PortraitWidth}");
        longWindow.Close();
    }

    /// <summary>溢出收缩路径的纯函数锁：预算充足回样式值 176（永不放大），预算不足等比收缩、半磅取整。</summary>
    [AvaloniaFact]
    public void Hero_font_size_shrinks_only_when_the_lockup_overflows()
    {
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

        Assert.Equal(
            SharePosterView.MilestoneHeroFontSize,
            SharePosterView.HeroFontSizeFor("33.4", 900, typeface, -14), 1);
        Assert.Equal(
            SharePosterView.MilestoneHeroFontSize,
            SharePosterView.HeroFontSizeFor("9223372", 800, typeface, -14), 1);

        var shrunk = SharePosterView.HeroFontSizeFor("9223372", 300, typeface, -14);
        Assert.True(shrunk < SharePosterView.MilestoneHeroFontSize, $"300px 预算应收缩，实测 {shrunk}");
        Assert.Equal(0, shrunk % 0.5, 5);
    }

    /// <summary>
    /// 末位数字不得被负字距裁切：TextBlock 默认 ClipToBounds，而负 LetterSpacing 让量测宽
    /// （尾字符之后也吃一格字距）比墨迹包络窄 |spacing|——真机字体数字 bearing 近零时末位
    /// 越界被裁掉一竖条（「7295」的 5）。英雄样式以右内边距补回 |spacing|，本测试锁字体无关
    /// 的不变式：hero bounds 必须 ≥ 量测宽 + |字距|（最坏墨迹包络），否则任何字体都可能裁。
    /// （不钉像素：headless 与真机字体的 bearing 不同，墨迹是否越界本身跨平台不稳定。）
    /// </summary>
    [AvaloniaFact]
    public void Milestone_hero_bounds_cover_the_negative_letter_spacing_envelope()
    {
        var vm = MilestoneViewModel(72_950_000); // 「7295万」：与真机截图同量级
        var view = new SharePosterView { DataContext = vm };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var hero = view.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Name == "MilestoneHeroText");
        using var layout = new Avalonia.Media.TextFormatting.TextLayout(
            hero.Text!, new Typeface(hero.FontFamily, FontStyle.Normal, hero.FontWeight),
            hero.FontSize, Brushes.Black, letterSpacing: hero.LetterSpacing);

        Assert.True(
            hero.Bounds.Width >= layout.Width + Math.Abs(hero.LetterSpacing) - 0.5,
            $"hero bounds {hero.Bounds.Width} 应罩住量测宽 {layout.Width} + 字距包络 " +
            $"{Math.Abs(hero.LetterSpacing)}——否则末位数字会被 ClipToBounds 裁切");
        window.Close();
    }

    [AvaloniaFact]
    public void Multi_day_header_carries_the_brand_not_the_app_page_title()
    {
        var model = ViewModel();
        var view = new SharePosterView { DataContext = model };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        // 页头 = Logo + 字标（与单日托盘同源）；「总览」是应用内的页面名，海报上不存在这一页。
        // 断言圈定当前基底（热力基底也有同款页头，整棵视觉树里会各有一份）
        var baseRoot = view.PosterRootControl;
        var brand = baseRoot.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("poster-brand"));
        Assert.Equal("Tokenmetry", brand.Text);

        // 右侧只剩日期范围；整张海报不再出现页标题「总览」
        var texts = baseRoot.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.DoesNotContain(Tokenmetry.App.Localization.Translations.Instance.NavOverview, texts);
        Assert.Contains(model.RangeText, texts);

        // Logo 真的取到了图片资源（不是空芯片）
        var logo = baseRoot.GetVisualDescendants().OfType<Border>()
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
        // 断言圈定总览基底：热力基底也有一套同款页头（隐藏但挂在树上）
        var baseRoot = view.PosterRootControl;
        var brand = baseRoot.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("poster-brand"));
        var range = baseRoot.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("poster-subtitle"));
        var logo = baseRoot.GetVisualDescendants().OfType<Border>()
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

        var panels = view.PosterRootControl.GetVisualDescendants().OfType<Border>()
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
        Assert.All(Cards(light), card => Assert.True(
            card.Effect is DropShadowEffect { Opacity: > 0 }, "日间海报卡片应带极轻投影"));
        lightWindow.Close();

        var dark = new SharePosterView { DataContext = ViewModel(theme: SharePosterTheme.Dark) };
        var darkWindow = Host(dark);
        using var darkFrame = ChartTestHelpers.RenderFrame(darkWindow);
        Assert.All(Cards(dark), card => Assert.Null(card.Effect));
        darkWindow.Close();

        // 修订九：里程碑统计两卡 + 模型卡同吃日间投影（CardSurfaces 统一收口，不许漏登记）
        var milestone = new SharePosterView
        {
            DataContext = MilestoneViewModel(3_340_000_000, theme: SharePosterTheme.Light),
        };
        var milestoneWindow = Host(milestone);
        using var milestoneFrame = ChartTestHelpers.RenderFrame(milestoneWindow);
        var milestoneCards = milestone.PosterRootControl.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("milestone-stat") || b.Classes.Contains("milestone-model-card"))
            .ToList();
        Assert.Equal(3, milestoneCards.Count);
        Assert.All(milestoneCards, card => Assert.True(
            card.Effect is DropShadowEffect { Opacity: > 0 }, "日间里程碑卡片应带极轻投影"));
        milestoneWindow.Close();

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

    /// <summary>信息卡托盘 fixture：5 个 Agent / 4 个模型，验证 Top4 / Top3 截断与品牌标。</summary>
    private static SharePosterViewModel TrayViewModel(bool singleDay = true,
        double margin = SharePosterRequest.DefaultMargin)
    {
        var day = new DateOnly(2026, 10, 17);
        var request = new SharePosterRequest(
            SharePosterLayout.Portrait, SharePosterTheme.Dark, SharePosterProjectMask.Off, day,
            singleDay ? day : new DateOnly(2026, 11, 2), SharePosterStyle.Tray, Margin: margin);

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
    public void Tray_style_renders_the_tray_base_instead_of_the_overview_grid()
    {
        var view = new SharePosterView { DataContext = TrayViewModel() };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var tray = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TrayRoot");
        var overview = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PosterRoot");

        // 信息卡样式 = 托盘基底（设计 §3.1），总览栅格整体隐藏；导出取的是托盘根。
        // 托盘走独立窄版（TrayWidth，有意偏离竖版 1080）
        Assert.True(tray.IsVisible);
        Assert.False(overview.IsVisible);
        Assert.Equal(SharePosterView.TrayWidth, tray.Bounds.Width, 1);
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
    public void Tray_style_renders_a_range_header_for_multi_day_windows()
    {
        // 样式与范围解耦后的新组合：多日范围 × 信息卡。版式选项被无视（托盘固定 900 窄版），
        // 头部日期行写范围而不是单日「日期 · 周几」。
        var model = ViewModel(layout: SharePosterLayout.Landscape, style: SharePosterStyle.Tray);
        var view = new SharePosterView { DataContext = model };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var tray = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TrayRoot");
        Assert.True(view.IsTrayStyle);
        Assert.True(tray.IsVisible);
        Assert.Equal(SharePosterView.TrayWidth, tray.Bounds.Width, 1);
        Assert.Same(tray, view.PosterRootControl);

        var date = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("tray-date"));
        Assert.Equal(model.TrayDateText, date.Text);

        window.Close();
    }

    [AvaloniaFact]
    public void Overview_style_renders_the_grid_for_a_single_day_range_with_hourly_charts()
    {
        // 样式与范围解耦后的新组合：单日范围 × 总览基底。总览栅格照常渲染，
        // 两张趋势图切小时形态（D-027 同源），且静态图的小时峰值仍有标注。
        var view = new SharePosterView
        {
            DataContext = ViewModel(singleDay: true, style: SharePosterStyle.Overview, hourly: Hourly()),
        };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var overview = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PosterRoot");
        Assert.True(overview.IsVisible);
        Assert.False(view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TrayRoot").IsVisible);
        Assert.Equal(SharePosterView.PortraitWidth, overview.Bounds.Width, 1);
        Assert.Same(overview, view.PosterRootControl);

        var charts = view.GetVisualDescendants().OfType<TrendChart>().ToList();
        Assert.Equal(2, charts.Count);
        Assert.Contains(charts, c => c.Mode == TrendChartMode.HourlyTokens);
        Assert.Contains(charts, c => c.Mode == TrendChartMode.HourlyCost);
        Assert.All(charts, c => Assert.False(string.IsNullOrEmpty(c.PeakAnnotationText)));

        window.Close();
    }

    /// <summary>
    /// 托盘头部的日期与多日页头同一条规矩：跟着字标左起排，不贴卡片右边——
    /// 卡内没有任何右锚点，日期挂在右上就跟下面的三格、构成条左边界都对不上。
    /// </summary>
    [AvaloniaFact]
    public void Tray_header_date_sits_under_the_brand_not_at_the_right_edge()
    {
        var view = new SharePosterView { DataContext = TrayViewModel() };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var tray = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TrayRoot");
        var brand = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("tray-brand"));
        var date = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("tray-date"));
        var logo = view.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("tray-logo"));

        var brandOrigin = brand.TranslatePoint(default, tray)!.Value;
        var dateOrigin = date.TranslatePoint(default, tray)!.Value;
        var logoRight = logo.TranslatePoint(default, tray)!.Value.X + logo.Bounds.Width;

        // 字标与日期共用左边界，且都在 Logo 右侧
        Assert.Equal(brandOrigin.X, dateOrigin.X, 0.5);
        Assert.True(brandOrigin.X > logoRight, "字标应该在 Logo 右侧，不压在图上");
        // 日期在字标下方（品牌领印两行），而不是同排右靠
        Assert.True(dateOrigin.Y > brandOrigin.Y);
        Assert.True(dateOrigin.X < tray.Bounds.Width / 2, "日期应该在左侧阅读线上，不靠右边");
        Assert.True(date.TextAlignment is TextAlignment.Left or TextAlignment.Start, "日期左起排");

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
        Assert.All(Cards(view), c => Assert.Null(c.Effect));

        window.RequestedThemeVariant = ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.All(Cards(view), c => Assert.True(
            c.Effect is DropShadowEffect { Opacity: > 0 }, "跟随日间后卡片应带投影"));
        window.Close();
    }

    [AvaloniaFact]
    public void Light_theme_request_switches_the_subtree_not_the_app()
    {
        var view = new SharePosterView { DataContext = ViewModel(theme: SharePosterTheme.Light) };
        var window = Host(view);
        using var frame = ChartTestHelpers.RenderFrame(window);

        var scope = view.GetVisualDescendants().OfType<ThemeVariantScope>().First();
        Assert.Equal(ThemeVariant.Light, scope.RequestedThemeVariant);

        window.Close();
    }

    /// <summary>
    /// 页边距是画布到内容区的距离：margin=0 时页头 logo 必须贴住顶边（与左边同规）。
    /// 此前页头两子级垂直居中，文字栈比 logo 高，居中把 logo 顶下来一截，五种基底字号组
    /// 不同、这一截各不一样（量得 2/7/8px）——「顶边距与左右不一致且五样式互不相同」。
    /// 修法是页头整体挂内容区顶边（Top），这里把五种基底一起锁住。
    /// </summary>
    [AvaloniaFact]
    public void Zero_margin_puts_every_base_header_logo_flush_with_the_top_edge()
    {
        void AssertFlush(SharePosterView view, Window window, double expectedTop)
        {
            using var frame = ChartTestHelpers.RenderFrame(window);
            var root = (Border)view.PosterRootControl;
            var logo = root.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("poster-logo") || b.Classes.Contains("tray-logo"));
            var brand = root.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Classes.Contains("poster-brand") || t.Classes.Contains("tray-brand"));

            Assert.Equal(expectedTop, root.Padding.Top, 0.5);
            Assert.Equal(expectedTop, logo.TranslatePoint(default, root)!.Value.Y, 0.5);
            Assert.Equal(expectedTop, brand.TranslatePoint(default, root)!.Value.Y, 0.5);
            window.Close();
        }

        // 五种基底在 margin=0 下逐个量：logo 顶边与字标行顶边都必须是 0
        var tray = new SharePosterView { DataContext = TrayViewModel(margin: 0) };
        AssertFlush(tray, Host(tray), 0);

        var portrait = new SharePosterView { DataContext = ViewModel(margin: 0) };
        AssertFlush(portrait, Host(portrait), 0);

        var landscape = new SharePosterView { DataContext = ViewModel(margin: 0, layout: SharePosterLayout.Landscape) };
        AssertFlush(landscape, HostWide(landscape), 0);

        var heat = new SharePosterView
        {
            DataContext = ViewModel(margin: 0, layout: SharePosterLayout.Landscape, style: SharePosterStyle.Heatmap),
        };
        AssertFlush(heat, HostWide(heat), 0);

        var rhythmRequest = new SharePosterRequest(
            SharePosterLayout.Landscape, SharePosterTheme.Dark, SharePosterProjectMask.Off,
            new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2), SharePosterStyle.Rhythm, Margin: 0);
        var rhythmData = SharePosterSnapshot.Build(
            rhythmRequest, ChartTestHelpers.DailyPoints30(), [Row("ZCode", 4_535_388_126, 200m)],
            [Row("glm-5.3-flash", 2_383_429_663, 80m)], [Row("Zearth", 784_282_344, 10m)],
            sessionCount: 764,
            weeklyRhythm: new WeeklyRhythmResult
            {
                Cells = Enumerable.Range(0, 7).SelectMany(w => Enumerable.Range(0, 24).Select(h => new WeeklyRhythmCell
                {
                    Weekday = w,
                    Hour = h,
                    TotalTokens = h is >= 8 and <= 20 && (w * 24 + h) % 3 != 0
                        ? (w * 24 + h) % 7 * 120_000L + 50_000L
                        : 0,
                })).ToList(),
            });
        var rhythm = new SharePosterView { DataContext = new SharePosterViewModel(rhythmData) };
        AssertFlush(rhythm, HostWide(rhythm), 0);

        var milestone = new SharePosterView { DataContext = ViewModel(margin: 0, style: SharePosterStyle.Milestone) };
        AssertFlush(milestone, Host(milestone), 0);

        // 非零页边距同一条规矩：logo 挂内容区顶边，顶边距恰等于页边距本身
        var padded = new SharePosterView { DataContext = ViewModel(margin: 40) };
        AssertFlush(padded, Host(padded), 40);
    }
}
