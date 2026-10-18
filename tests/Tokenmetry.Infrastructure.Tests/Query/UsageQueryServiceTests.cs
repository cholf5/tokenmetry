namespace Tokenmetry.Infrastructure.Tests.Query;

using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Cache;

/// <summary>
/// I-028 / I-029 UI 聚合查询服务的 fixture 测试：已知逐日逐模型 facts + 缺价模型 + 纪元/日锚定
/// 时间戳的会话源，聚合结果与手写 SQL 对照（TC-001），并覆盖未知成本 null ≠ 0 契约、日历补零、
/// 环比窗口、维度归一化歧义守卫、会话列表与小时分布 unknown 口径。
/// 时区固定 UTC（服务注入 TimeZoneInfo.Utc），会话日期断言不受本机时区影响。
/// fixture 会话（s0..s9）的 LastSeenAt = FirstSeenAt + 30 分钟。
/// </summary>
public class UsageQueryServiceTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteUsageStore _store = null!;
    private UsageQueryService _service = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ccusage-query-{Guid.NewGuid():N}.db");
        _store = new SqliteUsageStore(_dbPath);
        await _store.InitializeAsync();
        _service = new UsageQueryService(_store, TimeZoneInfo.Utc);
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        if (File.Exists(_dbPath))
        {
            // A-1（Windows）：连接池默认开启（Pooling=True），池化连接会一直持有 .db 句柄，
            // 重试删除救不回来——先清池再删，POSIX 上无副作用。
            SqliteConnection.ClearAllPools();
            // SQLite may keep the file open for a short time. Retry a few times.
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    File.Delete(_dbPath);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    // ---- I-028: 日序列 ----

    [Fact]
    public async Task Daily_series_bounded_range_zero_fills_and_flags_empty_days()
    {
        var points = await _service.GetDailySeriesAsync(new UsageQueryFilter { FromDay = "2026-03-10", ToDay = "2026-03-16" });

        points.Should().HaveCount(7);
        points.Select(point => point.Day).Should().Equal(
            "2026-03-10", "2026-03-11", "2026-03-12", "2026-03-13", "2026-03-14", "2026-03-15", "2026-03-16");

        // 数据日
        var march10 = points[0];
        march10.HasData.Should().BeTrue();
        march10.InputTokens.Should().Be(3100);
        march10.OutputTokens.Should().Be(1550);
        march10.CacheCreationTokens.Should().Be(100);
        march10.CacheReadTokens.Should().Be(2000);
        march10.TotalTokens.Should().Be(6750);
        march10.Cost.KnownCostUsd.Should().Be(3.50m);
        march10.Cost.HasUnknownCost.Should().BeTrue("缺价模型的行成本为 null，日点必须暴露未知标记而非折 0");

        // 补零空日：token 为 0、成本为 Empty（非 0 也非未知）、HasData=false 供图层辨识
        var march12 = points[2];
        march12.HasData.Should().BeFalse();
        march12.TotalTokens.Should().Be(0);
        march12.Cost.KnownCostUsd.Should().BeNull();
        march12.Cost.HasUnknownCost.Should().BeFalse();
        march12.Cost.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Daily_series_unbounded_range_returns_actual_days_only()
    {
        var points = await _service.GetDailySeriesAsync(new UsageQueryFilter());

        points.Select(point => point.Day).Should().Equal(
            "2026-03-08", "2026-03-10", "2026-03-11", "2026-03-14", "2026-03-15", "2026-03-17", "2026-03-20");
        points.All(point => point.HasData).Should().BeTrue();
    }

    [Fact]
    public async Task Daily_series_matches_handwritten_sql_with_full_grain_precedence()
    {
        // TC-001：手写 SQL（full 粒度优先，无 full 日的分解日退回 day 粒度行）与日序列逐日对照。
        var points = await _service.GetDailySeriesAsync(new UsageQueryFilter { FromDay = "2026-03-08", ToDay = "2026-03-16" });

        const string sql = """
            SELECT day,
                   SUM(input_tokens), SUM(output_tokens), SUM(cache_creation_tokens),
                   SUM(cache_read_tokens), SUM(total_tokens),
                   SUM(cost_usd), SUM(cost_usd IS NULL)
            FROM usage_facts u
            WHERE day >= '2026-03-08' AND day <= '2026-03-16'
              AND (grain = 'full'
                   OR (grain = 'day' AND NOT EXISTS (
                       SELECT 1 FROM usage_facts f2 WHERE f2.day = u.day AND f2.grain = 'full')))
            GROUP BY day
            ORDER BY day
            """;
        var sqlRows = await QueryAsync(sql, reader => new
        {
            Day = reader.GetString(0),
            Input = reader.GetInt64(1),
            Output = reader.GetInt64(2),
            CacheCreation = reader.GetInt64(3),
            CacheRead = reader.GetInt64(4),
            Total = reader.GetInt64(5),
            KnownCost = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6),
            UnknownRows = reader.GetInt64(7),
        });

        // SQL 只返回有聚合行的数据日；与日序列中有数据的日子对照（补零日无 SQL 行）。
        sqlRows.Select(row => row.Day).Should().Equal(points.Where(point => point.HasData).Select(point => point.Day));
        foreach (var (sqlRow, point) in sqlRows.Zip(points.Where(point => point.HasData)))
        {
            point.HasData.Should().BeTrue();
            point.InputTokens.Should().Be(sqlRow.Input, point.Day);
            point.OutputTokens.Should().Be(sqlRow.Output, point.Day);
            point.CacheCreationTokens.Should().Be(sqlRow.CacheCreation, point.Day);
            point.CacheReadTokens.Should().Be(sqlRow.CacheRead, point.Day);
            point.TotalTokens.Should().Be(sqlRow.Total, point.Day);
            point.Cost.KnownCostUsd.Should().BeApproximately(sqlRow.KnownCost ?? 0m, 1e-9m, point.Day);
            point.Cost.HasUnknownCost.Should().Be(sqlRow.UnknownRows > 0, point.Day);
        }
    }

    [Fact]
    public async Task Daily_series_agent_filter_passes_through()
    {
        var points = await _service.GetDailySeriesAsync(
            new UsageQueryFilter { FromDay = "2026-03-10", ToDay = "2026-03-11", Agent = "Claude Code" });

        points.Should().HaveCount(2);
        points[0].TotalTokens.Should().Be(3750);
        points[0].Cost.KnownCostUsd.Should().Be(1.50m);
        points[0].Cost.HasUnknownCost.Should().BeTrue();
        points[1].TotalTokens.Should().Be(1750);
        points[1].Cost.HasUnknownCost.Should().BeFalse();
    }

    [Fact]
    public async Task Daily_series_decomposition_day_falls_back_to_day_grain_row()
    {
        // CLI 分解日（无 full 行）：日合计取 day 粒度行（与 ToDailySummaries 口径一致）。
        var points = await _service.GetDailySeriesAsync(new UsageQueryFilter { FromDay = "2026-03-19", ToDay = "2026-03-21" });

        points.Should().HaveCount(3);
        var march20 = points[1];
        march20.Day.Should().Be("2026-03-20");
        march20.HasData.Should().BeTrue();
        march20.InputTokens.Should().Be(700);
        march20.OutputTokens.Should().Be(300);
        march20.TotalTokens.Should().Be(1000);
        march20.Cost.KnownCostUsd.Should().Be(0.90m);
        march20.Cost.HasUnknownCost.Should().BeFalse();
    }

    [Fact]
    public async Task Daily_series_agent_filter_on_decomposition_day_yields_flagged_zero_point()
    {
        // CLI 分解行在 Agent 过滤下：day 粒度行（agent 为空）被过滤，agent 粒度行无 token 拆分——
        // 日点保持 HasData=true（有 facts）但日合计为 0（不折算 agent 粒度行，避免与维度聚合重复计数）。
        var points = await _service.GetDailySeriesAsync(
            new UsageQueryFilter { FromDay = "2026-03-19", ToDay = "2026-03-21", Agent = "Codex" });

        var march20 = points.Single(point => point.Day == "2026-03-20");
        march20.HasData.Should().BeTrue();
        march20.TotalTokens.Should().Be(0);
        march20.Cost.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Daily_series_invalid_range_throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetDailySeriesAsync(
            new UsageQueryFilter { FromDay = "2026-03-10", ToDay = "2026-03-01" }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetDailySeriesAsync(
            new UsageQueryFilter { FromDay = "20260310" }));
    }

    // ---- I-028: KPI 与环比 ----

    [Fact]
    public async Task Kpi_matches_handwritten_sql_and_exposes_unknown_cost()
    {
        var kpi = await _service.GetKpiAsync(new UsageQueryFilter { FromDay = "2026-03-10", ToDay = "2026-03-16" });

        const string usageSql = """
            SELECT COALESCE(SUM(total_tokens), 0), COALESCE(SUM(cache_read_tokens), 0),
                   SUM(cost_usd), SUM(cost_usd IS NULL)
            FROM usage_facts u
            WHERE day >= '2026-03-10' AND day <= '2026-03-16'
              AND (grain = 'full'
                   OR (grain = 'day' AND NOT EXISTS (
                       SELECT 1 FROM usage_facts f2 WHERE f2.day = u.day AND f2.grain = 'full')))
            """;
        const string sessionSql = """
            SELECT COUNT(*) FROM session_facts
            WHERE substr(first_seen_at, 1, 10) >= '2026-03-10'
              AND substr(first_seen_at, 1, 10) <= '2026-03-16'
            """;
        var usage = (await QueryAsync(usageSql, reader => new
        {
            Total = reader.GetInt64(0),
            CacheRead = reader.GetInt64(1),
            KnownCost = reader.IsDBNull(2) ? (decimal?)null : reader.GetDecimal(2),
            UnknownRows = reader.GetInt64(3),
        })).Single();
        var sessionCount = (await QueryAsync(sessionSql, reader => reader.GetInt64(0))).Single();

        kpi.TotalTokens.Should().Be(usage.Total).And.Be(9630);
        kpi.CacheReadTokens.Should().Be(usage.CacheRead);
        kpi.Cost.KnownCostUsd.Should().BeApproximately(usage.KnownCost ?? 0m, 1e-9m);
        kpi.Cost.HasUnknownCost.Should().Be(usage.UnknownRows > 0).And.BeTrue();
        kpi.SessionCount.Should().Be(sessionCount).And.Be(8);
        kpi.CacheReadShare.Should().Be(Math.Round(3000m / 9630m, 4));
    }

    [Fact]
    public async Task Kpi_previous_period_is_equal_length_lookback_with_direction_and_percent()
    {
        // 当前窗口 [03-10, 03-11]（2 天）→ 上一周期 = 等长回看 [03-08, 03-09]。
        var kpi = await _service.GetKpiAsync(new UsageQueryFilter { FromDay = "2026-03-10", ToDay = "2026-03-11" });

        var previous = kpi.PreviousPeriod;
        previous.Should().NotBeNull();
        previous!.FromDay.Should().Be("2026-03-08");
        previous.ToDay.Should().Be("2026-03-09");
        previous.TotalTokens.Should().Be(150);
        previous.SessionCount.Should().Be(1);
        previous.Cost.KnownCostUsd.Should().Be(1.00m);
        previous.CacheReadShare.Should().Be(0m, "上一周期没有缓存读取，占比为已知的 0 而非 null");

        kpi.TotalTokens.Should().Be(8700);
        kpi.SessionCount.Should().Be(6, "含日锚定 00:00 会话（s8 在 03-10 起始），不含纪元会话（1970 在范围外）");
        kpi.Cost.KnownCostUsd.Should().Be(4.25m);

        previous.TotalTokensChange.Direction.Should().Be(ChangeDirection.Increase);
        previous.TotalTokensChange.PercentChange.Should().Be(5700.00m);
        previous.CostChange.Direction.Should().Be(ChangeDirection.Increase);
        previous.CostChange.PercentChange.Should().Be(325.00m);
        previous.SessionCountChange.Direction.Should().Be(ChangeDirection.Increase);
        previous.SessionCountChange.PercentChange.Should().Be(500.00m);
        // 占比环比：基线为 0 → 百分比不可定义（null），方向仍然给出。
        previous.CacheReadShareChange.Direction.Should().Be(ChangeDirection.Increase);
        previous.CacheReadShareChange.PercentChange.Should().BeNull();
    }

    [Fact]
    public async Task Kpi_unbounded_range_has_no_previous_period()
    {
        var kpi = await _service.GetKpiAsync(new UsageQueryFilter());
        kpi.PreviousPeriod.Should().BeNull();

        var halfBounded = await _service.GetKpiAsync(new UsageQueryFilter { FromDay = "2026-03-10" });
        halfBounded.PreviousPeriod.Should().BeNull("单端有界无法定义等长回看窗口");
    }

    [Fact]
    public async Task Kpi_on_empty_store_reports_zeroes_without_fabricating_cost_or_share()
    {
        var emptyDb = Path.Combine(Path.GetTempPath(), $"ccusage-query-empty-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteUsageStore(emptyDb);
            await store.InitializeAsync();
            var service = new UsageQueryService(store, TimeZoneInfo.Utc);

            var kpi = await service.GetKpiAsync(new UsageQueryFilter { FromDay = "2026-06-01", ToDay = "2026-06-07" });

            kpi.TotalTokens.Should().Be(0);
            kpi.SessionCount.Should().Be(0);
            kpi.CacheReadShare.Should().BeNull("总 token 为 0 时无基数，占比为 null 而非伪造的 0");
            kpi.Cost.IsEmpty.Should().BeTrue();
            kpi.PreviousPeriod.Should().NotBeNull();
            kpi.PreviousPeriod!.TotalTokens.Should().Be(0);
            kpi.PreviousPeriod.TotalTokensChange.Direction.Should().Be(ChangeDirection.Neutral);
            kpi.PreviousPeriod.CostChange.Direction.Should().Be(ChangeDirection.Neutral, "上一周期无已知成本（null）时不臆造方向");
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // A-1（Windows）：清池释放 .db 句柄后再删
            File.Delete(emptyDb);
        }
    }

    [Fact]
    public async Task Cost_contract_distinguishes_unknown_null_and_known_zero()
    {
        // 纪律红线：null（全未知）≠ 0（已知为零）≠ Empty（无数据），三种形态在接口层可辨。
        var storePath = Path.Combine(Path.GetTempPath(), $"ccusage-cost-{Guid.NewGuid():N}.db");
        var store = new SqliteUsageStore(storePath);
        await store.InitializeAsync();
        try
        {
            await store.UpsertUsageFactsAsync(
            [
                new UsageFact
                {
                    Day = "2026-04-01",
                    Grain = UsageFactGrain.Full,
                    Agent = "Qwen Code",
                    Model = "unknown-model",
                    Project = "/p",
                    TotalTokens = 100,
                    CostUsd = null,
                },
                new UsageFact
                {
                    Day = "2026-04-02",
                    Grain = UsageFactGrain.Full,
                    Agent = "ZCode",
                    Model = "glm-x",
                    Project = "ZCode",
                    TotalTokens = 100,
                    CostUsd = 0m,
                },
            ]);
            var service = new UsageQueryService(store, TimeZoneInfo.Utc);

            var allUnknown = await service.GetDailySeriesAsync(new UsageQueryFilter { FromDay = "2026-04-01", ToDay = "2026-04-01" });
            var allKnownZero = await service.GetDailySeriesAsync(new UsageQueryFilter { FromDay = "2026-04-02", ToDay = "2026-04-02" });

            var unknownCost = allUnknown.Single().Cost;
            unknownCost.KnownCostUsd.Should().BeNull("全未知日的已知成本为 null");
            unknownCost.HasUnknownCost.Should().BeTrue();
            unknownCost.IsEmpty.Should().BeFalse();

            var zeroCost = allKnownZero.Single().Cost;
            zeroCost.KnownCostUsd.Should().Be(0m, "已知为零是 0，不是 null");
            zeroCost.HasUnknownCost.Should().BeFalse();
            zeroCost.IsEmpty.Should().BeFalse();

            var kpi = await service.GetKpiAsync(new UsageQueryFilter { FromDay = "2026-04-01", ToDay = "2026-04-02" });
            kpi.Cost.KnownCostUsd.Should().Be(0m, "已知 0 与未知相加仍是 0（未知不折 0，也不吞掉已知 0）");
            kpi.Cost.HasUnknownCost.Should().BeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // A-1（Windows）：清池释放 .db 句柄后再删
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }
    }

    // ---- I-029: 维度聚合 ----

    [Fact]
    public async Task Agent_breakdown_matches_sql_with_session_counts_and_shares()
    {
        var rows = await _service.GetAgentBreakdownAsync(new UsageQueryFilter());
        var expected = await QueryDimensionExpectationAsync("agent", UsageFactGrain.Agent, "agent");

        rows.Select(row => row.Key).Should().Equal(expected.Select(row => row.Key));
        AssertDimensionRowsMatch(rows, expected, "agent");
        rows.Should().OnlyContain(row => row.DisplayName == null, "Agent 维度 Key 即展示名");
        rows.Should().OnlyContain(row => row.DominantAgent == null && row.AgentCount == null,
            "D-010 关联 Agent 列仅模型维度填充，Agent 维度恒为 null");

        // 分解日的 agent 粒度行（03-20 Codex）计入 Codex 维度；只含会话的 agent 行 token 为 0。
        rows.Single(row => row.Key == "Claude Code").TotalTokens.Should().Be(6440);
        rows.Single(row => row.Key == "Codex").TotalTokens.Should().Be(4350);
        rows.Single(row => row.Key == "Gemini CLI").TotalTokens.Should().Be(0);
        var scopeTotal = rows.Sum(row => row.TotalTokens);
        rows.Single(row => row.Key == "Claude Code").TokenShare.Should().Be(Math.Round(6440m / scopeTotal, 4));
        rows.Single(row => row.Key == "Codex").CostShare.Should().BeNull(
            "Codex 含未知成本行（03-11 gpt-x），不参与已知成本占比（契约）");
    }

    [Fact]
    public async Task Model_breakdown_matches_sql_and_keeps_session_only_models()
    {
        var rows = await _service.GetModelBreakdownAsync(new UsageQueryFilter());
        var expected = await QueryDimensionExpectationAsync("model", UsageFactGrain.Model, "dominant_model");

        rows.Select(row => row.Key).Should().Equal(expected.Select(row => row.Key));
        AssertDimensionRowsMatch(rows, expected, "model");

        rows.Select(row => row.Key).Should().Equal(
            "claude-sonnet-4", "gpt-x", "claude-unpriced-model", "gemini-x", "qwen-x");

        rows[0].TotalTokens.Should().Be(6680, "full 行 6280 + 分解日 model 粒度行 400");
        rows[0].SessionCount.Should().Be(4);
        rows[0].Cost.KnownCostUsd.Should().Be(3.06m);
        rows[0].CostShare.Should().Be(Math.Round(3.06m / 6.60m, 4), "全已知成本的行参与已知成本占比（分母 3.06 + 3.54）");
        rows[1].TotalTokens.Should().Be(3950);
        rows[1].Cost.HasUnknownCost.Should().BeTrue();
        rows[2].TotalTokens.Should().Be(160, "缺价模型含 03-17 大小写变体行（150 + 10）");
        rows[2].Cost.KnownCostUsd.Should().BeNull("缺价模型成本为 null");
        rows[2].Cost.HasUnknownCost.Should().BeTrue();
        rows[2].CostShare.Should().BeNull("含未知成本的行不参与已知成本占比");
        rows[3].Key.Should().Be("gemini-x");
        rows[3].TotalTokens.Should().Be(0, "只有会话、无 facts 行的模型仍成行（键并集）");
        rows[3].SessionCount.Should().Be(1);
        rows[3].Cost.IsEmpty.Should().BeTrue();
        rows.Single(row => row.Key == "qwen-x").SessionCount.Should().Be(1);

        // D-010 关联 Agent 派生（共享 fixture 每个模型只被一个 Agent 使用）：
        rows.Should().OnlyContain(row => row.AgentCount == 1, "共享 fixture 各模型均为单一 Agent 会话");
        rows.Single(row => row.Key == "claude-sonnet-4").DominantAgent.Should().Be("Claude Code");
        rows.Single(row => row.Key == "gpt-x").DominantAgent.Should().Be("Codex");
    }

    [Fact]
    public async Task Model_breakdown_derives_dominant_agent_from_session_facts()
    {
        // D-010 契约演进对账：DominantAgent / AgentCount 与 SessionCount 同源（同批 session_facts、
        // 同一个 dominant_model 分组按 Agent 计数）。主用 Agent = 会话数最多者（并列取键序数最小，
        // 与会话 token 量无关）；只有 facts 行没有会话的模型诚实为 null，不猜。
        var storePath = Path.Combine(Path.GetTempPath(), $"ccusage-model-agents-{Guid.NewGuid():N}.db");
        var store = new SqliteUsageStore(storePath);
        await store.InitializeAsync();
        try
        {
            var service = new UsageQueryService(store, TimeZoneInfo.Utc);
            await store.UpsertSessionFactsAsync(
            [
                Session("a1", "Claude Code", "/w", "w", "m-shared", new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero), 100, 0.1m),
                Session("a2", "Claude Code", "/w", "w", "m-shared", new DateTimeOffset(2026, 3, 1, 11, 0, 0, TimeSpan.Zero), 100, 0.1m),
                Session("a3", "Codex", "/w", "w", "m-shared", new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), 500, 0.5m),
                // m-tie 刻意让键序数胜者（Claude Code）token 更少（审查修复轮 P2：原量级下
                // "并列取 token 大者"的错误实现也能通过，断言无区分度）
                Session("a4", "Codex", "/w", "w", "m-tie", new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero), 900, 0.9m),
                Session("a5", "Claude Code", "/w", "w", "m-tie", new DateTimeOffset(2026, 3, 2, 11, 0, 0, TimeSpan.Zero), 100, 0.1m),
            ]);
            await store.UpsertUsageFactsAsync(
            [
                new UsageFact
                {
                    Day = "2026-03-03",
                    Grain = UsageFactGrain.Model,
                    Model = "m-facts-only",
                    TotalTokens = 400,
                    CostUsd = 0.4m,
                },
            ]);

            var rows = await service.GetModelBreakdownAsync(new UsageQueryFilter());

            var shared = rows.Single(row => row.Key == "m-shared");
            shared.DominantAgent.Should().Be("Claude Code", "主用 Agent 按会话数（2 > 1），与单会话 token 量无关");
            shared.AgentCount.Should().Be(2);

            var tie = rows.Single(row => row.Key == "m-tie");
            tie.DominantAgent.Should().Be("Claude Code",
                "1:1 并列取键序数最小（Claude Code < Codex）——该胜者 token 更少（100 < 900），可区分'按键序数'与'按 token 大者'两种实现");
            tie.AgentCount.Should().Be(2);

            var factsOnly = rows.Single(row => row.Key == "m-facts-only");
            factsOnly.SessionCount.Should().Be(0);
            factsOnly.DominantAgent.Should().BeNull("无会话数据时不猜关联 Agent");
            factsOnly.AgentCount.Should().BeNull();
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // A-1（Windows）：清池释放 .db 句柄后再删
            File.Delete(storePath);
        }
    }

    [Fact]
    public async Task Project_breakdown_matches_sql_and_guard_falls_back_to_raw_path()
    {
        var rows = await _service.GetProjectBreakdownAsync(new UsageQueryFilter());
        var expected = await QueryDimensionExpectationAsync("project", UsageFactGrain.Project, "project");

        rows.Select(row => row.Key).Should().Equal(expected.Select(row => row.Key));
        AssertDimensionRowsMatch(rows, expected, "project");

        rows.Should().OnlyContain(row => row.DominantAgent == null && row.AgentCount == null,
            "D-010 关联 Agent 列仅模型维度填充，项目维度恒为 null");

        // 歧义守卫（I-025 同式；审查后碰撞检测按 ordinal-ignore-case）：/work/tools、/other/tools、
        // /work/Tools 的展示名 tools / tools / Tools 在大小写不敏感比较下同名（大小写不敏感的本地
        // 文件系统上是同一路径）→ 三行全部退回显示原始路径，绝不合并不同项目。
        rows.Select(row => row.Key).Should().Equal(
            "/work/alpha", "/work/beta", "/other/tools", "/work/tools", "/work/Tools", "/work/gem", "/work/qwen");
        rows[0].DisplayName.Should().Be("alpha");
        rows[1].DisplayName.Should().Be("beta");
        rows[2].DisplayName.Should().Be("/other/tools");
        rows[3].DisplayName.Should().Be("/work/tools");
        rows[4].DisplayName.Should().Be("/work/Tools");
        rows[5].DisplayName.Should().Be("gem");
        rows[6].DisplayName.Should().Be("qwen");

        rows[0].TotalTokens.Should().Be(5500);
        rows[0].SessionCount.Should().Be(3);
        rows[0].Cost.KnownCostUsd.Should().Be(2.25m);
        rows[0].Cost.HasUnknownCost.Should().BeTrue();
        rows[1].TotalTokens.Should().Be(3350);
        rows[3].TotalTokens.Should().Be(30);
        rows[4].TotalTokens.Should().Be(10);
        rows[3].TokenShare.Should().Be(Math.Round(30m / 9790m, 4), "项目维度范围合计 = 9790（03-20 分解日无 project 粒度行，不进项目维度）");
    }

    // ---- I-029: 会话列表 ----

    [Fact]
    public async Task Sessions_search_matches_any_field()
    {
        var byProjectPath = await _service.GetSessionsAsync(new SessionListQuery { SearchTerm = "work/alpha" });
        byProjectPath.TotalCount.Should().Be(3);
        byProjectPath.Rows.Select(row => row.SessionId).Should().Equal("s4", "s2", "s1");

        var byModel = await _service.GetSessionsAsync(new SessionListQuery { SearchTerm = "GPT-X" });
        byModel.TotalCount.Should().Be(3, "搜索大小写不敏感");
        byModel.Rows.Select(row => row.SessionId).Should().BeEquivalentTo("s0", "s3", "s5");

        var byAgent = await _service.GetSessionsAsync(new SessionListQuery { SearchTerm = "qwen" });
        byAgent.Rows.Select(row => row.SessionId).Should().Equal("s8");

        var noHit = await _service.GetSessionsAsync(new SessionListQuery { SearchTerm = "不存在的标题" });
        noHit.TotalCount.Should().Be(0);
        noHit.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Sessions_combined_filters_sort_and_paging()
    {
        var result = await _service.GetSessionsAsync(new SessionListQuery
        {
            Project = "/work/beta",
            Model = "gpt-x",
            SortBy = SessionSortField.TotalTokens,
            SortDescending = true,
            Page = 1,
            PageSize = 1,
        });

        result.TotalCount.Should().Be(3);
        result.Rows.Should().ContainSingle();
        result.Rows[0].SessionId.Should().Be("s3", "beta + gpt-x 下 token 最多的是 s3 (3000)");
        result.Page.Should().Be(1);

        var page2 = await _service.GetSessionsAsync(new SessionListQuery
        {
            Project = "/work/beta",
            SortBy = SessionSortField.TotalTokens,
            Page = 2,
            PageSize = 1,
        });
        page2.TotalCount.Should().Be(3);
        page2.Rows.Select(row => row.SessionId).Should().Equal("s5");
    }

    [Fact]
    public async Task Sessions_sort_by_last_seen_defaults_descending()
    {
        var result = await _service.GetSessionsAsync(new SessionListQuery());

        result.TotalCount.Should().Be(10);
        result.Rows.First().SessionId.Should().Be("s9", "默认最近活动优先（s9 03-15）");
        result.Rows.Select(row => row.LastSeenAt).Should().Equal(result.Rows.Select(row => row.LastSeenAt).OrderByDescending(x => x));

        var ascending = await _service.GetSessionsAsync(new SessionListQuery { SortDescending = false });
        ascending.Rows.First().SessionId.Should().Be("s7", "纪元会话的 LastSeenAt 最小");
    }

    [Fact]
    public async Task Sessions_cost_sort_puts_unknown_cost_after_known_costs()
    {
        var result = await _service.GetSessionsAsync(new SessionListQuery { SortBy = SessionSortField.Cost });

        var known = result.Rows.TakeWhile(row => row.CostUsd is not null).ToList();
        var unknown = result.Rows.SkipWhile(row => row.CostUsd is not null).ToList();
        known.Should().HaveCount(8);
        unknown.Select(row => row.SessionId).Should().Equal("s2", "s5");
        known.Select(row => row.CostUsd).Should().Equal(known.Select(row => row.CostUsd).OrderByDescending(cost => cost));
        known.First().SessionId.Should().Be("s3");
    }

    [Fact]
    public async Task Sessions_respect_time_range_and_agent_filter()
    {
        var result = await _service.GetSessionsAsync(new SessionListQuery
        {
            Filter = new UsageQueryFilter { FromDay = "2026-03-10", ToDay = "2026-03-11", Agent = "Claude Code" },
        });

        result.TotalCount.Should().Be(3);
        result.Rows.Select(row => row.SessionId).Should().Equal("s4", "s2", "s1");
        result.Rows.Should().OnlyContain(row => row.Agent == "Claude Code");
    }

    [Fact]
    public async Task Sessions_pagination_rejects_non_positive_page_size_and_handles_extreme_pages()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetSessionsAsync(new SessionListQuery { PageSize = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetSessionsAsync(new SessionListQuery { PageSize = -5 }));

        // (page-1)*pageSize 用 long 计算：极端页码不再 int 溢出，越界返回空页。
        var beyond = await _service.GetSessionsAsync(new SessionListQuery { Page = 2, PageSize = int.MaxValue });
        beyond.Rows.Should().BeEmpty();
        beyond.TotalCount.Should().Be(10);

        var first = await _service.GetSessionsAsync(new SessionListQuery { Page = 1, PageSize = int.MaxValue });
        first.Rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task Daily_series_caps_zero_fill_range_at_ten_years()
    {
        var from = new DateOnly(2000, 1, 1);
        var cappedTo = UsageQueryFilter.FormatDay(from.AddDays(UsageQueryFilter.MaxDailySeriesDays - 1));
        var capped = await _service.GetDailySeriesAsync(new UsageQueryFilter { FromDay = "2000-01-01", ToDay = cappedTo });
        capped.Should().HaveCount(UsageQueryFilter.MaxDailySeriesDays, "恰好上限（3653 天，10 年含闰日余量）可用");

        var overTo = UsageQueryFilter.FormatDay(from.AddDays(UsageQueryFilter.MaxDailySeriesDays));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.GetDailySeriesAsync(
            new UsageQueryFilter { FromDay = "2000-01-01", ToDay = overTo }));
    }

    // ---- I-029: 小时分布 ----

    [Fact]
    public async Task Hourly_distribution_excludes_imprecise_timestamps_and_counts_unknown()
    {
        var result = await _service.GetHourlyDistributionAsync(new UsageQueryFilter());

        result.Hours.Should().HaveCount(24, "0..23 全量返回，空桶由服务补零");
        result.Hours.Select(bucket => bucket.Hour).Should().Equal(Enumerable.Range(0, 24));
        result.TimeZoneId.Should().Be(TimeZoneInfo.Utc.Id);

        // 无分钟级精度：s7 纪元兜底（1970）+ s8 日锚定 00:00 —— 计 unknown，不入桶，不伪造分布。
        result.UnknownSessionCount.Should().Be(2);
        result.UnknownTotalTokens.Should().Be(1200);

        var bucket9 = result.Hours.Single(bucket => bucket.Hour == 9);
        bucket9.SessionCount.Should().Be(2);
        bucket9.TotalTokens.Should().Be(4500);
        bucket9.Cost.KnownCostUsd.Should().Be(1.90m);

        result.Hours.Single(bucket => bucket.Hour == 23).TotalTokens.Should().Be(3000);
        result.Hours.Single(bucket => bucket.Hour == 0).SessionCount.Should().Be(0, "s8 的日锚定 00:00 被排除，00 点桶保持空");

        var accountedSessions = result.Hours.Sum(bucket => bucket.SessionCount);
        (accountedSessions + result.UnknownSessionCount).Should().Be(10, "每个会话要么入桶、要么计入 unknown，无静默丢失");
    }

    [Fact]
    public async Task Hourly_distribution_respects_range_and_agent_filter_and_unknown_cost_flag()
    {
        var result = await _service.GetHourlyDistributionAsync(
            new UsageQueryFilter { FromDay = "2026-03-10", ToDay = "2026-03-11", Agent = "Claude Code" });

        result.UnknownSessionCount.Should().Be(0, "日锚定的 s8 属于 Qwen Code，被 Agent 过滤后不进入 unknown 统计");
        result.Hours.Single(bucket => bucket.Hour == 8).TotalTokens.Should().Be(1750);
        var hour14 = result.Hours.Single(bucket => bucket.Hour == 14);
        hour14.SessionCount.Should().Be(1);
        hour14.TotalTokens.Should().Be(150);
        hour14.Cost.KnownCostUsd.Should().BeNull("s2 会话成本全未知（null）");
        hour14.Cost.HasUnknownCost.Should().BeTrue();
        result.Hours.Sum(bucket => bucket.SessionCount).Should().Be(3);
    }

    [Fact]
    public async Task Hourly_distribution_utc_midnight_anchor_is_unknown_on_non_utc_timezone()
    {
        // 审查 P1 回归：Goose 式日期级来源锚定 "{date}T00:00:00Z"（UTC 午夜）。+08:00 机器上其
        // 本地 TimeOfDay 为 08 点——只查本地 TimeOfDay 会把会话伪造进 08 点桶且 unknown 不可见。
        var storePath = Path.Combine(Path.GetTempPath(), $"ccusage-tz-{Guid.NewGuid():N}.db");
        var store = new SqliteUsageStore(storePath);
        await store.InitializeAsync();
        try
        {
            await store.UpsertSessionFactsAsync(
            [
                new SessionFact
                {
                    SessionId = "g1",
                    Agent = "Goose",
                    Project = "/p",
                    DominantModel = "m",
                    FirstSeenAt = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero),
                    LastSeenAt = new DateTimeOffset(2026, 3, 10, 0, 30, 0, TimeSpan.Zero),
                    TotalTokens = 400,
                    CostUsd = 0.10m,
                },
                new SessionFact
                {
                    SessionId = "g2",
                    Agent = "Goose",
                    Project = "/p",
                    DominantModel = "m",
                    FirstSeenAt = new DateTimeOffset(2026, 3, 10, 2, 30, 0, TimeSpan.Zero),
                    LastSeenAt = new DateTimeOffset(2026, 3, 10, 3, 0, 0, TimeSpan.Zero),
                    TotalTokens = 100,
                    CostUsd = 0.10m,
                },
            ]);
            var shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
            var service = new UsageQueryService(store, shanghai);

            var result = await service.GetHourlyDistributionAsync(new UsageQueryFilter());

            result.UnknownSessionCount.Should().Be(1, "UTC 午夜锚定与查询时区无关地判为无精度");
            result.UnknownTotalTokens.Should().Be(400);
            result.Hours.Single(bucket => bucket.Hour == 8).SessionCount.Should().Be(0, "不得被伪造进 +08:00 的 08 点桶");
            result.Hours.Single(bucket => bucket.Hour == 10).TotalTokens.Should().Be(100, "02:30Z → 上海本地 10:30 桶");
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // A-1（Windows）：清池释放 .db 句柄后再删
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }
    }

    // ---- 全局 Model / Project 过滤（右栏撤除定案：顶栏四维筛选的查询契约）----

    [Fact]
    public async Task Kpi_with_model_filter_scopes_to_model()
    {
        var kpi = await _service.GetKpiAsync(new UsageQueryFilter { Model = "claude-sonnet-4" });

        // 种子中 claude-sonnet-4 的 full 行：03-10 3600 / 03-11 1750 / 03-14 30 / 03-15 900
        kpi.TotalTokens.Should().Be(3600 + 1750 + 30 + 900);
    }

    [Fact]
    public async Task Kpi_with_project_filter_scopes_to_project_path()
    {
        var kpi = await _service.GetKpiAsync(new UsageQueryFilter { Project = "/work/alpha" });

        // /work/alpha 的 full 行：03-10 sonnet 3600 + 03-10 unpriced 150 + 03-11 sonnet 1750
        // （unpriced 行同属 alpha，与项目维度聚合既有 5500 断言同口径）
        kpi.TotalTokens.Should().Be(3600 + 150 + 1750);
    }

    [Fact]
    public async Task Daily_series_with_model_filter_excludes_other_models()
    {
        var points = await _service.GetDailySeriesAsync(new UsageQueryFilter
        {
            FromDay = "2026-03-10",
            ToDay = "2026-03-10",
            Model = "claude-sonnet-4",
        });

        // 当天 sonnet 的 full 行 total=3600；gpt-x 行（3000）与无匹配模型行被排除
        points.Single().TotalTokens.Should().Be(3600);
    }

    [Fact]
    public async Task Sessions_with_model_and_project_filter_scope_rows()
    {
        var result = await _service.GetSessionsAsync(new SessionListQuery
        {
            Filter = new UsageQueryFilter { Model = "claude-sonnet-4", Project = "/work/alpha" },
            PageSize = null,
        });

        result.Rows.Should().OnlyContain(row => row.DominantModel == "claude-sonnet-4" && row.Project == "/work/alpha");
        result.Rows.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Kpi_previous_period_inherits_model_and_project_filters()
    {
        // 双端有界才产生环比；环比窗口 = 等长回看，过滤维度必须原样继承
        var kpi = await _service.GetKpiAsync(new UsageQueryFilter
        {
            FromDay = "2026-03-14",
            ToDay = "2026-03-20",
            Model = "claude-sonnet-4",
            Project = "/work/tools",
        });

        var previous = kpi.PreviousPeriod;
        previous.Should().NotBeNull();
        // 回看窗口 03-07..03-13 内 sonnet 且 /work/tools 无数据 → 0（若过滤未继承，会数到其他项目的行）
        previous!.SessionCount.Should().Be(0);
        previous.TotalTokens.Should().Be(0);
    }

    // ---- fixture 与工具 ----

    /// <summary>手写 SQL 维度期望行（审查 P2-1：三个维度统一 SQL 对照）。</summary>
    private sealed record DimensionSqlExpectation(
        string Key,
        long Input,
        long Output,
        long CacheCreation,
        long CacheRead,
        long Total,
        decimal? KnownCost,
        long UnknownRows,
        long Sessions);

    /// <summary>
    /// 手写 SQL 期望 = facts 聚合（full 粒度优先，无 full 日退回对应分解粒度行）∪ 会话计数
    /// （键并集——会话侧可能有 facts 缺失的键，反之亦然），排序与实现契约一致
    /// （TotalTokens 降序、Key 序数平局）。
    /// </summary>
    private async Task<List<DimensionSqlExpectation>> QueryDimensionExpectationAsync(
        string usageColumn, string decompositionGrain, string sessionColumn)
    {
        var usageSql = $"""
            SELECT u.{usageColumn}, SUM(input_tokens), SUM(output_tokens), SUM(cache_creation_tokens),
                   SUM(cache_read_tokens), SUM(total_tokens), SUM(cost_usd), SUM(cost_usd IS NULL)
            FROM usage_facts u
            WHERE u.{usageColumn} <> ''
              AND (grain = 'full'
                   OR (grain = '{decompositionGrain}' AND NOT EXISTS (
                       SELECT 1 FROM usage_facts f2 WHERE f2.day = u.day AND f2.grain = 'full')))
            GROUP BY u.{usageColumn}
            """;
        var sessionSql = $"""
            SELECT {sessionColumn}, COUNT(*) FROM session_facts
            WHERE {sessionColumn} <> '' GROUP BY {sessionColumn}
            """;

        var expected = (await QueryAsync(usageSql, reader => new DimensionSqlExpectation(
            Key: reader.GetString(0),
            Input: reader.GetInt64(1),
            Output: reader.GetInt64(2),
            CacheCreation: reader.GetInt64(3),
            CacheRead: reader.GetInt64(4),
            Total: reader.GetInt64(5),
            KnownCost: reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            UnknownRows: reader.GetInt64(7),
            Sessions: 0L))).ToDictionary(row => row.Key, row => row);

        foreach (var sessionRow in await QueryAsync(sessionSql, reader => new { Key = reader.GetString(0), Count = reader.GetInt64(1) }))
        {
            expected[sessionRow.Key] = expected.TryGetValue(sessionRow.Key, out var existing)
                ? existing with { Sessions = sessionRow.Count }
                : new DimensionSqlExpectation(sessionRow.Key, 0, 0, 0, 0, 0, null, 0, sessionRow.Count);
        }

        return expected.Values
            .OrderByDescending(row => row.Total)
            .ThenBy(row => row.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>维度行与 SQL 期望逐键逐列对照（token 分桶 / 已知成本与未知标记 / 会话数）。</summary>
    private static void AssertDimensionRowsMatch(
        IReadOnlyList<DimensionUsageRow> rows,
        IReadOnlyList<DimensionSqlExpectation> expected,
        string dimension)
    {
        foreach (var exp in expected)
        {
            var row = rows.Single(candidate => candidate.Key == exp.Key);
            var because = $"{dimension}:{exp.Key}";
            row.InputTokens.Should().Be(exp.Input, because);
            row.OutputTokens.Should().Be(exp.Output, because);
            row.CacheCreationTokens.Should().Be(exp.CacheCreation, because);
            row.CacheReadTokens.Should().Be(exp.CacheRead, because);
            row.TotalTokens.Should().Be(exp.Total, because);
            if (exp.KnownCost is { } knownCost)
            {
                row.Cost.KnownCostUsd.Should().BeApproximately(knownCost, 1e-9m, because);
            }
            else
            {
                row.Cost.KnownCostUsd.Should().BeNull(because, "无一已知成本为 null（不是 0）");
            }

            row.Cost.HasUnknownCost.Should().Be(exp.UnknownRows > 0, because);
            row.SessionCount.Should().Be(exp.Sessions, because);
        }
    }

    private async Task SeedAsync()
    {
        var facts = new List<UsageFact>
        {
            Full("2026-03-08", "Codex", "gpt-x", "/work/beta", "beta", input: 100, output: 50, total: 150, cost: 1.00m),
            Full("2026-03-10", "Claude Code", "claude-sonnet-4", "/work/alpha", "alpha", input: 1000, output: 500, cacheCreation: 100, cacheRead: 2000, total: 3600, cost: 1.50m),
            Full("2026-03-10", "Claude Code", "claude-unpriced-model", "/work/alpha", "alpha", input: 100, output: 50, total: 150, cost: null),
            Full("2026-03-10", "Codex", "gpt-x", "/work/beta", "beta", input: 2000, output: 1000, total: 3000, cost: 2.00m),
            Full("2026-03-11", "Claude Code", "claude-sonnet-4", "/work/alpha", "alpha", input: 500, output: 250, cacheRead: 1000, total: 1750, cost: 0.75m),
            Full("2026-03-11", "Codex", "gpt-x", "/work/beta", "beta", input: 100, output: 100, total: 200, cost: null),
            Full("2026-03-14", "Claude Code", "claude-sonnet-4", "/work/tools", "tools", input: 10, output: 20, total: 30, cost: 0.05m),
            Full("2026-03-15", "Claude Code", "claude-sonnet-4", "/other/tools", "tools", input: 900, total: 900, cost: 0.40m),
            // 大小写变体路径（审查 P2-5）：/work/Tools 的展示名 "Tools" 与 "tools" 仅大小写不同，
            // 歧义守卫按 ordinal-ignore-case 判碰撞 → 三行全部退回原始路径。
            Full("2026-03-17", "Claude Code", "claude-unpriced-model", "/work/Tools", "Tools", input: 10, total: 10, cost: null),
            // CLI 分解日（无 full 行）：day / model / agent 粒度行
            new() { Day = "2026-03-20", Grain = UsageFactGrain.Day, InputTokens = 700, OutputTokens = 300, TotalTokens = 1000, CostUsd = 0.90m },
            new() { Day = "2026-03-20", Grain = UsageFactGrain.Model, Model = "gpt-x", TotalTokens = 600, CostUsd = 0.54m },
            new() { Day = "2026-03-20", Grain = UsageFactGrain.Model, Model = "claude-sonnet-4", TotalTokens = 400, CostUsd = 0.36m },
            new() { Day = "2026-03-20", Grain = UsageFactGrain.Agent, Agent = "Codex", TotalTokens = 1000, CostUsd = 0.90m },
        };

        var sessions = new List<SessionFact>
        {
            Session("s0", "Codex", "/work/beta", "beta", "gpt-x", new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero), 150, 1.00m),
            Session("s1", "Claude Code", "/work/alpha", "alpha", "claude-sonnet-4", new DateTimeOffset(2026, 3, 10, 9, 15, 0, TimeSpan.Zero), 3600, 1.50m),
            Session("s2", "Claude Code", "/work/alpha", "alpha", "claude-unpriced-model", new DateTimeOffset(2026, 3, 10, 14, 0, 0, TimeSpan.Zero), 150, null),
            Session("s3", "Codex", "/work/beta", "beta", "gpt-x", new DateTimeOffset(2026, 3, 10, 23, 30, 0, TimeSpan.Zero), 3000, 2.00m),
            Session("s4", "Claude Code", "/work/alpha", "alpha", "claude-sonnet-4", new DateTimeOffset(2026, 3, 11, 8, 0, 0, TimeSpan.Zero), 1750, 0.75m),
            Session("s5", "Codex", "/work/beta", "beta", "gpt-x", new DateTimeOffset(2026, 3, 11, 12, 0, 0, TimeSpan.Zero), 200, null),
            Session("s6", "Claude Code", "/work/tools", string.Empty, "claude-sonnet-4", new DateTimeOffset(2026, 3, 14, 21, 0, 0, TimeSpan.Zero), 30, 0.05m),
            // 纪元兜底时间戳（采集层 mtime 不可得时的文档化兜底）→ 无分钟级精度
            Session("s7", "Gemini CLI", "/work/gem", "gem", "gemini-x", new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), 500, 0.10m),
            // 日锚定 00:00 时间戳 → 与日粒度来源不可辨，按"宁排除不伪造"计 unknown
            Session("s8", "Qwen Code", "/work/qwen", "qwen", "qwen-x", new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), 700, 0.20m),
            Session("s9", "Claude Code", "/other/tools", string.Empty, "claude-sonnet-4", new DateTimeOffset(2026, 3, 15, 9, 0, 0, TimeSpan.Zero), 900, 0.40m),
        };

        await _store.UpsertUsageFactsAsync(facts);
        await _store.UpsertSessionFactsAsync(sessions);
    }

    private static UsageFact Full(
        string day,
        string agent,
        string model,
        string project,
        string displayName,
        long input = 0,
        long output = 0,
        long cacheCreation = 0,
        long cacheRead = 0,
        long total = 0,
        decimal? cost = null) => new()
    {
        Day = day,
        Grain = UsageFactGrain.Full,
        Agent = agent,
        Model = model,
        Project = project,
        ProjectDisplayName = displayName,
        InputTokens = input,
        OutputTokens = output,
        CacheCreationTokens = cacheCreation,
        CacheReadTokens = cacheRead,
        TotalTokens = total,
        CostUsd = cost,
    };

    private static SessionFact Session(
        string id,
        string agent,
        string project,
        string displayName,
        string model,
        DateTimeOffset firstSeenAt,
        long totalTokens,
        decimal? cost) => new()
    {
        SessionId = id,
        Agent = agent,
        Project = project,
        ProjectDisplayName = displayName,
        DominantModel = model,
        FirstSeenAt = firstSeenAt,
        LastSeenAt = firstSeenAt + TimeSpan.FromMinutes(30),
        TotalTokens = totalTokens,
        CostUsd = cost,
    };

    private async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> map)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}
