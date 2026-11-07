using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Share;

/// <summary>
/// 打卡热力图（样式 A）的窗口与色阶纯计算：周列对齐周一、范围外留白、52 周封窗锚定最后一周、
/// 色阶按秩均分（同值同档）、单活跃日给最深档、峰值取窗口最大且日序列缺位不冒充数据。
/// </summary>
public class SharePosterHeatmapTests
{
    private static IReadOnlyList<DailyUsagePoint> Daily(DateOnly start, int days, params (int Offset, long Tokens)[] marks)
    {
        var offsets = marks.Select(m => m.Offset).ToHashSet();
        return Enumerable.Range(0, days)
            .Select(i => new DailyUsagePoint
            {
                Day = start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                TotalTokens = marks.Where(m => m.Offset == i).Sum(m => m.Tokens),
                HasData = offsets.Contains(i),
            })
            .ToArray();
    }

    [Fact]
    public void Short_range_aligns_columns_to_monday_and_blanks_outside()
    {
        // 2026-01-05 是周一：1/7(三)..1/8(四) 的两天范围 → 单列 7 行，列首列尾各留白
        var grid = SharePosterHeatmap.Build(
            new DateOnly(2026, 1, 7), new DateOnly(2026, 1, 8),
            Daily(new DateOnly(2026, 1, 7), 2, (0, 500)));

        Assert.Equal(1, grid.Weeks);
        Assert.Equal(new DateOnly(2026, 1, 5), grid.StartMonday);
        Assert.Equal(7, grid.Cells.Count);
        Assert.Equal(5, grid.Cells.Count(c => c.IsBlank));
        Assert.Equal(
            [new DateOnly(2026, 1, 7), new DateOnly(2026, 1, 8)],
            grid.Cells.Where(c => !c.IsBlank).Select(c => c.Day).ToArray());
    }

    [Fact]
    public void Window_caps_at_52_weeks_anchored_to_the_last_week()
    {
        // 两年多的「全部时间」→ 最近 52 周（364 格全在范围内，无留白）；空日序列 = 不冒充峰值。
        // 范围末日取周日：最后一列（周一锚定）整体落在范围内
        var grid = SharePosterHeatmap.Build(
            new DateOnly(2024, 1, 1), new DateOnly(2026, 11, 8), Daily(new DateOnly(2026, 11, 1), 0));

        Assert.Equal(52, grid.Weeks);
        Assert.Equal(new DateOnly(2025, 11, 10), grid.StartMonday);
        Assert.Equal(52 * 7, grid.Cells.Count);
        Assert.DoesNotContain(grid.Cells, c => c.IsBlank);
        Assert.All(grid.Cells, c => Assert.Equal(0, c.Level));
        Assert.Null(grid.PeakDay);
    }

    [Fact]
    public void Levels_are_rank_based_and_ties_share_a_level()
    {
        // 1/5(一)..1/11(日)：四天同为 100、周五 900 → 同值同档（100 → 1 档），峰值独占最深档
        var grid = SharePosterHeatmap.Build(
            new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 11),
            Daily(new DateOnly(2026, 1, 5), 7, (0, 100), (1, 100), (2, 100), (3, 100), (4, 900)));

