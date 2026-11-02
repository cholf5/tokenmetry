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
