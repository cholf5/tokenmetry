using System;
using System.Collections.Generic;
using System.Linq;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>时段节律矩阵的一个格子。星期 0 = 周一（展示口径与打卡热力图一致）；Level 0..4 = 空档/四档深浅。</summary>
public sealed record PosterRhythmCell(int Weekday, int Hour, long Tokens, int Level, bool IsPeak);

/// <summary>
/// 时段节律（样式 B）的格子与峰值：7 × 24 矩阵，顺序与 UniformGrid(Rows=7, Columns=24) 的行主序一致
/// （先星期行后小时列）。纯函数、无 UI 依赖；色阶复用打卡热力图的按秩均分档位
/// （<see cref="SharePosterHeatmap.LevelFor"/>，窗口 = 全部格子的非零值）。
/// </summary>
public sealed record SharePosterRhythmGrid(
    IReadOnlyList<PosterRhythmCell> Cells,
    int? PeakWeekday,
    int? PeakHour,
    long PeakTokens)
{
    /// <summary>格子总数（7 × 24 = 168，含全零）。</summary>
    public const int CellCount = 7 * 24;
}

/// <summary>
/// 时段节律矩阵的纯计算：<see cref="WeeklyRhythmResult"/>（Core 查询结果）→ 海报格子。
/// 查询失败（null）与无数据同形：168 个全零格、无峰值——矩阵如实空场，不冒充事实。
/// </summary>
public static class SharePosterRhythm
{
    public static SharePosterRhythmGrid Build(WeeklyRhythmResult? rhythm)
    {
        var tokens = new long[SharePosterRhythmGrid.CellCount];
        if (rhythm is not null)
        {
            foreach (var cell in rhythm.Cells)
            {
                if (cell.Weekday is >= 0 and < 7 && cell.Hour is >= 0 and < 24)
                {
                    tokens[cell.Weekday * 24 + cell.Hour] = cell.TotalTokens;
                }
            }
        }

        var sorted = tokens.Where(t => t > 0).Order().ToArray();
        var peakTokens = sorted.Length > 0 ? sorted[^1] : 0;

        var cells = new PosterRhythmCell[SharePosterRhythmGrid.CellCount];
        int? peakWeekday = null;
        int? peakHour = null;
        for (var weekday = 0; weekday < 7; weekday++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var value = tokens[weekday * 24 + hour];
                var isPeak = peakTokens > 0 && value == peakTokens && peakWeekday is null;
                if (isPeak)
                {
                    peakWeekday = weekday;
                    peakHour = hour;
                }

                cells[weekday * 24 + hour] = new PosterRhythmCell(
                    weekday, hour, value, SharePosterHeatmap.LevelFor(value, sorted), isPeak);
            }
        }

        return new SharePosterRhythmGrid(cells, peakWeekday, peakHour, peakTokens);
    }
}
