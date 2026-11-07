using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Tests.Models;

/// <summary>
/// 分享海报的数据快照（P2）：把总览页/信息窗的查询结果收成一份不可变、可安全跨线程渲染的切片。
/// 契约重点：未知成本保持未知（null ≠ 0）、日序列不补齐缺口、排行按 token 降序取前 N。
/// </summary>
public class SharePosterSnapshotTests
{
    private static readonly DateOnly SingleDay = new(2026, 10, 17);

    private static DimensionUsageRow Row(
        string key, long input, long output, long cache, decimal? cost, bool unknownCost = false) => new()
    {
        Key = key,
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cache,
        TotalTokens = input + output + cache,
        Cost = new AggregatedCost(cost, unknownCost),
    };

    private static SharePosterRequest Request(DateOnly? start = null, DateOnly? end = null) =>
        new(SharePosterLayout.Portrait, SharePosterTheme.Dark, SharePosterProjectMask.Off,
            start ?? new DateOnly(2026, 1, 28),
            end ?? new DateOnly(2026, 11, 2),
            SharePosterStyle.Overview);

    [Fact]
    public void Single_day_range_sets_the_flag()
    {
        var day = new DateOnly(2026, 10, 17);

        Assert.True(Request(day, day).IsSingleDay);
        Assert.False(Request().IsSingleDay);
    }

    [Fact]
    public void Totals_sum_the_agent_dimension_and_keep_unknown_cost_unknown()
    {
        var agents = new[]
        {
            Row("ZCode", 100, 10, 1_000, 2.5m),
            Row("Codex", 50, 5, 500, null, unknownCost: true),
        };

        var data = SharePosterSnapshot.Build(Request(), [], agents, [], [], sessionCount: 7);

        data.Totals.InputTokens.Should().Be(150);
        data.Totals.OutputTokens.Should().Be(15);
        data.Totals.CacheReadTokens.Should().Be(1_500);
        data.Totals.TotalTokens.Should().Be(1_665);
        data.Totals.SessionCount.Should().Be(7);

        // 未知成本绝不折算为 0：已知部分仍是 2.5，未知标记必须透传
        data.Totals.Cost.KnownCostUsd.Should().Be(2.5m);
        data.Totals.Cost.HasUnknownCost.Should().BeTrue();
    }

    [Fact]
    public void Cache_share_is_null_when_there_is_no_token_base()
    {
        var data = SharePosterSnapshot.Build(Request(), [], [], [], [], sessionCount: 0);

        data.Totals.TotalTokens.Should().Be(0);
        data.Totals.CacheShare.Should().BeNull("分母为 0 时不伪造 0%");
    }

    [Fact]
    public void Cache_share_is_cache_read_over_total()
    {
        var agents = new[] { Row("ZCode", 168_278, 38_021, 5_958_548, 0m) };

        var data = SharePosterSnapshot.Build(Request(), [], agents, [], [], sessionCount: 1);

        data.Totals.CacheShare.Should().BeApproximately(5_958_548m / 6_164_847m, 0.0001m);
    }

    [Fact]
    public void Rankings_sort_descending_and_truncate_to_top_six()
    {
        var models = Enumerable.Range(1, 8)
            .Select(i => Row($"m{i}", 0, 0, i * 100, null))
            .ToArray();

        var data = SharePosterSnapshot.Build(Request(), [], [], models, [], sessionCount: 0);

        data.Models.Should().HaveCount(SharePosterSnapshot.TopN);
        data.Models.Select(m => m.Name).Should().Equal("m8", "m7", "m6", "m5", "m4", "m3");
    }

    [Fact]
    public void Ranking_share_uses_its_own_dimension_total_as_denominator()
    {
        var projects = new[] { Row("alpha", 0, 0, 750, null), Row("beta", 0, 0, 250, null) };

        var data = SharePosterSnapshot.Build(Request(), [], [], [], projects, sessionCount: 0);

        data.Projects.Should().HaveCount(2);
        data.Projects[0].Share.Should().BeApproximately(0.75m, 0.0001m);
        data.Projects[1].Share.Should().BeApproximately(0.25m, 0.0001m);
    }

    [Fact]
    public void Project_rows_use_display_name_and_fall_back_to_key()
    {
        var projects = new[]
        {
            new DimensionUsageRow
            {
                Key = "/work/Zearth", DisplayName = "Zearth", TotalTokens = 10,
                Cost = AggregatedCost.Empty,
            },
            new DimensionUsageRow
            {
                Key = "/work/sven-net", DisplayName = null, TotalTokens = 5,
                Cost = AggregatedCost.Empty,
            },
        };

        var data = SharePosterSnapshot.Build(Request(), [], [], [], projects, sessionCount: 0);

        data.Projects.Select(p => p.Name).Should().Equal("Zearth", "/work/sven-net");
    }

