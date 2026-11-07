using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tokenmetry.App.ViewModels;
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
