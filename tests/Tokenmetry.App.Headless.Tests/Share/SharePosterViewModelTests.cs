using System;
using System.Linq;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Headless.Tests.Controls;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Share;

/// <summary>
/// 项目名雾化（P6）：雾化是"可见段 + 模糊段"两段文本，不是把字符换成占位符——
/// 字符原样保留，行宽才和未雾化时一致，排行表左右列不会错位。
/// </summary>
public class SharePosterViewModelTests
{
    private static readonly string[] Projects = ["Zearth", "sven-net", "a"];

    private static SharePosterViewModel For(SharePosterProjectMask mask)
    {
        var rows = Projects
            .Select(key => new DimensionUsageRow
            {
                Key = key,
                TotalTokens = 100,
                Cost = new AggregatedCost(1m, false),
            })
            .ToArray();

        var data = SharePosterSnapshot.Build(
            new SharePosterRequest(
                SharePosterLayout.Portrait, SharePosterTheme.Dark, mask,
                new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2), SharePosterStyle.Overview),
            ChartTestHelpers.DailyPoints30(), [], [], rows, sessionCount: 0);

        return new SharePosterViewModel(data);
    }

    private static SharePosterRequest Request(SharePosterProjectMask mask = SharePosterProjectMask.Off) =>
        new(SharePosterLayout.Portrait, SharePosterTheme.Dark, mask,
            new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2), SharePosterStyle.Overview);

    [Fact]
    public void Type_segments_include_cache_writes_and_use_the_app_labels()
    {
        // 缓存段 = 读取 + 写入（与总览页同一条 CreateTokenTypeSegments）；图例文案走 Translations，
        // 不是 Core 里的英文键。三段之和 = 总 token。
        var daily = new[]
        {
            new DailyUsagePoint
            {
                Day = "2026-10-17",
                InputTokens = 100,
                OutputTokens = 50,
                CacheReadTokens = 800,
                CacheCreationTokens = 50,
                TotalTokens = 1_000,
                HasData = true,
            },
        };
        var data = SharePosterSnapshot.Build(Request(), daily, [], [], [], sessionCount: 1);
        var vm = new SharePosterViewModel(data);

        var segments = vm.TypeSegments.ToList();
        Assert.Equal(
            [
                Tokenmetry.App.Localization.Translations.Instance.TokenTypeInput,
                Tokenmetry.App.Localization.Translations.Instance.TokenTypeOutput,
                Tokenmetry.App.Localization.Translations.Instance.TokenTypeCache,
            ],
            segments.Select(s => s.Name));
        Assert.Equal(100, segments[0].Value);
        Assert.Equal(50, segments[1].Value);
        Assert.Equal(850, segments[2].Value);   // 800 读取 + 50 写入
        Assert.Equal(1_000, segments.Sum(s => s.Value));
    }

    [Fact]
    public void Agent_ring_is_top_six_plus_others_and_lets_the_control_pick_token_colors()
    {
        var agents = Enumerable.Range(1, 8)
            .Select(i => new DimensionUsageRow
            {
                Key = $"agent{i}",
                TotalTokens = (9 - i) * 1_000,
                Cost = AggregatedCost.Empty,
            })
            .ToArray();
        var data = SharePosterSnapshot.Build(Request(), [], agents, [], [], sessionCount: 0);
        var vm = new SharePosterViewModel(data);

        var segments = vm.AgentSegments.ToList();
        Assert.Equal(7, segments.Count);   // Top6 + 其他
        Assert.Equal(Tokenmetry.App.Localization.Translations.Instance.OthersSegment, segments[^1].Name);
        Assert.All(segments, s => Assert.Null(s.Color));   // 段色交给 AppSeries 令牌
    }

    [Fact]
    public void Mask_off_keeps_every_name_intact()
    {
        var vm = For(SharePosterProjectMask.Off);

        Assert.All(vm.ProjectRows, r =>
        {
            Assert.False(r.HasMaskedName);
            Assert.Equal(string.Empty, r.MaskedName);
        });
        Assert.Equal(Projects, vm.ProjectRows.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Partial_mask_keeps_the_first_characters_and_hides_the_rest()
    {
        var vm = For(SharePosterProjectMask.Partial);

        var zearth = vm.ProjectRows.Single(r => r.Name == "Zear");
        Assert.True(zearth.HasMaskedName);
        Assert.Equal("th", zearth.MaskedName);

        // 短名不比阈值长时整名保留（不把 1 个字符的项目变成全糊）
        var shortName = vm.ProjectRows.Single(r => r.Name == "a");
        Assert.False(shortName.HasMaskedName);
    }

    [Fact]
    public void Full_mask_hides_the_whole_name_but_keeps_its_text()
    {
        var vm = For(SharePosterProjectMask.Full);

        Assert.All(vm.ProjectRows, r =>
        {
            Assert.Equal(string.Empty, r.Name);
            Assert.True(r.HasMaskedName);
        });

        // 字符本身没有丢：模糊段拼起来就是原名（宽度由此保持一致）
        Assert.Contains("Zearth", vm.ProjectRows.Select(r => r.MaskedName));
    }

    [Fact]
    public void Trend_modes_switch_to_hourly_only_for_single_day_windows_with_hourly_data()
    {
        // 总览基底的形态选择（D-027 同源）：单日范围 + 小时分布 → 小时折线；
        // 小时数据缺位（查询失败回落）或多日范围 → 日序列，不画空小时图。
        var hourly = new HourlyDistributionResult
        {
            Hours = [new HourlyUsageBucket { Hour = 9, TotalTokens = 5, SessionCount = 1 }],
        };
        var day = new DateOnly(2026, 10, 17);
        SharePosterRequest Req(bool singleDay) => new(
            SharePosterLayout.Portrait, SharePosterTheme.Dark, SharePosterProjectMask.Off,
            day, singleDay ? day : day.AddDays(3), SharePosterStyle.Overview);

        var hourlySingle = new SharePosterViewModel(SharePosterSnapshot.Build(
            Req(singleDay: true), ChartTestHelpers.DailyPoints30(), [], [], [], sessionCount: 0,
            hourly: hourly, hourlyCutoffHour: 12));
        Assert.Equal(TrendChartMode.HourlyTokens, hourlySingle.TokenTrendMode);
        Assert.Equal(TrendChartMode.HourlyCost, hourlySingle.CostTrendMode);

        var fallback = new SharePosterViewModel(SharePosterSnapshot.Build(
            Req(singleDay: true), ChartTestHelpers.DailyPoints30(), [], [], [], sessionCount: 0));
        Assert.Equal(TrendChartMode.DailyTokens, fallback.TokenTrendMode);
        Assert.Equal(TrendChartMode.CostUsd, fallback.CostTrendMode);

        var multi = new SharePosterViewModel(SharePosterSnapshot.Build(
            Req(singleDay: false), ChartTestHelpers.DailyPoints30(), [], [], [], sessionCount: 0,
            hourly: hourly));
        Assert.Equal(TrendChartMode.DailyTokens, multi.TokenTrendMode);
        Assert.Equal(TrendChartMode.CostUsd, multi.CostTrendMode);
    }

    [Fact]
    public void Style_decides_the_base_and_range_decides_the_date_lines()
    {
        // 样式与范围解耦：多日范围也能是信息卡（IsTrayStyle），单日范围也能是总览；
        // 日期行跟着范围走——总览基底单日写「日期 · 周几」（不再有 start — end 同日的怪话），
        // 信息卡多日写范围。
        var day = new DateOnly(2026, 10, 17);
        var trayMulti = new SharePosterViewModel(SharePosterSnapshot.Build(
            new SharePosterRequest(SharePosterLayout.Portrait, SharePosterTheme.Dark,
                SharePosterProjectMask.Off, day, day.AddDays(3), SharePosterStyle.Tray),
            ChartTestHelpers.DailyPoints30(), [], [], [], sessionCount: 0));
        Assert.True(trayMulti.IsTrayStyle);
        Assert.False(trayMulti.IsSingleDay);
        Assert.Contains("—", trayMulti.TrayDateText);
        Assert.Contains("2026.10.17", trayMulti.TrayDateText);
        Assert.Contains("2026.10.20", trayMulti.TrayDateText);

        var overviewSingle = new SharePosterViewModel(SharePosterSnapshot.Build(
            new SharePosterRequest(SharePosterLayout.Portrait, SharePosterTheme.Dark,
                SharePosterProjectMask.Off, day, day, SharePosterStyle.Overview),
            ChartTestHelpers.DailyPoints30(), [], [], [], sessionCount: 0));
        Assert.False(overviewSingle.IsTrayStyle);
        Assert.True(overviewSingle.IsSingleDay);
        Assert.Equal(overviewSingle.SingleDayDateText, overviewSingle.RangeText);
        Assert.Equal(overviewSingle.SingleDayDateText, overviewSingle.TrayDateText);
    }
}