    [Fact]
    public void Daily_series_is_carried_over_verbatim_without_gap_filling()
    {
        var daily = new[]
        {
            new DailyUsagePoint { Day = "2026-02-07", TotalTokens = 238, HasData = true },
            new DailyUsagePoint { Day = "2026-02-08", TotalTokens = 0, HasData = false },
        };

        var data = SharePosterSnapshot.Build(Request(), daily, [], [], [], sessionCount: 0);

        data.Daily.Should().HaveCount(2);
        data.Daily[1].HasData.Should().BeFalse("缺口按原样交给图表断开，不在快照层补 0");
    }

    [Fact]
    public void Kpi_aggregate_overrides_the_dimension_row_sums()
    {
        // 维度行会跳过空键、且按天只取 full 或本维度分解行——行合计可能小于 KPI。
        // 海报是给外部看的印刷品，总量必须以总览页同一份 KPI 为准（设计反目标 #1）。
        var agents = new[] { Row("ZCode", 100, 10, 1_000, 2.5m) };
        var kpi = new UsageKpiResult
        {
            TotalTokens = 9_999,
            CacheReadTokens = 8_888,
            Cost = new AggregatedCost(4m, false),
            SessionCount = 42,
            CacheReadShare = 0.88m,
        };

        var data = SharePosterSnapshot.Build(Request(), [], agents, [], [], sessionCount: 7, kpi);

        data.Totals.TotalTokens.Should().Be(9_999);
        data.Totals.CacheReadTokens.Should().Be(8_888);
        data.Totals.SessionCount.Should().Be(42);
        data.Totals.Cost.KnownCostUsd.Should().Be(4m);
        data.Totals.CacheShare.Should().Be(0.88m);
    }

    [Fact]
    public void Agent_rankings_are_not_truncated_so_the_ring_can_aggregate_the_rest()
    {
        var agents = Enumerable.Range(1, 9)
            .Select(i => Row($"a{i}", 0, 0, i * 10, null))
            .ToArray();

        var data = SharePosterSnapshot.Build(Request(), [], agents, [], [], sessionCount: 0);

        // 表格维度取 TopN，Agent 维度留全量（环形自己算 Top6 + 其他）
        data.Agents.Should().HaveCount(9);
        data.Models.Should().BeEmpty();
        data.Projects.Should().BeEmpty();
    }

    [Fact]
    public void Hourly_distribution_and_cutoff_are_carried_over_verbatim()
    {
        // 小时形态的数据随快照走（总览样式的单日海报切小时形态用）；快照不加工、不补桶。
        var hourly = new HourlyDistributionResult
        {
            Hours = [new HourlyUsageBucket { Hour = 9, TotalTokens = 123, SessionCount = 2 }],
            UnknownSessionCount = 1,
            UnknownTotalTokens = 10,
        };

        var data = SharePosterSnapshot.Build(Request(SingleDay, SingleDay), [], [], [], [], sessionCount: 0,
            hourly: hourly, hourlyCutoffHour: 14);

        data.Hourly.Should().BeSameAs(hourly);
        data.HourlyCutoffHour.Should().Be(14);
    }

    [Fact]
    public void Hourly_data_defaults_to_null_for_multi_day_windows()
    {
        var data = SharePosterSnapshot.Build(Request(), [], [], [], [], sessionCount: 0);

        data.Hourly.Should().BeNull();
        data.HourlyCutoffHour.Should().BeNull();
    }

    [Fact]
    public void Weekly_rhythm_is_carried_over_verbatim()
    {
        // 时段节律矩阵随快照走（样式与范围解耦后任何范围都可能切到它）；快照不加工、不补格。
        var rhythm = new WeeklyRhythmResult
        {
            Cells = [new WeeklyRhythmCell { Weekday = 1, Hour = 9, SessionCount = 2, TotalTokens = 123 }],
            UnknownSessionCount = 1,
            UnknownTotalTokens = 10,
        };

        var data = SharePosterSnapshot.Build(Request(), [], [], [], [], sessionCount: 0, weeklyRhythm: rhythm);

        data.WeeklyRhythm.Should().BeSameAs(rhythm);
    }

    [Fact]
    public void Weekly_rhythm_defaults_to_null()
    {
        var data = SharePosterSnapshot.Build(Request(), [], [], [], [], sessionCount: 0);

        data.WeeklyRhythm.Should().BeNull();
    }
}