        Assert.All(grid.Cells.Where(c => c.Tokens == 100), c => Assert.Equal(1, c.Level));
        var peak = grid.Cells.Single(c => c.Tokens == 900);
        Assert.Equal(4, peak.Level);
        Assert.True(peak.IsPeak);
        Assert.Equal(new DateOnly(2026, 1, 9), grid.PeakDay);
        Assert.Equal(900, grid.PeakTokens);
    }

    [Fact]
    public void Distinct_values_spread_across_the_four_levels()
    {
        var grid = SharePosterHeatmap.Build(
            new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 8),
            Daily(new DateOnly(2026, 1, 5), 4, (0, 10), (1, 20), (2, 30), (3, 40)));

        Assert.Equal(
            [1, 2, 3, 4],
            grid.Cells.Where(c => !c.IsBlank).Select(c => c.Level).ToArray());
    }

    [Fact]
    public void Single_active_day_gets_the_deepest_level_and_the_peak()
    {
        var grid = SharePosterHeatmap.Build(
            new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 11),
            Daily(new DateOnly(2026, 1, 5), 7, (5, 50)));

        var cell = grid.Cells.Single(c => c.Day == new DateOnly(2026, 1, 10));
        Assert.Equal(4, cell.Level);
        Assert.True(cell.IsPeak);
        Assert.Equal(new DateOnly(2026, 1, 10), grid.PeakDay);
    }

    [Fact]
    public void Month_labels_mark_the_column_containing_the_first_of_the_month()
    {
        // 1/26(一)..3/8(日)：2 月标在含 2/1 的列（第 0 列，因为 1/26+6 = 2/1），3 月标在含 3/1 的第 4 列；
        // 1 月不在范围内就不标——不画范围外的事实
        var grid = SharePosterHeatmap.Build(
            new DateOnly(2026, 1, 26), new DateOnly(2026, 3, 8), Daily(new DateOnly(2026, 1, 26), 42));

        Assert.Equal(6, grid.Weeks);
        Assert.Equal([0, 4], grid.MonthLabels.Select(l => l.StartCol).ToArray());
        Assert.Equal([4, 2], grid.MonthLabels.Select(l => l.Span).ToArray());
        Assert.All(grid.MonthLabels, l => Assert.False(string.IsNullOrWhiteSpace(l.Text)));
    }

    [Fact]
    public void Levelfor_edge_cases()
    {
        Assert.Equal(0, SharePosterHeatmap.LevelFor(0, [100, 200]));
        // 单活跃日即窗口峰值 → 最深档
        Assert.Equal(4, SharePosterHeatmap.LevelFor(100, [100]));
        // 越过更高秩的阈值才升档
        Assert.Equal(1, SharePosterHeatmap.LevelFor(100, [100, 200, 300, 400]));
        Assert.Equal(4, SharePosterHeatmap.LevelFor(400, [100, 200, 300, 400]));
    }

    [Fact]
    public void Column_width_and_canvas_width_follow_the_week_count()
    {
        // 满 52 周：底部横排档预算 1608/52 收窄到基准列宽 31、画布 1800——与既有固定横版完全一致
        Assert.Equal(SharePosterView.HeatCellSize + 4, SharePosterHeatmap.ColumnWidthFor(52));
        Assert.Equal(SharePosterView.LandscapeWidth, SharePosterHeatmap.CanvasWidthFor(52));

        // 列宽随周数收放：各档内单调不增（并排档/底部档预算不同，档位边界 41/42 处允许抬升）
        for (var weeks = 1; weeks <= 52; weeks++)
        {
            var column = SharePosterHeatmap.ColumnWidthFor(weeks);
            Assert.InRange(column, SharePosterView.HeatCellSize + 4, SharePosterHeatmap.MaxCellSize + 4);
            var neighbor = weeks < 52 ? SharePosterHeatmap.ColumnWidthFor(weeks + 1) : column;
            if (weeks != SharePosterHeatmap.MaxBesideWeeks)
            {
                Assert.True(column >= neighbor, $"列宽应在 {weeks} 周处不增");
            }

            var canvas = SharePosterHeatmap.CanvasWidthFor(weeks);
            Assert.InRange(canvas, SharePosterHeatmap.MinCanvasWidth, SharePosterView.LandscapeWidth);
        }

        // 极窄范围：并排档画布 = 网格 + 占用 + 脚注列，1 周 576——不再满 1800 留大段空白
        Assert.Equal(52 + SharePosterHeatmap.MaxCellSize + 4
                     + 140 + SharePosterHeatmap.BesideStatsColumnWidth + SharePosterHeatmap.BesideStatsColumnSpacing,
                     SharePosterHeatmap.CanvasWidthFor(1));
        Assert.Equal(52 + 4 * (SharePosterHeatmap.MaxCellSize + 4)
                     + 140 + SharePosterHeatmap.BesideStatsColumnWidth + SharePosterHeatmap.BesideStatsColumnSpacing,
                     SharePosterHeatmap.CanvasWidthFor(4));
    }

    [Fact]
    public void Beside_stats_follow_the_week_count()
    {
        // 窄/中范围脚注并排进卡片；长范围（网格自己占满 1800）回到底部横排
        for (var weeks = 1; weeks <= 52; weeks++)
        {
            Assert.Equal(weeks <= SharePosterHeatmap.MaxBesideWeeks, SharePosterHeatmap.UseBesideStats(weeks));
        }

        Assert.True(SharePosterHeatmap.UseBesideStats(5));
        Assert.False(SharePosterHeatmap.UseBesideStats(42));
    }

    [Fact]
    public void Cell_tooltip_describes_the_day_or_the_no_data_state()
    {
        var grid = SharePosterHeatmap.Build(
            new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 7),
            Daily(new DateOnly(2026, 1, 5), 3, (0, 1_200_000)));

        // 活动日：日期 · 紧凑值 + 单位（与时间页热力图同一文案口径）
        var active = grid.Cells.Single(c => c.Day == new DateOnly(2026, 1, 5));
        Assert.Equal(
            "2026.01.05 · " + TrendChart.FormatCompactTokens(1_200_000) + " " + Translations.Instance.ChartTipTokenUnit,
            SharePosterHeatmap.CellTooltip(active));

        // 范围内无用量日：如实「无数据」，不冒充 0
        var empty = grid.Cells.Single(c => c.Day == new DateOnly(2026, 1, 6));
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, Translations.Instance.HeatmapNoDataTooltip, "2026.01.06"),
            SharePosterHeatmap.CellTooltip(empty));

        // 范围外留白格：不给 tooltip
        Assert.Null(SharePosterHeatmap.CellTooltip(grid.Cells.First(c => c.IsBlank)));
    }

    [Fact]
    public void Cells_are_weekday_major_so_days_land_on_their_weekday_row()
    {
        // 修订六回归锁：UniformGrid(Columns=weeks) 按行主序填充（child i → row i/weeks），
        // 格子必须按星期行主序生成——跨 ≥ 2 周时日子才落在自己的星期行上（此前按周优先，
        // 日子蛇形串位：星期行标签对不上真实日期、峰值描边落到错误的行）
        var grid = SharePosterHeatmap.Build(
            new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 25), Daily(new DateOnly(2026, 1, 5), 21));

        Assert.Equal(3, grid.Weeks);
        for (var weekday = 0; weekday < 7; weekday++)
        {
            for (var week = 0; week < grid.Weeks; week++)
            {
                Assert.Equal(
                    new DateOnly(2026, 1, 5).AddDays(week * 7 + weekday),
                    grid.Cells[weekday * grid.Weeks + week].Day);
            }
        }
    }

    [Fact]
    public void Tied_peaks_take_the_earliest_date()
    {
        // 并列峰值取最早日期（与格子顺序无关）：1/5(一) 与 1/8(四) 同为峰值 → 取 1/5
        var grid = SharePosterHeatmap.Build(
            new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 11),
            Daily(new DateOnly(2026, 1, 5), 7, (0, 900), (3, 900)));

        Assert.Equal(new DateOnly(2026, 1, 5), grid.PeakDay);
        Assert.True(grid.Cells.Single(c => c.Day == new DateOnly(2026, 1, 5)).IsPeak);
        Assert.False(grid.Cells.Single(c => c.Day == new DateOnly(2026, 1, 8)).IsPeak);
    }

    [Fact]
    public void View_model_builds_the_heat_grid_for_the_request_window()
    {
        var request = new SharePosterRequest(
            SharePosterLayout.Landscape, SharePosterTheme.Dark, SharePosterProjectMask.Off,
            new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 11), SharePosterStyle.Heatmap);
        var data = SharePosterSnapshot.Build(
            request,
            Daily(new DateOnly(2026, 1, 5), 7, (0, 100), (4, 900)),
            [], [], [], sessionCount: 0);
        var vm = new SharePosterViewModel(data);

        Assert.True(vm.IsHeatmapStyle);
        Assert.False(vm.IsOverviewStyle);
        Assert.Equal(1, vm.Heat.Weeks);
        Assert.Equal(new DateOnly(2026, 1, 9), vm.Heat.PeakDay);
        // 峰值脚注 = 紧凑值 · 日期（日期段固定 Invariant yyyy.MM.dd）；无峰值窗口如实写「—」
        Assert.EndsWith(" · 2026.01.09", vm.HeatPeakText, StringComparison.Ordinal);
        Assert.Equal(vm.DayCount.ToString("N0", CultureInfo.CurrentCulture), vm.HeatActiveDaysText);
    }
}
