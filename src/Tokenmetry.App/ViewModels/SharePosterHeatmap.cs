using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>打卡热力图的一个格子。IsBlank = 范围外的占位格（对齐星期行用，不画底）；Level 0..4 = 空档/四档深浅。</summary>
public sealed record PosterHeatCell(DateOnly Day, long Tokens, int Level, bool IsBlank, bool IsPeak);

/// <summary>月份标签：落在第 <paramref name="StartCol"/> 列，横跨 <paramref name="Span"/> 列（左对齐）。</summary>
public sealed record PosterHeatMonthLabel(int StartCol, int Span, string Text);

/// <summary>
/// 打卡热力图（样式 A）的窗口与格子：周为列（周一起）、星期为行，最多 <see cref="MaxWeeks"/> 周。
/// 纯函数、无 UI 依赖；色阶是窗口内活跃日的相对档位（同 GitHub 贡献图的分位逻辑），不换算绝对量。
/// </summary>
/// <param name="WeekdayLabels">星期行标签（周一起 7 行，只标一/三/五，其余 null 占位）。</param>
/// <param name="PeakDay">单日峰值日（窗口内 token 最多的日子）；全空窗口为 null。</param>
public sealed record SharePosterHeatGrid(
    int Weeks,
    DateOnly StartMonday,
    IReadOnlyList<PosterHeatCell> Cells,
    IReadOnlyList<PosterHeatMonthLabel> MonthLabels,
    IReadOnlyList<string?> WeekdayLabels,
    DateOnly? PeakDay,
    long PeakTokens)
{
    /// <summary>格子在列优先（周）里的列号 = (Day - StartMonday).Days / 7；星期行 = 周一起第几行。</summary>
    public int ColumnOf(DateOnly day) => (day.DayNumber - StartMonday.DayNumber) / 7;
}

/// <summary>
/// 打卡热力图的窗口规则与色阶计算。开窗（设计 §3.1 修订 2026-11-07）：范围不足 <see cref="MaxWeeks"/>
/// 周时按实际范围（对齐到周一列，范围外的格子留白），更长范围（如「全部时间」）只取最近 52 周——
/// 海报是印刷品，长度必须可控。色阶：窗口内非零日按秩均分四档（同值同档），单活跃日给最深档。
/// </summary>
public static class SharePosterHeatmap
{
    /// <summary>热力图最多画的周数（GitHub 贡献图的标准窗口）。</summary>
    public const int MaxWeeks = 52;

