using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.HermesSqlite;
using Microsoft.Data.Sqlite;

namespace CCUsage.Infrastructure.Tests.HermesSqlite;

/// <summary>
/// I-016: Hermes SQLite 适配器测试（临时库 fixtures，合成数据；语义对照
/// ../ccusage/rust/adapters/hermes/ 的 loader.rs / parser.rs / paths.rs 内联测试，同名场景逐一对齐）。
/// </summary>
public class HermesSqliteAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly List<string> _environmentVariables = new();

    /// <summary>TC-001: 蓝本 loads_billable_hermes_sessions_from_state_db：五桶 + reasoning 并入 + actual cost 直采。</summary>
    [Fact]
    public async Task Loads_billable_hermes_sessions_from_state_db()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(Path.Combine(home, "state.db"));
        InsertSession(db, new SessionFixture(
            Id: "session-1",
            Model: "claude-sonnet-4-20250514",
            Provider: "anthropic",
            StartedAt: 1_750_000_000.25,
            MessageCount: 42,
            InputTokens: 1200,
            OutputTokens: 300,
            CacheReadTokens: 50,
            CacheWriteTokens: 20,
            ReasoningTokens: 10,
            EstimatedCost: null,
            ActualCost: 0.34));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        var record = result.Records[0];
        // 1_750_000_000.25 s → ×1000 = 1_750_000_000_250 ms（截断 .25s 的 250ms 内毫秒保留）。
        record.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_250));
        record.Agent.Should().Be("Hermes");
        record.ProjectPath.Should().Be("Hermes");
        record.SessionId.Should().Be("session-1");
        record.MessageId.Should().Be("hermes:session-1", "蓝本 message.id 形状");
        record.Model.Should().Be("claude-sonnet-4-20250514");
        record.InputTokens.Should().Be(1200);
        record.OutputTokens.Should().Be(310, "reasoning = 10 并入 output（I-006 口径）");
        record.CacheCreationTokens.Should().Be(20);
        record.CacheReadTokens.Should().Be(50);
        record.CostUsd.Should().Be(0.34m, "actual_cost_usd 直采（蓝本 recorded_positive_cost_is_trusted）");

        var diagnostics = result.Diagnostics;
        diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        diagnostics.FilesDiscovered.Should().Be(1);
        diagnostics.FilesRead.Should().Be(1);
        diagnostics.MalformedLines.Should().Be(0);
        diagnostics.InvalidRecords.Should().Be(0);
        diagnostics.MissingCostRecords.Should().Be(0);
    }

    /// <summary>蓝本 recorded_zero_cost_falls_back_to_pricing_calculation：stored cost 0 仍走定价引擎。</summary>
    [Fact]
    public async Task Recorded_zero_cost_falls_back_to_pricing_engine()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(Path.Combine(home, "state.db"));
        InsertSession(db, new SessionFixture(
            "subscription-included", "gpt-5.1", "openai", 1_789_462_800_000.0, 72,
            InputTokens: 244_075, OutputTokens: 10_019, CacheReadTokens: 3_339_776,
            CacheWriteTokens: 0, ReasoningTokens: 3_216,
            EstimatedCost: null, ActualCost: 0.0));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.CostUsd.Should().BeNull("stored cost 为 0 → 走定价引擎");
        var priced = new PricingEngine().PriceRecord(record);
        priced.CostUsd.Should().BeGreaterThan(0m, "有 token 用量的会话仍应被定价（内置快照 gpt-5.1）");
        result.Diagnostics.MissingCostRecords.Should().Be(1);
    }

    /// <summary>estimated_cost_usd 回退（actual 缺失时）；全零行（无 tokens 且无 cost）跳过。</summary>
    [Fact]
    public async Task Falls_back_to_estimated_cost_and_skips_all_zero_rows()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(Path.Combine(home, "state.db"));
        InsertSession(db, new SessionFixture(
            "session-estimated", "gpt-5.1", "openai", 1_789_462_800_000.0, 1,
            InputTokens: 100, OutputTokens: 10, CacheReadTokens: 0, CacheWriteTokens: 0,
            ReasoningTokens: 0, EstimatedCost: 0.01, ActualCost: null));
        InsertSession(db, new SessionFixture(
            "session-zero", "gpt-5.1", "openai", 1_789_462_800_000.0, 0,
            InputTokens: 0, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 0,
            ReasoningTokens: 0, EstimatedCost: 0.0, ActualCost: 0.0));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle().Subject.CostUsd.Should().Be(0.01m, "actual 缺失回退 estimated");
        result.Diagnostics.LinesSkipped.Should().Be(1, "五桶与 cost 全零的行按 skipped 计");
    }

    /// <summary>身份列 NULL / started_at 不可读 / 时间戳非正或越界 → 整行作废（InvalidRecords）。</summary>
    [Fact]
    public async Task Rejects_rows_with_invalid_identity_or_timestamp()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(Path.Combine(home, "state.db"));
        InsertRaw(db, "INSERT INTO sessions (id, source, model, started_at, input_tokens) VALUES (NULL, 'cli', 'gpt-5.1', 1789462800000, 10)");
        InsertRaw(db, "INSERT INTO sessions (id, source, model, started_at, input_tokens) VALUES ('session-bad-time', 'cli', 'gpt-5.1', 'not-a-number', 10)");
        InsertRaw(db, "INSERT INTO sessions (id, source, model, started_at, input_tokens) VALUES ('session-negative-time', 'cli', 'gpt-5.1', -5.0, 10)");
        InsertSession(db, new SessionFixture(
            "session-ok", "gpt-5.1", "openai", 1_789_462_800_000.0, 1,
            InputTokens: 10, OutputTokens: 1, CacheReadTokens: 0, CacheWriteTokens: 0,
            ReasoningTokens: 0, EstimatedCost: null, ActualCost: null));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle().Subject.SessionId.Should().Be("session-ok");
        result.Diagnostics.InvalidRecords.Should().Be(3);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    /// <summary>model 为空白的行在 SQL 层排除（蓝本 WHERE 子句），不计入诊断。</summary>
    [Fact]
    public async Task Excludes_blank_model_rows_in_sql()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(Path.Combine(home, "state.db"));
        InsertRaw(db, "INSERT INTO sessions (id, source, model, started_at, input_tokens) VALUES ('session-null-model', 'cli', NULL, 1789462800000, 10)");
        InsertRaw(db, "INSERT INTO sessions (id, source, model, started_at, input_tokens) VALUES ('session-blank-model', 'cli', '   ', 1789462800000, 10)");

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.InvalidRecords.Should().Be(0);
        result.Diagnostics.FilesRead.Should().Be(1);
    }

    /// <summary>秒/毫秒判别（蓝本 timestamp_from_number）：>1e12 按毫秒、否则 ×1000；f64 截断取整。</summary>
    [Fact]
    public async Task Distinguishes_second_and_millisecond_started_at_scales()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(Path.Combine(home, "state.db"));
        InsertSession(db, new SessionFixture(
            "session-seconds", "gpt-5.1", "openai", 1_789_462_800.0, 1,
            InputTokens: 10, OutputTokens: 1, CacheReadTokens: 0, CacheWriteTokens: 0,
            ReasoningTokens: 0, EstimatedCost: null, ActualCost: null));
        InsertSession(db, new SessionFixture(
            "session-millis", "gpt-5.1", "openai", 1_789_462_800_123.0, 1,
            InputTokens: 10, OutputTokens: 1, CacheReadTokens: 0, CacheWriteTokens: 0,
            ReasoningTokens: 0, EstimatedCost: null, ActualCost: null));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Single(record => record.SessionId == "session-seconds").Timestamp
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_789_462_800_000));
        result.Records.Single(record => record.SessionId == "session-millis").Timestamp
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_789_462_800_123));
    }

    /// <summary>started_at 为整数存储列：read_f64 的 i64 兜底（蓝本同口径）。</summary>
    [Fact]
    public async Task Reads_integer_started_at_column()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(Path.Combine(home, "state.db"));
        InsertRaw(db, "INSERT INTO sessions (id, source, model, started_at, input_tokens, output_tokens) VALUES ('session-int-time', 'cli', 'gpt-5.1', 1789462800, 10, 1)");

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle().Subject.Timestamp
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_789_462_800_000));
    }

    /// <summary>schema 不支持（无 sessions 表）→ 该库 0 条 + 降级，不抛异常。</summary>
    [Fact]
    public async Task Database_without_sessions_table_degrades_to_no_data()
    {
        var home = CreateTempHome();
        var db = Path.Combine(home, "state.db");
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (id TEXT)";
            command.ExecuteNonQuery();
        }

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.FilesDiscovered.Should().Be(1);
        result.Diagnostics.FilesRead.Should().Be(0);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>跨库按 session id 去重（蓝本 seen_sessions 全局集合，先扫的库保留）。</summary>
    [Fact]
    public async Task Deduplicates_cross_database_sessions_keeping_first_home()
    {
        var homeA = CreateTempHome();
        var homeB = CreateTempHome();
        var dbA = CreateDatabase(Path.Combine(homeA, "state.db"));
        var dbB = CreateDatabase(Path.Combine(homeB, "state.db"));
        InsertSession(dbA, new SessionFixture(
            "shared-session", "gpt-5.1", "openai", 1_789_462_800_000.0, 1,
            InputTokens: 10, OutputTokens: 1, CacheReadTokens: 0, CacheWriteTokens: 0,
            ReasoningTokens: 0, EstimatedCost: null, ActualCost: null));
        InsertSession(dbB, new SessionFixture(
            "shared-session", "gpt-5.1", "openai", 1_789_462_800_000.0, 1,
            InputTokens: 20, OutputTokens: 2, CacheReadTokens: 0, CacheWriteTokens: 0,
            ReasoningTokens: 0, EstimatedCost: null, ActualCost: null));

        var result = await CreateAdapter($"{homeA},{homeB}").ScanAsync();

        result.Records.Should().ContainSingle().Subject.InputTokens.Should().Be(10, "先扫的库保留（蓝本 seen_sessions）");
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    /// <summary>记录按时间戳升序。</summary>
    [Fact]
    public async Task Sorts_records_by_timestamp()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(Path.Combine(home, "state.db"));
        InsertSession(db, new SessionFixture(
            "session-late", "gpt-5.1", "openai", 1_789_471_800_000.0, 1,
            InputTokens: 10, OutputTokens: 1, CacheReadTokens: 0, CacheWriteTokens: 0,
            ReasoningTokens: 0, EstimatedCost: null, ActualCost: null));
        InsertSession(db, new SessionFixture(
            "session-early", "gpt-5.1", "openai", 1_789_462_800_000.0, 1,
            InputTokens: 10, OutputTokens: 1, CacheReadTokens: 0, CacheWriteTokens: 0,
            ReasoningTokens: 0, EstimatedCost: null, ActualCost: null));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Select(record => record.SessionId).Should().ContainInOrder("session-early", "session-late");
    }

    /// <summary>HERMES_HOME 多根（蓝本 paths.rs：逗号分隔、去空白、跳过空段、按序去重）。</summary>
    [Fact]
    public void Parses_comma_separated_hermes_home()
    {
        var homeA = CreateTempHome();
        var homeB = CreateTempHome();
        CreateDatabase(Path.Combine(homeA, "state.db"));
        CreateDatabase(Path.Combine(homeB, "state.db"));
        SetEnvironment("HERMES_HOME", $" {homeA}, , {homeB}, {homeA} ");

        new HermesLogLocator().ResolveDatabases(null)
            .Should().BeEquivalentTo([Path.Combine(homeA, "state.db"), Path.Combine(homeB, "state.db")]);
    }

    [Fact]
    public void Configured_home_takes_priority_over_environment_variable()
    {
        var configured = CreateTempHome();
        var fromEnvironment = CreateTempHome();
        CreateDatabase(Path.Combine(configured, "state.db"));
        CreateDatabase(Path.Combine(fromEnvironment, "state.db"));
        SetEnvironment("HERMES_HOME", fromEnvironment);

        new HermesLogLocator().ResolveDatabases(configured).Should().BeEquivalentTo([Path.Combine(configured, "state.db")]);
    }

    [Fact]
    public void Falls_back_to_hermes_directory_under_home()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(Path.Combine(home, ".hermes", "state.db"));
        SetEnvironment("HERMES_HOME", null);
        SetEnvironment("USERPROFILE", home);
        SetEnvironment("HOME", null);

        new HermesLogLocator().ResolveDatabases(null).Should().BeEquivalentTo([db]);
    }

    [Fact]
    public async Task Missing_database_returns_no_data()
    {
        var home = CreateTempHome();

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.FilesDiscovered.Should().Be(0);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    // ---- 映射器纯函数（蓝本 parser.rs 内联测试的候选/归一化部分）----

    /// <summary>蓝本 tries_provider_qualified_model_candidate_first：qualified exact 命中时插在最前。</summary>
    [Fact]
    public void Tries_provider_qualified_model_candidate_first()
    {
        HermesSessionParser.BuildModelCandidates("openai", "gpt-5.5", candidate => candidate == "openai/gpt-5.5")
            .Should().Equal("openai/gpt-5.5", "gpt-5.5");
    }

    /// <summary>蓝本 uses_raw_model_for_timestamped_pricing_when_provider_match_is_fuzzy：qualified 非 exact
    /// 时不插入（避免模糊命中），仅裸模型名。</summary>
    [Fact]
    public void Uses_raw_model_when_provider_match_is_not_exact()
    {
        HermesSessionParser.BuildModelCandidates("deepseek", "deepseek-v4-flash", _ => false)
            .Should().Equal("deepseek-v4-flash");
    }

    /// <summary>蓝本 uses_exact_provider_pricing_from_embedded_fallback：qualified 与裸名都 exact 时双候选。</summary>
    [Fact]
    public void Includes_both_candidates_when_both_match_exactly()
    {
        HermesSessionParser.BuildModelCandidates("deepseek", "deepseek-v4-flash", _ => true)
            .Should().Equal("deepseek/deepseek-v4-flash", "deepseek-v4-flash");
    }

    /// <summary>蓝本 accepts_normalized_provider_pricing：'.'/'@'→'-' 键归一化口径（normalized_pricing_key）。</summary>
    [Fact]
    public void Normalizes_dots_and_at_signs_in_pricing_keys()
    {
        var entries = new[] { ("deepseek/deepseek-v4-flash", "deepseek-v4-flash") };
        HermesSessionParser.HasExactNormalizedPrice(entries, "deepseek/deepseek.v4.flash").Should().BeTrue();
        HermesSessionParser.HasExactNormalizedPrice(entries, "deepseek/deepseek-v4-flash").Should().BeTrue();
        HermesSessionParser.HasExactNormalizedPrice(entries, "deepseek/deepseek.v4.flash-preview").Should().BeFalse();
    }

    /// <summary>蓝本 normalize_provider / infer_provider_from_model 全分支。</summary>
    [Fact]
    public void Normalizes_and_infers_providers()
    {
        HermesSessionParser.NormalizeProvider("OpenAI", "gpt-5.1").Should().Be("openai");
        HermesSessionParser.NormalizeProvider("github-copilot", "gpt-5.1").Should().Be("github_copilot");
        HermesSessionParser.NormalizeProvider("claude", "x").Should().Be("anthropic");
        HermesSessionParser.NormalizeProvider("vertex_ai", "x").Should().Be("google");
        HermesSessionParser.NormalizeProvider(null, "claude-sonnet-4-5").Should().Be("anthropic");
        HermesSessionParser.NormalizeProvider(null, "claude/sonnet").Should().Be("anthropic");
        HermesSessionParser.NormalizeProvider(null, "gpt-5.1").Should().Be("openai");
        HermesSessionParser.NormalizeProvider(null, "chatgpt-x").Should().Be("openai");
        HermesSessionParser.NormalizeProvider(null, "o3").Should().Be("openai", "o<digit> 前缀");
        HermesSessionParser.NormalizeProvider(null, "openrouter-x").Should().Be("hermes", "openrouter 不在推断清单（仅显式归一）");
        HermesSessionParser.NormalizeProvider(null, "gemini-3-pro").Should().Be("google");
        HermesSessionParser.NormalizeProvider(null, "gemini/pro").Should().Be("google");
        HermesSessionParser.NormalizeProvider(null, "deepseek-v4").Should().Be("hermes");
    }

    /// <summary>蓝本 calculates_cost_for_hermes_frontier_models_from_embedded_pricing 的移植：
    /// gpt-5.5 / grok-4.3 等前沿模型按内置快照可计价（成本 > 0）。</summary>
    [Fact]
    public void Prices_hermes_frontier_models_from_builtin_snapshot()
    {
        var engine = new PricingEngine();
        foreach (var model in new[] { "gpt-5.5", "grok-4.3" })
        {
            var detail = new TokenUsageDetail
            {
                InputTokens = 1_000,
                OutputTokens = 150, // 100 output + 50 reasoning（蓝本按输出价计费）
            };
            var priced = engine.PriceTokens(model, detail);
            priced.CostUsd.Should().NotBeNull($"'{model}' 应命中内置快照定价");
            priced.CostUsd.Should().BeGreaterThan(0m);
        }
    }

    /// <summary>蓝本 timestamp_from_number 边界：非有限 / 非正作废；超大值由 Map 防御边界拒绝。</summary>
    [Fact]
    public void Rejects_non_finite_and_non_positive_timestamps()
    {
        HermesSessionParser.TryTimestampFromNumber(double.NaN, out _).Should().BeFalse();
        HermesSessionParser.TryTimestampFromNumber(double.PositiveInfinity, out _).Should().BeFalse();
        HermesSessionParser.TryTimestampFromNumber(0, out _).Should().BeFalse();
        HermesSessionParser.TryTimestampFromNumber(-1000, out _).Should().BeFalse();
        HermesSessionParser.TryTimestampFromNumber(1e12, out var boundary).Should().BeTrue();
        boundary.Should().Be(1_000_000_000_000_000, "> 1e12 阈值本身按毫秒");
    }

    // ---- helpers ----

    private HermesSqliteAdapter CreateAdapter(string configuredHome) => new(configuredHome: configuredHome);

    private string CreateTempHome()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-hermes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private void SetEnvironment(string name, string? value)
    {
        _environmentVariables.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private static string CreateDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Execute(path, """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                source TEXT NOT NULL,
                model TEXT,
                started_at REAL NOT NULL,
                message_count INTEGER DEFAULT 0,
                input_tokens INTEGER DEFAULT 0,
                output_tokens INTEGER DEFAULT 0,
                cache_read_tokens INTEGER DEFAULT 0,
                cache_write_tokens INTEGER DEFAULT 0,
                reasoning_tokens INTEGER DEFAULT 0,
                billing_provider TEXT,
                estimated_cost_usd REAL,
                actual_cost_usd REAL
            )
            """);
        return path;
    }

    private static void InsertSession(string db, SessionFixture fixture)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (
                id, source, model, started_at, message_count,
                input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,
                billing_provider, estimated_cost_usd, actual_cost_usd
            ) VALUES ($id, 'cli', $model, $startedAt, $messageCount, $input, $output, $cacheRead, $cacheWrite, $reasoning, $provider, $estimated, $actual)
            """;
        command.Parameters.AddWithValue("$id", fixture.Id);
        command.Parameters.AddWithValue("$model", fixture.Model);
        command.Parameters.AddWithValue("$startedAt", fixture.StartedAt);
        command.Parameters.AddWithValue("$messageCount", fixture.MessageCount);
        command.Parameters.AddWithValue("$input", fixture.InputTokens);
        command.Parameters.AddWithValue("$output", fixture.OutputTokens);
        command.Parameters.AddWithValue("$cacheRead", fixture.CacheReadTokens);
        command.Parameters.AddWithValue("$cacheWrite", fixture.CacheWriteTokens);
        command.Parameters.AddWithValue("$reasoning", fixture.ReasoningTokens);
        command.Parameters.AddWithValue("$provider", (object?)fixture.Provider ?? DBNull.Value);
        command.Parameters.AddWithValue("$estimated", (object?)fixture.EstimatedCost ?? DBNull.Value);
        command.Parameters.AddWithValue("$actual", (object?)fixture.ActualCost ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertRaw(string db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Execute(string db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>fixture 行参数（与生产 schema 列对应；started_at 以 REAL 秒/毫秒给出）。</summary>
    private sealed record SessionFixture(
        string Id,
        string Model,
        string? Provider,
        double StartedAt,
        long MessageCount,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens,
        long ReasoningTokens,
        double? EstimatedCost,
        double? ActualCost);

    public void Dispose()
    {
        foreach (var name in _environmentVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不构成测试失败（Windows 文件锁等）。
            }
        }
    }
}
