using System;
using System.Collections.Generic;
using System.Linq;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Tests.ViewModels;

/// <summary>
/// 分享海报的选项与范围解析（P5b/P6）：三组选项的值 ↔ 枚举映射、未知值回落，以及
/// 「海报时间范围」在无界筛选下的退让规则（文件名与单日判定都要一对具体日期）。
/// 断言不涉及任何界面文案（语言无关纪律）。
/// </summary>
public class SharePosterOptionsTests
{
    [Fact]
    public void Option_lists_cover_every_enum_value_exactly_once()
    {
        SharePosterOptions.Layouts.Select(o => SharePosterOptions.ParseLayout(o.Value))
            .Should().BeEquivalentTo(Enum.GetValues<SharePosterLayout>());
        SharePosterOptions.Themes.Select(o => SharePosterOptions.ParseTheme(o.Value))
            .Should().BeEquivalentTo(Enum.GetValues<SharePosterTheme>());
        SharePosterOptions.Masks.Select(o => SharePosterOptions.ParseMask(o.Value))
            .Should().BeEquivalentTo(Enum.GetValues<SharePosterProjectMask>());

        // 选中项必须是同一批实例（ComboBox 按引用比较 SelectedItem）
        SharePosterOptions.ForLayout(SharePosterLayout.Landscape).Should().BeSameAs(SharePosterOptions.Layouts[1]);
        SharePosterOptions.ForTheme(SharePosterTheme.Light).Should().BeSameAs(SharePosterOptions.Themes[2]);
        SharePosterOptions.ForMask(SharePosterProjectMask.Full).Should().BeSameAs(SharePosterOptions.Masks[2]);
    }

    [Theory]
    [InlineData("portrait")]
    [InlineData("landscape")]
    [InlineData("  LANDSCAPE ")]
    public void Layout_keys_round_trip(string key)
    {
        var layout = SharePosterOptions.ParseLayout(key);
        SharePosterOptions.ParseLayout(SharePosterOptions.KeyOf(layout)).Should().Be(layout);
    }

    [Theory]
    [InlineData(null, SharePosterLayout.Portrait)]
    [InlineData("", SharePosterLayout.Portrait)]
    [InlineData("diagonal", SharePosterLayout.Portrait)]
    public void Unknown_layout_falls_back_to_portrait(string? value, SharePosterLayout expected) =>
        SharePosterOptions.ParseLayout(value).Should().Be(expected);

    [Theory]
    [InlineData(null, SharePosterTheme.FollowApp)]
    [InlineData("sepia", SharePosterTheme.FollowApp)]
    [InlineData("app", SharePosterTheme.FollowApp)]
    [InlineData("dark", SharePosterTheme.Dark)]
    [InlineData("light", SharePosterTheme.Light)]
    public void Theme_keys_parse_with_follow_app_fallback(string? value, SharePosterTheme expected) =>
        SharePosterOptions.ParseTheme(value).Should().Be(expected);

    [Theory]
    [InlineData(null, SharePosterProjectMask.Off)]
    [InlineData("mosaic", SharePosterProjectMask.Off)]
    [InlineData("partial", SharePosterProjectMask.Partial)]
    [InlineData("full", SharePosterProjectMask.Full)]
    public void Mask_keys_parse_with_off_fallback(string? value, SharePosterProjectMask expected) =>
        SharePosterOptions.ParseMask(value).Should().Be(expected);

    [Fact]
    public void Poster_range_prefers_the_filter_bounds()
    {
        var filter = new UsageQueryFilter { FromDay = "2026-10-01", ToDay = "2026-10-17" };

        var (start, end) = MainWindowViewModel.ResolvePosterRange(filter, Daily(), new DateOnly(2026, 11, 2));

        start.Should().Be(new DateOnly(2026, 10, 1));
        end.Should().Be(new DateOnly(2026, 10, 17));
    }

    [Fact]
    public void Poster_range_falls_back_to_the_first_and_last_day_with_data()
    {
        // 「全部时间」= 双端无界：范围退到日序列里真实有数据的首末两天，不谎报今天
        var filter = new UsageQueryFilter();

        var (start, end) = MainWindowViewModel.ResolvePosterRange(filter, Daily(), new DateOnly(2026, 11, 2));

        start.Should().Be(new DateOnly(2026, 9, 20));
        end.Should().Be(new DateOnly(2026, 10, 3));
    }

    [Fact]
    public void Poster_range_defaults_to_today_when_there_is_no_data()
    {
        var filter = new UsageQueryFilter();
        var today = new DateOnly(2026, 11, 2);

        var (start, end) = MainWindowViewModel.ResolvePosterRange(filter, [], today);

        start.Should().Be(today);
        end.Should().Be(today);
    }

    [Fact]
    public void Poster_range_does_not_fabricate_a_window_from_a_single_bound()
    {
        // 只选了起点（无终点）而库里数据全在起点之前：不能换序造出一个"用户没选过、中间也没数据"的区间，
        // 收口成单日，让海报如实显示这个范围。
        var filter = new UsageQueryFilter { FromDay = "2026-10-20" };

        var (start, end) = MainWindowViewModel.ResolvePosterRange(filter, Daily(), new DateOnly(2026, 11, 2));

        start.Should().Be(new DateOnly(2026, 10, 20));
        end.Should().Be(start);

        // 只选了终点：起点退到数据首日（数据早于终点，正常成窗口）
        var toOnly = new UsageQueryFilter { ToDay = "2026-10-02" };
        var (s2, e2) = MainWindowViewModel.ResolvePosterRange(toOnly, Daily(), new DateOnly(2026, 11, 2));
        s2.Should().Be(new DateOnly(2026, 9, 20));
        e2.Should().Be(new DateOnly(2026, 10, 2));
    }

    /// <summary>三天：首尾有数据、中间补零（补零日不能当范围端点）。</summary>
    private static IReadOnlyList<DailyUsagePoint> Daily() =>
    [
        new() { Day = "2026-09-20", TotalTokens = 100, HasData = true },
        new() { Day = "2026-09-21", TotalTokens = 0, HasData = false },
        new() { Day = "2026-10-03", TotalTokens = 200, HasData = true },
    ];
}