    public static SharePosterHeatGrid Build(
        DateOnly rangeStart, DateOnly rangeEnd, IReadOnlyList<DailyUsagePoint> daily)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rangeStart, rangeEnd);

        var endMonday = Monday(rangeEnd);
        var startMonday = Monday(rangeStart);
        var weeks = (endMonday.DayNumber - startMonday.DayNumber) / 7 + 1;
        if (weeks > MaxWeeks)
        {
            weeks = MaxWeeks;
            startMonday = endMonday.AddDays(-(MaxWeeks - 1) * 7);
        }

        // 窗口内按天取 token：日序列本身覆盖整个范围（缺失日补零、HasData=false），这里只取数值；
        // HasData 的空日与补零日在热力图上同为「无用量」的空档色，不区分（诚实度一致）。
        var tokensByDay = new Dictionary<DateOnly, long>();
        foreach (var point in daily)
        {
            tokensByDay[DateOnly.ParseExact(point.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture)] = point.TotalTokens;
        }

        var cells = new List<PosterHeatCell>(weeks * 7);
        var nonZero = new List<long>();
        for (var weekday = 0; weekday < 7; weekday++)
        {
            for (var week = 0; week < weeks; week++)
            {
                // 格子按星期行主序（先星期后周列），与 UniformGrid(Columns=weeks) 的行主序填充一致：
                // child i → (row = i / weeks, col = i % weeks)。修订六修正：此前按周优先生成，
                // 跨 ≥ 2 周时日子蛇形串位——星期行标签对不上真实日期、峰值描边落到错误的行。
                var day = startMonday.AddDays(week * 7 + weekday);
                if (day < rangeStart || day > rangeEnd)
                {
                    cells.Add(new PosterHeatCell(day, 0, 0, IsBlank: true, IsPeak: false));
                    continue;
                }

                var tokens = tokensByDay.GetValueOrDefault(day);
                if (tokens > 0)
                {
                    nonZero.Add(tokens);
                }

                cells.Add(new PosterHeatCell(day, tokens, 0, IsBlank: false, IsPeak: false));
            }
        }

        var sorted = nonZero.Order().ToArray();
        var peakTokens = sorted.Length > 0 ? sorted[^1] : 0;
        // 并列峰值取最早日期（与节律矩阵「取最早的 (星期, 小时)」同一口径；与格子顺序无关）
        var peakDay = peakTokens > 0
            ? cells.Where(c => !c.IsBlank && c.Tokens == peakTokens).Select(c => c.Day).Min()
            : default(DateOnly?);

        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i].IsBlank)
            {
                continue;
            }

            var level = LevelFor(cells[i].Tokens, sorted);
            cells[i] = cells[i] with { Level = level, IsPeak = peakDay == cells[i].Day };
        }

        return new SharePosterHeatGrid(
            weeks,
            startMonday,
            cells,
            BuildMonthLabels(rangeStart, rangeEnd, startMonday, weeks),
            WeekdayLabels(),
            peakDay,
            peakTokens);
    }

    /// <summary>
    /// 星期行标签：周一起 7 行，只标一/三/五（同 GitHub 的密度），文案取当前语言的星期缩写。
    /// 时段节律（样式 B）的星期列共用同一份（海报的星期口径全仓唯一出处）。
    /// </summary>
    public static IReadOnlyList<string?> WeekdayLabels()
    {
        var names = WeekdayNames();
        var labels = new string?[7];
        for (var weekday = 0; weekday < 7; weekday += 2)
        {
            labels[weekday] = names[weekday];
        }

        return labels;
    }

    /// <summary>周一起的完整星期缩写名（0 = 周一 .. 6 = 周日，当前语言）——峰值文案等需要全名的地方用。</summary>
    public static IReadOnlyList<string> WeekdayNames()
    {
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        return Enumerable.Range(0, 7)
            .Select(i => names[((int)DayOfWeek.Monday + i) % 7])
            .ToArray();
    }

    /// <summary>色阶档位：非零值按「窗口内严格小于它的活跃值个数」定秩（同值同档），秩均分到 1..4。</summary>
    public static int LevelFor(long tokens, IReadOnlyList<long> sortedNonZero)
    {
        if (tokens <= 0)
        {
            return 0;
        }

        // 单活跃日即窗口峰值，给最深档；其余按秩线性铺满 1..4（秩 0 → 1 档，秩 n-1 → 4 档）
        if (sortedNonZero.Count <= 1)
        {
            return 4;
        }

        var rank = 0;
        foreach (var value in sortedNonZero)
        {
            if (value >= tokens)
            {
                break;
            }

            rank++;
        }

        var fraction = (double)rank / (sortedNonZero.Count - 1);
        return Math.Clamp(1 + (int)(4 * fraction), 1, 4);
    }

    // ── 窄范围画布收口（修订 2026-11-07）：画布宽度随窗口周数收窄，格子按余量放大；
    // 修订六：窄/中范围脚注三格搬进卡片与日历并排——此前竖排脚注垫在下面，海报变成
    // 「窄而长」的纸条，且范围外透明格让格阵结构不可见、看着只有一片空白 ──

    /// <summary>少周数时格子放大的封顶（约为基准 <see cref="SharePosterView.HeatCellSize"/> 的 2 倍；再大 7 行就把海报撑成窄长条）。</summary>
    public const int MaxCellSize = 56;

    /// <summary>画布宽度下限：页头（logo + 字标 + 日期行）仍要摆得下的最窄海报。</summary>
    public const int MinCanvasWidth = 560;

    /// <summary>并排档脚注列的宽度预算（峰值文案「紧凑值 · 日期」在并排档用小一号的 24px 值）与列间距。</summary>
    public const int BesideStatsColumnWidth = 300;
    public const int BesideStatsColumnSpacing = 24;

    /// <summary>脚注与网格并排的最大周数：再长网格自己就占满画布，脚注回到底部横排（档位边界，非预算推导值）。</summary>
    public const int MaxBesideWeeks = 41;

    /// <summary>卡片自身的内边距占用（poster-panel 18×2；页边距之外的固定 chrome）。</summary>
    public const int CardChrome = 36;

    /// <summary>格子之外的画布占用：页边距 ×2 + 卡内边距。节律基底的画布宽也用它（同一 heat-root 基底）。</summary>
    public static double CanvasChromeFor(double margin) =>
        2 * Math.Clamp(margin, SharePosterRequest.MinMargin, SharePosterRequest.MaxMargin) + CardChrome;

    /// <summary>星期标签列 + 列间距（网格的固定左槽；节律基底同宽）。</summary>
    public const int WeekdayGutter = 52;

    private const int HeatGap = 4;

    /// <summary>并排档 / 底部横排档的周列预算（1800 减去各自脚注与 chrome 占用的余量）。</summary>
    private static int BesideColumnBudget(double margin) =>
        (int)(1800 - CanvasChromeFor(margin) - WeekdayGutter - BesideStatsColumnWidth - BesideStatsColumnSpacing);
    private static int BelowColumnBudget(double margin) =>
        (int)(1800 - CanvasChromeFor(margin) - WeekdayGutter);

    /// <summary>脚注三格是否搬进卡片与网格并排（窄/中范围；长范围网格占满 1800，脚注留在底部横排）。</summary>
    public static bool UseBesideStats(int weeks) => weeks <= MaxBesideWeeks;

    /// <summary>
    /// 每列槽宽（格 + 间隙）：按所在档位的预算收放——并排档封顶 60（格 56），长范围档在 52 周
    /// 收窄到基准 31（格 27，现状）。月份标签行与格子共用同一列宽，标签才对得上列。
    /// </summary>
    public static int ColumnWidthFor(int weeks, double margin = SharePosterRequest.DefaultMargin)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weeks);
        var budget = UseBesideStats(weeks) ? BesideColumnBudget(margin) : BelowColumnBudget(margin);
        return Math.Clamp(budget / weeks, SharePosterView.HeatCellSize + HeatGap, MaxCellSize + HeatGap);
    }

    /// <summary>
    /// 热力海报画布宽：网格 + 固定占用，并排档再加脚注列，夹在 <see cref="MinCanvasWidth"/> 与
    /// 1800 之间——宽度跟着内容与页边距走（边距越大画布越宽），高度被并排脚注压住。
    /// </summary>
    public static double CanvasWidthFor(int weeks, double margin = SharePosterRequest.DefaultMargin)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weeks);
        var grid = WeekdayGutter + weeks * ColumnWidthFor(weeks, margin);
        var beside = UseBesideStats(weeks) ? BesideStatsColumnWidth + BesideStatsColumnSpacing : 0;
        return Math.Clamp(grid + CanvasChromeFor(margin) + beside, MinCanvasWidth, SharePosterView.LandscapeWidth);
    }

    /// <summary>
    /// 格子悬停提示（预览活视图；导出 PNG 是静态位图不受影响）。文案口径与时间页
    /// <see cref="Controls.Heatmap"/> 的格子 tooltip 一致：日期 · 紧凑值 + 单位，无用量日为
    /// 「日期 · 无数据」；范围外留白格不给 tooltip（null → 视图不挂）。
    /// </summary>
    public static string? CellTooltip(PosterHeatCell cell)
    {
        if (cell.IsBlank)
        {
            return null;
        }

        var label = cell.Day.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        return cell.Tokens > 0
            ? label + " · " + TrendChart.FormatCompactTokens(cell.Tokens) + " " + Translations.Instance.ChartTipTokenUnit
            : string.Format(CultureInfo.CurrentCulture, Translations.Instance.HeatmapNoDataTooltip, label);
    }

    /// <summary>月份标签：标在「包含当月 1 号」的那一列（且 1 号在范围内），横跨到下一个标签列。</summary>
    private static IReadOnlyList<PosterHeatMonthLabel> BuildMonthLabels(
        DateOnly rangeStart, DateOnly rangeEnd, DateOnly startMonday, int weeks)
    {
        var labels = new List<PosterHeatMonthLabel>();
        var lastLabeledMonth = -1;
        for (var week = 0; week < weeks; week++)
        {
            var columnStart = startMonday.AddDays(week * 7);
            var columnEnd = columnStart.AddDays(6);
            // 该列里的当月 1 号：列首就是 1 号，或下个月 1 号落在本列内
            var firstOfColumnMonth = new DateOnly(columnStart.Year, columnStart.Month, 1);
            var firstOfNextMonth = firstOfColumnMonth.AddMonths(1);
            var monthStart = firstOfNextMonth <= columnEnd ? firstOfNextMonth
                : columnStart == firstOfColumnMonth ? firstOfColumnMonth
                : default(DateOnly?);

            // 年月序（Year*12+Month）去重：52 周窗口可跨两个日历年
            if (monthStart is not { } day || day < rangeStart || day > rangeEnd
                || day.Year * 12 + day.Month == lastLabeledMonth)
            {
                continue;
            }

            lastLabeledMonth = day.Year * 12 + day.Month;
            labels.Add(new PosterHeatMonthLabel(
                week, 1, day.ToString("MMM", CultureInfo.CurrentCulture)));
        }

        // 标签横跨到下一个有标签的列（月 ≥ 28 天，不会溢出），末尾跨到最后一列
        for (var i = 0; i < labels.Count; i++)
        {
            var nextStart = i + 1 < labels.Count ? labels[i + 1].StartCol : weeks;
            labels[i] = labels[i] with { Span = Math.Max(1, nextStart - labels[i].StartCol) };
        }

        return labels;
    }

    /// <summary>所在周的周一（列对齐固定周一起，不随区域设置——海报形态是确定的）。</summary>
    private static DateOnly Monday(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
