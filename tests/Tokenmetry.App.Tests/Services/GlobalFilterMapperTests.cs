using System;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;
using FluentAssertions;
using Xunit;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// 全局筛选 → 查询参数映射对账（时间窗口本地自然日含端点；D-020 起相对词表档由
/// RelativeRangeSpec 计算端点，today 显式注入钉死「今天」——本月/上月/昨天与用户预设的
/// 跨月/跨年边界不随运行日期漂移；自定义档契约：未选侧不设界限、倒置换序、跨度收口
/// 日序列物化上限——口径见 <see cref="GlobalFilterMapper"/>）。
/// </summary>
public class GlobalFilterMapperTests
{
    /// <summary>映射基准「今天」：钉死日期，断言与运行日无关。</summary>
    private static readonly DateOnly Today = new(2026, 10, 25);

    private static TimeRangeOption Range(string id) => TimeRangeOption.FindBuiltIn(id)!;

    private static UsageQueryFilter Build(TimeRangeOption range, DateTimeOffset? customFrom = null, DateTimeOffset? customTo = null) =>
        GlobalFilterMapper.Build(range, AgentOption.All, customFrom, customTo, model: null, project: null, Today);

    // ── 相对词表档（内置 = 预定义 Spec）──

    [Fact]
    public void Today_BoundsBothEndsToToday()
    {
        var filter = Build(Range("Today"));

        filter.FromDay.Should().Be("2026-10-25");
        filter.ToDay.Should().Be("2026-10-25");
    }

    [Fact]
    public void Yesterday_BoundsBothEndsToYesterday()
    {
        var filter = Build(Range("Yesterday"));

        filter.FromDay.Should().Be("2026-10-24");
        filter.ToDay.Should().Be("2026-10-24");
    }

    [Fact]
    public void Last7Days_BoundsRecentSevenDaysInclusive()
    {
        var filter = Build(Range("Last7Days"));

        filter.FromDay.Should().Be("2026-10-19");
        filter.ToDay.Should().Be("2026-10-25");
    }

    [Fact]
    public void ThisMonth_StartsOnFirstEndsToday()
    {
        var filter = Build(Range("ThisMonth"));

        filter.FromDay.Should().Be("2026-10-01");
        filter.ToDay.Should().Be("2026-10-25");
    }

    [Fact]
    public void LastMonth_CrossesYearBoundary()
    {
        // 钉在 1 月跑上月：上一年 12 月整月（跨年回归锁）
        var filter = GlobalFilterMapper.Build(
            TimeRangeOption.LastMonth, AgentOption.All, null, null, null, null, new DateOnly(2026, 1, 15));

        filter.FromDay.Should().Be("2025-12-01");
        filter.ToDay.Should().Be("2025-12-31");
    }

    [Fact]
    public void UserPreset_WeekZero_MapsMondayAnchoredWindow()
    {
        // 用户预设与内置档同形状同映射路径（2026-01-07 周三 → 本周 2026-01-05 起）
        var preset = new TimeRangeOption("preset:test", RelativeRangeSpec.Week(0));

        var filter = GlobalFilterMapper.Build(preset, AgentOption.All, null, null, null, null, new DateOnly(2026, 1, 7));

        filter.FromDay.Should().Be("2026-01-05");
        filter.ToDay.Should().Be("2026-01-07");
    }

    [Fact]
    public void All_LeavesBothBoundsUnset()
    {
        var filter = Build(Range("All"));

        filter.FromDay.Should().BeNull();
        filter.ToDay.Should().BeNull();
    }

    // ── 自定义档 ──

    [Fact]
    public void Custom_BothDates_MapByLocalDayInclusive()
    {
        // 带时刻输入按自然日收口（DatePicker 只产出日期语义，时刻不参与比较）
        var filter = Build(
            Range("Custom"),
            new DateTimeOffset(2026, 1, 5, 15, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero));

        filter.FromDay.Should().Be("2026-01-05");
        filter.ToDay.Should().Be("2026-01-12");
        filter.IsRangeValid.Should().BeTrue();
    }

    [Fact]
    public void Custom_ReversedDates_AreSwapped()
    {
        var filter = Build(
            Range("Custom"),
            new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));

        filter.FromDay.Should().Be("2026-01-05");
        filter.ToDay.Should().Be("2026-01-12");
    }

    [Fact]
    public void Custom_MissingEnd_LeavesUpperBoundUnset()
    {
        var filter = Build(
            Range("Custom"),
            new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero),
            customTo: null);

        filter.FromDay.Should().Be("2026-01-05");
        filter.ToDay.Should().BeNull();
    }

    [Fact]
    public void Custom_MissingStart_LeavesLowerBoundUnset()
    {
        var filter = Build(
            Range("Custom"),
            customFrom: null,
            new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero));

        filter.FromDay.Should().BeNull();
        filter.ToDay.Should().Be("2026-01-12");
    }

    [Fact]
    public void Custom_SpanBeyondDailySeriesCap_ClampsFromKeepingRecentWindow()
    {
        var to = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero);

        var filter = Build(
            Range("Custom"),
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
        var filter = Build(
            Range("Custom"),
            new DateTimeOffset(Today.AddDays(30).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            customTo: null);

        filter.FromDay.Should().Be("2026-10-25");
        filter.ToDay.Should().BeNull();
    }

    // ── 其余维度 ──

    [Fact]
    public void Agent_AllMeansUnfiltered()
    {
        var filter = Build(Range("Today"));

        filter.Agent.Should().BeNull();
    }

    [Fact]
    public void Build_with_all_sentinels_yields_null_model_and_project()
    {
        // 哨兵 All 的 Model/Path 为 null → 不过滤（映射不得把哨兵误传成空串过滤值）
        var filter = GlobalFilterMapper.Build(new FilterStateService());

        filter.Model.Should().BeNull();
        filter.Project.Should().BeNull();
    }
}
