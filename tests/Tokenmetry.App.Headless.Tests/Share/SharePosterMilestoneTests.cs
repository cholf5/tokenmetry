using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Share;

/// <summary>
/// 里程碑（样式 C）的叙事数字：全部取自快照已有数据——最猛的一天扫完整范围（不受热力图 52 周
/// 开窗限制）、最长连续从补零日序列数 HasData 连段、Top 模型取模型排行首行；无数据如实「—」。
/// </summary>
public class SharePosterMilestoneTests
{
    private static IReadOnlyList<DailyUsagePoint> Daily(params (int Offset, long Tokens)[] marks)
    {
        var start = new DateOnly(2026, 1, 1);
        var marksByOffset = marks.GroupBy(m => m.Offset).ToDictionary(g => g.Key, g => g.Sum(m => m.Tokens));
        return Enumerable.Range(0, 20)
            .Select(i => new DailyUsagePoint
            {
                Day = start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                TotalTokens = marksByOffset.GetValueOrDefault(i),
                HasData = marksByOffset.ContainsKey(i),
            })
            .ToArray();
    }

    private static SharePosterViewModel For(
        IReadOnlyList<DailyUsagePoint> daily,
        IReadOnlyList<DimensionUsageRow>? models = null,
        SharePosterStyle style = SharePosterStyle.Milestone)
    {
        var request = new SharePosterRequest(
            SharePosterLayout.Portrait, SharePosterTheme.Dark, SharePosterProjectMask.Off,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 20), style);
        var data = SharePosterSnapshot.Build(
            request, daily, [], models ?? [], [], sessionCount: 7);
        return new SharePosterViewModel(data);
    }

    [Fact]
    public void Streak_counts_the_longest_run_of_active_days()
    {
        // 1/1-1/5 连续 5 天 → 空档 → 1/7-1/14 连续 8 天 → 最长连续 8
        var vm = For(Daily((0, 100), (1, 100), (2, 100), (3, 100), (4, 100), (6, 100), (7, 100),
            (8, 100), (9, 100), (10, 100), (11, 100), (12, 100), (13, 100)));

        Assert.Contains("8", vm.MilestoneStreakText, StringComparison.Ordinal);
    }

    [Fact]
    public void Peak_scans_the_full_range_and_formats_date_and_compact_value()
    {
        var vm = For(Daily((2, 100), (15, 900), (17, 300)));

        // 最猛的一天 = 1/16（offset 15）：日期段固定 Invariant yyyy.MM.dd，值段是「/ 紧凑值」
        Assert.StartsWith("2026.01.16", vm.MilestonePeakDateText, StringComparison.Ordinal);
        Assert.EndsWith("/ 900", vm.MilestonePeakValueText, StringComparison.Ordinal);
        Assert.True(vm.HasMilestonePeakValue);
    }

    [Fact]
    public void Top_model_takes_the_first_ranking_row()
    {
        var vm = For(
            Daily((0, 100)),
            models:
            [
                new DimensionUsageRow { Key = "glm-5.3-flash", DisplayName = "glm-5.3-flash", TotalTokens = 4_500_000 },
                new DimensionUsageRow { Key = "gpt-5.3-codex", DisplayName = "gpt-5.3-codex", TotalTokens = 120 },
            ]);

        Assert.Equal("glm-5.3-flash", vm.MilestoneTopModelName);
        // 模型行 = 紧凑值 + 占比（{0} tokens · 占总量 {1} 形态，两段占位都在）
        Assert.Contains("450", vm.MilestoneTopModelCountText, StringComparison.Ordinal);
        Assert.Contains("100", vm.MilestoneTopModelCountText, StringComparison.Ordinal);
        Assert.NotNull(vm.MilestoneTopModelAvatar);
        Assert.True(vm.HasMilestoneModelBadge);
    }

    [Fact]
    public void Empty_range_reports_the_honest_placeholder()
    {
        var vm = For(Daily());

        Assert.Equal("—", vm.MilestonePeakDateText);
        Assert.False(vm.HasMilestonePeakValue);
        Assert.Equal("—", vm.MilestoneTopModelName);
        Assert.Equal("—", vm.MilestoneTopModelCountText);
        Assert.False(vm.HasMilestoneModelBadge);
        // 0 天是事实不是冒充（范围里确实没有连续使用的日子）
        Assert.Contains("0", vm.MilestoneStreakText, StringComparison.Ordinal);
    }

    [Fact]
    public void Streak_day_unit_comes_from_the_format_key_not_hardcoded()
    {
        // 占位符不变式由 TranslationsTests 全量覆盖；这里锁「数字与单位都来自 key」的拼装顺序
        var vm = For(Daily((0, 100), (1, 100)));

        Assert.StartsWith("2", vm.MilestoneStreakText, StringComparison.Ordinal);
    }
}
