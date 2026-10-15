using System;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;
using FluentAssertions;
using Xunit;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// 全局筛选 → 查询参数映射对账（时间窗口本地自然日含端点；新增 今日 / 自定义 档的映射契约：
/// 未选侧不设界限、倒置换序、跨度收口日序列物化上限——口径见 <see cref="GlobalFilterMapper"/>）。
/// </summary>
public class GlobalFilterMapperTests
{
    private static TimeRangeOption Range(TimeRange value) => new(value);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public void Today_BoundsBothEndsToToday()
    {
        var filter = GlobalFilterMapper.Build(Range(TimeRange.Today), AgentOption.All);

        filter.FromDay.Should().Be(UsageQueryFilter.FormatDay(Today));
        filter.ToDay.Should().Be(UsageQueryFilter.FormatDay(Today));
    }

    [Fact]
    public void Last7Days_BoundsRecentSevenDaysInclusive()
    {
        var filter = GlobalFilterMapper.Build(Range(TimeRange.Last7Days), AgentOption.All);

        filter.FromDay.Should().Be(UsageQueryFilter.FormatDay(Today.AddDays(-6)));
        filter.ToDay.Should().Be(UsageQueryFilter.FormatDay(Today));
    }

    [Fact]
    public void All_LeavesBothBoundsUnset()
    {
        var filter = GlobalFilterMapper.Build(Range(TimeRange.All), AgentOption.All);

        filter.FromDay.Should().BeNull();
        filter.ToDay.Should().BeNull();
    }

    [Fact]
    public void Custom_BothDates_MapByLocalDayInclusive()
    {
        // 带时刻输入按自然日收口（DatePicker 只产出日期语义，时刻不参与比较）
        var filter = GlobalFilterMapper.Build(
            Range(TimeRange.Custom),
            AgentOption.All,
            new DateTimeOffset(2026, 1, 5, 15, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero));

        filter.FromDay.Should().Be("2026-01-05");
        filter.ToDay.Should().Be("2026-01-12");
        filter.IsRangeValid.Should().BeTrue();
    }

    [Fact]
    public void Custom_ReversedDates_AreSwapped()
    {
        var filter = GlobalFilterMapper.Build(
            Range(TimeRange.Custom),
            AgentOption.All,
            new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));

        filter.FromDay.Should().Be("2026-01-05");
        filter.ToDay.Should().Be("2026-01-12");
    }

    [Fact]
    public void Custom_MissingEnd_LeavesUpperBoundUnset()
    {
        var filter = GlobalFilterMapper.Build(
            Range(TimeRange.Custom),
            AgentOption.All,
            new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero),
            customTo: null);

        filter.FromDay.Should().Be("2026-01-05");
        filter.ToDay.Should().BeNull();
    }

    [Fact]
    public void Custom_MissingStart_LeavesLowerBoundUnset()
    {
        var filter = GlobalFilterMapper.Build(
            Range(TimeRange.Custom),
            AgentOption.All,
            customFrom: null,
            new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero));

        filter.FromDay.Should().BeNull();
        filter.ToDay.Should().Be("2026-01-12");
    }

    [Fact]
    public void Custom_SpanBeyondDailySeriesCap_ClampsFromKeepingRecentWindow()
    {
        var to = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);

        var filter = GlobalFilterMapper.Build(
            Range(TimeRange.Custom),
            AgentOption.All,
            to.AddDays(-4000),
            to);

        var from = UsageQueryFilter.ParseDay(filter.FromDay)!.Value;
        var toDay = UsageQueryFilter.ParseDay(filter.ToDay)!.Value;
        (toDay.DayNumber - from.DayNumber + 1).Should().Be(UsageQueryFilter.MaxDailySeriesDays);
        toDay.Should().Be(new DateOnly(2026, 1, 12));
    }

    [Fact]
    public void Custom_FutureStartOnly_ClampsToLowerBoundToday()
    {
        // 只选晚于今天的起点：未选终点时把起点收口到今天，避免「未来起、无上界」的纯空窗
        var filter = GlobalFilterMapper.Build(
            Range(TimeRange.Custom),
            AgentOption.All,
            new DateTimeOffset(Today.AddDays(30).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            customTo: null);

        filter.FromDay.Should().Be(UsageQueryFilter.FormatDay(Today));
        filter.ToDay.Should().BeNull();
    }

    [Fact]
    public void Agent_AllMeansUnfiltered()
    {
        var filter = GlobalFilterMapper.Build(Range(TimeRange.Today), AgentOption.All);

        filter.Agent.Should().BeNull();
    }
}
