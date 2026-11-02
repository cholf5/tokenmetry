using System;
using System.Linq;
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
                new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2)),
            ChartTestHelpers.DailyPoints30(), [], [], rows, sessionCount: 0);

        return new SharePosterViewModel(data);
    }

    private static SharePosterRequest Request(SharePosterProjectMask mask = SharePosterProjectMask.Off) =>
        new(SharePosterLayout.Portrait, SharePosterTheme.Dark, mask,
            new DateOnly(2026, 1, 28), new DateOnly(2026, 11, 2));

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
}
