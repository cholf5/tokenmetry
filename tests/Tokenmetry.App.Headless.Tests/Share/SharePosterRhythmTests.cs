using System;
using System.Linq;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Share;

/// <summary>
/// 时段节律（样式 B）的纯计算：Core 矩阵 → 海报格子。查询失败（null）与无数据同形（168 全零格、
/// 无峰值）；色阶复用打卡热力图的按秩均分档位；峰值取全矩阵最大、并列取最早的（星期, 小时）。
/// </summary>
public class SharePosterRhythmTests
{
    private static WeeklyRhythmResult Rhythm(params (int Weekday, int Hour, long Tokens)[] marks)
    {
        return new WeeklyRhythmResult
        {
            Cells = Enumerable.Range(0, 7)
                .SelectMany(w => Enumerable.Range(0, 24).Select(h => new WeeklyRhythmCell
                {
                    Weekday = w,
                    Hour = h,
                    TotalTokens = marks.Where(m => m.Weekday == w && m.Hour == h).Sum(m => m.Tokens),
                }))
                .ToList(),
        };
    }

    [Fact]
    public void Null_or_empty_rhythm_builds_an_all_zero_grid_without_peak()
    {
        foreach (var grid in new[] { SharePosterRhythm.Build(null), SharePosterRhythm.Build(new WeeklyRhythmResult()) })
        {
            Assert.Equal(SharePosterRhythmGrid.CellCount, grid.Cells.Count);
            Assert.All(grid.Cells, c => Assert.Equal(0, c.Level));
            Assert.Null(grid.PeakWeekday);
            Assert.Equal(0, grid.PeakTokens);
        }
    }

    [Fact]
    public void Cells_map_to_the_weekday_hour_matrix_and_share_the_heat_levels()
    {
        // 三个非零格：档位按全矩阵非零值的秩均分（复用热力图 LevelFor），并列同值同档
        var grid = SharePosterRhythm.Build(Rhythm((0, 9, 100), (3, 14, 900), (6, 21, 100)));

        Assert.Equal(4, grid.Cells.Single(c => c.Weekday == 3 && c.Hour == 14).Level);
        Assert.Equal(1, grid.Cells.Single(c => c.Weekday == 0 && c.Hour == 9).Level);
        Assert.Equal(1, grid.Cells.Single(c => c.Weekday == 6 && c.Hour == 21).Level);
        Assert.All(grid.Cells.Where(c => c.Tokens == 0), c => Assert.Equal(0, c.Level));
    }

    [Fact]
    public void Peak_takes_the_first_maximum_in_weekday_hour_order()
    {
        var grid = SharePosterRhythm.Build(Rhythm((2, 10, 500), (4, 22, 500), (1, 3, 100)));

        // 并列峰值取最早的（星期, 小时）：周二 10 点先于周四 22 点
        Assert.Equal(2, grid.PeakWeekday);
        Assert.Equal(10, grid.PeakHour);
        Assert.Equal(500, grid.PeakTokens);
        Assert.True(grid.Cells.Single(c => c.Weekday == 2 && c.Hour == 10).IsPeak);
        Assert.False(grid.Cells.Single(c => c.Weekday == 4 && c.Hour == 22).IsPeak);
    }

    [Fact]
    public void Out_of_range_coordinates_from_a_stale_result_are_ignored()
    {
        var stale = new WeeklyRhythmResult
        {
            Cells = [new WeeklyRhythmCell { Weekday = 9, Hour = 30, TotalTokens = 999 }],
        };
        var grid = SharePosterRhythm.Build(stale);

        Assert.All(grid.Cells, c => Assert.Equal(0, c.Tokens));
        Assert.Null(grid.PeakWeekday);
    }

    [Fact]
    public void View_model_exposes_rhythm_texts_from_the_snapshot()
    {
        var request = new SharePosterRequest(
            SharePosterLayout.Landscape, SharePosterTheme.Dark, SharePosterProjectMask.Off,
            new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2), SharePosterStyle.Rhythm);
        var data = SharePosterSnapshot.Build(
            request,
            [],
            [], [], [], sessionCount: 0,
            weeklyRhythm: new WeeklyRhythmResult
            {
                Cells = Enumerable.Range(0, 7)
                    .SelectMany(w => Enumerable.Range(0, 24).Select(h => new WeeklyRhythmCell
                        { Weekday = w, Hour = h, TotalTokens = w == 3 && h == 14 ? 900 : 0 }))
                    .ToList(),
                UnknownSessionCount = 4,
                UnknownTotalTokens = 1_200,
            });
        var vm = new SharePosterViewModel(data);

        Assert.True(vm.IsRhythmStyle);
        Assert.Equal(3, vm.Rhythm.PeakWeekday);
        Assert.Equal(14, vm.Rhythm.PeakHour);
        // 峰值文案来自 PeakFormat 字典项（语言无关：占位符实参按序拼进）；unknown 小注非空且可隐藏
        Assert.Contains("14:00", vm.RhythmPeakText, StringComparison.Ordinal);
        Assert.Contains("900", vm.RhythmPeakText, StringComparison.Ordinal);
        Assert.True(vm.ShowRhythmUnknownNote);
        Assert.NotNull(vm.RhythmUnknownNote);

        // 无 unknown：小注隐藏（与时间页同一份口径实现，null 语义一致）
        var clean = SharePosterSnapshot.Build(
            request, [], [], [], [], sessionCount: 0,
            weeklyRhythm: new WeeklyRhythmResult());
        Assert.False(new SharePosterViewModel(clean).ShowRhythmUnknownNote);
    }
}
