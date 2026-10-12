using System.Globalization;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.GooseSqlite;
using Microsoft.Data.Sqlite;

namespace CCUsage.Infrastructure.Tests.GooseSqlite;

/// <summary>
/// I-012: Goose SQLite 适配器测试（临时库 fixtures，合成数据；语义对照
/// ../ccusage/rust/adapters/goose/ 的 loader.rs / parser.rs / paths.rs 内联测试与实现语义）。
/// </summary>
public class GooseSqliteAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly List<string> _environmentVariables = new();

    /// <summary>TC-001: 蓝本 loads_accumulated_tokens_from_goose_sqlite：accumulated 列优先，total 余量并入 output。</summary>
    [Fact]
    public async Task Loads_accumulated_tokens_from_goose_sqlite()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertSession(db, new SessionFixture(
            Id: "session-a",
            ModelConfig: """{"model_name":"claude-sonnet-4-20250514"}""",
            Provider: "anthropic",
            CreatedAt: "2026-05-01 01:02:03",
            Total: 180, Input: 100, Output: 50));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        var record = result.Records[0];
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 1, 1, 2, 3, TimeSpan.Zero), "19 字符空格分隔时间归一为 UTC");
        record.Agent.Should().Be("Goose");
        record.ProjectPath.Should().Be("Goose");
        record.SessionId.Should().Be("session-a");
        record.MessageId.Should().Be("session-a");
        record.Model.Should().Be("claude-sonnet-4-20250514");
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(80, "reasoning = total 180 − (100+50) = 30，并入 output（I-006 口径）");
        record.CacheCreationTokens.Should().Be(0);
        record.CacheReadTokens.Should().Be(0);
        record.TotalTokens.Should().Be(180);
        record.CostUsd.Should().BeNull("Goose 无逐请求成本，统一走定价引擎");
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 1, 1, 2, 3, TimeSpan.Zero));

        var diagnostics = result.Diagnostics;
        diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        diagnostics.FilesDiscovered.Should().Be(1);
        diagnostics.FilesRead.Should().Be(1);
        diagnostics.MalformedLines.Should().Be(0);
        diagnostics.InvalidRecords.Should().Be(0);
        diagnostics.DuplicateRecords.Should().Be(0);
        diagnostics.MissingCostRecords.Should().Be(1);
    }

    /// <summary>accumulated 列全 0/负值时回退 total/input/output 列（蓝本 read_token_value 的 or 回退链）。</summary>
    [Fact]
    public async Task Falls_back_to_plain_token_columns_when_accumulated_are_not_positive()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertSession(db, new SessionFixture(
            "session-accum-zero", """{"model_name":"gpt-5.1"}""", null,
            "2026-05-01 01:02:03", Total: 0, Input: 0, Output: 0,
            PlainTotal: 45, PlainInput: 40, PlainOutput: 5));
        InsertSession(db, new SessionFixture(
            "session-negative", """{"model_name":"gpt-5.1"}""", null,
            "2026-05-01 01:02:04", Total: -5, Input: -6, Output: -7,
            PlainTotal: 22, PlainInput: 20, PlainOutput: 2));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records.Single(record => record.SessionId == "session-accum-zero")
            .InputTokens.Should().Be(40);
        result.Records.Single(record => record.SessionId == "session-accum-zero")
            .OutputTokens.Should().Be(5);
        result.Records.Single(record => record.SessionId == "session-negative")
            .TotalTokens.Should().Be(22, "负值列按蓝本 read_token_value 不生效（i64 > 0 过滤）");
    }

    /// <summary>tokens 三元组全零行跳过（LinesSkipped）；无 usage 的会话不出记录。</summary>
    [Fact]
    public async Task Skips_all_zero_sessions_as_skipped_rows()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertSession(db, new SessionFixture(
            "session-zero", """{"model_name":"gpt-5.1"}""", null,
            "2026-05-01 01:02:03", Total: 0, Input: 0, Output: 0));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.LinesSkipped.Should().Be(1);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>model_config_json 非法 JSON / 缺 model_name / created_at 不可解析 → 整行作废（InvalidRecords）。</summary>
    [Fact]
    public async Task Rejects_rows_with_unparsable_config_or_timestamp()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertSession(db, new SessionFixture(
            "session-bad-config", "not-json", null, "2026-05-01 01:02:03", Total: 10, Input: 8, Output: 2));
        InsertSession(db, new SessionFixture(
            "session-no-model", """{"temperature":0.7}""", null, "2026-05-01 01:02:04", Total: 10, Input: 8, Output: 2));
        InsertSession(db, new SessionFixture(
            "session-bad-time", """{"model_name":"gpt-5.1"}""", null, "not-a-time", Total: 10, Input: 8, Output: 2));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.InvalidRecords.Should().Be(3);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>
    /// I-024 接线：映射产物带蓝本 calculate_goose_cost / missing_goose_pricing 候选链
    /// （provider 归一化后 ≠ goose 时 [裸名, "{provider}/{model}"]），引擎按记录计价——
    /// 此前自研引擎按裸模型名定价（偏差登记），限定键形状的价目命中依赖裸名巧合。
    /// </summary>
    [Fact]
    public async Task Carries_provider_qualified_pricing_candidates_and_prices_model()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertSession(db, new SessionFixture(
            "session-kimi", """{"model_name":"kimi-k2.6"}""", "moonshot-for-coding",
            "2026-05-01 01:02:03", Total: 1100, Input: 1000, Output: 100));

        var result = await CreateAdapter(root).ScanAsync();
        var record = result.Records.Should().ContainSingle().Subject;

        record.Pricing.Should().NotBeNull();
        record.Pricing!.ModelCandidates.Should().Equal(
            "kimi-k2.6", "moonshot_for_coding/kimi-k2.6"); // 显式 provider '-'→'_'（蓝本 normalize_provider）后拼限定键
        record.Model.Should().Be("kimi-k2.6", "分组/显示仍用裸模型名");

        var priced = new PricingEngine().PriceRecord(record);
        priced.CostUsd.Should().Be(0.001m + 0.000416m, "1000×$1/M + 100×$4.16/M（快照 moonshot/kimi-k2.6，2026-10-12 刷新价）");
        priced.MatchedKey.Should().Be("moonshot/kimi-k2.6");
    }

    /// <summary>I-024 接线：provider 兜底为 goose 时不做限定（蓝本 `provider_id == "goose"` 短路同式），
    /// 未命中行为与接线前一致（Missing + UnpricedModels fail-loud）。</summary>
    [Fact]
    public async Task Skips_provider_qualification_when_provider_falls_back_to_goose()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertSession(db, new SessionFixture(
            "session-mystery", """{"model_name":"mystery-model"}""", null,
            "2026-05-01 01:02:03", Total: 30, Input: 20, Output: 10));

        var result = await CreateAdapter(root).ScanAsync();
        var record = result.Records.Should().ContainSingle().Subject;

        record.Pricing.Should().NotBeNull();
        record.Pricing!.ModelCandidates.Should().Equal("mystery-model"); // 缺省 provider 按模型前缀推断为 goose 兜底 → 不限定

        var engine = new PricingEngine();
        var priced = engine.PriceRecord(record);
        priced.IsPriced.Should().BeFalse();
        engine.GetUnpricedModels().Should().ContainSingle(info => info.Model == "mystery-model");
    }

    /// <summary>时间戳形态：整数秒（×1000）、整数毫秒（>1e12）、RFC3339、纯日期（蓝本 parse_goose_timestamp 全分支）。</summary>
    [Fact]
    public async Task Parses_all_timestamp_shapes()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertSession(db, new SessionFixture(
            "session-epoch-seconds", """{"model_name":"gpt-5.1"}""", null, "1767225600", Total: 3, Input: 2, Output: 1));
        InsertSession(db, new SessionFixture(
            "session-epoch-millis", """{"model_name":"gpt-5.1"}""", null, "1767225600000", Total: 3, Input: 2, Output: 1));
        InsertSession(db, new SessionFixture(
            "session-rfc3339", """{"model_name":"gpt-5.1"}""", null, "2026-01-01T00:00:00Z", Total: 3, Input: 2, Output: 1));
        InsertSession(db, new SessionFixture(
            "session-date-only", """{"model_name":"gpt-5.1"}""", null, "2026-01-02", Total: 3, Input: 2, Output: 1));
        InsertSession(db, new SessionFixture(
            "session-iso-space", """{"model_name":"gpt-5.1"}""", null, "2026-01-03T04:05:06", Total: 3, Input: 2, Output: 1));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Single(record => record.SessionId == "session-epoch-seconds").Timestamp
            .Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        result.Records.Single(record => record.SessionId == "session-epoch-millis").Timestamp
            .Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        result.Records.Single(record => record.SessionId == "session-rfc3339").Timestamp
            .Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        result.Records.Single(record => record.SessionId == "session-date-only").Timestamp
            .Should().Be(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        result.Records.Single(record => record.SessionId == "session-iso-space").Timestamp
            .Should().Be(new DateTimeOffset(2026, 1, 3, 4, 5, 6, TimeSpan.Zero));
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
    }

    /// <summary>created_at 为整数存储列：i64 → 文本 → 秒/毫秒判别（蓝本 read_timestamp_value 的 i64 分支）。</summary>
    [Fact]
    public async Task Reads_integer_created_at_column()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertRaw(db, """
            INSERT INTO sessions (id, model_config_json, provider_name, created_at, accumulated_total_tokens, accumulated_input_tokens, accumulated_output_tokens)
            VALUES ('session-int-time', '{"model_name":"gpt-5.1"}', NULL, 1767225600, 3, 2, 1)
            """);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Subject.Timestamp
            .Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>model_config_json 为 NULL / 空白的行在 SQL 层排除（蓝本 WHERE 子句），不计入诊断。</summary>
    [Fact]
    public async Task Excludes_blank_model_config_rows_in_sql()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertRaw(db, "INSERT INTO sessions (id, model_config_json, created_at, accumulated_total_tokens, accumulated_input_tokens, accumulated_output_tokens) VALUES ('session-null-config', NULL, '2026-05-01 01:02:03', 10, 8, 2)");
        InsertRaw(db, "INSERT INTO sessions (id, model_config_json, created_at, accumulated_total_tokens, accumulated_input_tokens, accumulated_output_tokens) VALUES ('session-blank-config', '   ', '2026-05-01 01:02:03', 10, 8, 2)");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.InvalidRecords.Should().Be(0);
        result.Diagnostics.FilesRead.Should().Be(1);
    }

    /// <summary>schema 不支持（无 sessions 表）→ 打开成功但 prepare 失败 → 0 条 + Partial 降级，不抛异常。</summary>
    [Fact]
    public async Task Database_without_sessions_table_degrades_to_no_data()
    {
        var root = CreateTempDir();
        var db = Path.Combine(root, "data", "sessions", "sessions.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (id TEXT)";
            command.ExecuteNonQuery();
        }

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.FilesDiscovered.Should().Be(1);
        result.Diagnostics.FilesRead.Should().Be(0);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>多库：双默认根各一库，双库记录都保留（去重键含库路径，蓝本键形状）。</summary>
    [Fact]
    public async Task Reads_multiple_databases_keeping_same_session_id_across_paths()
    {
        var home = CreateTempDir();
        var dbA = CreateDatabase(Path.Combine(home, ".local", "share", "goose", "sessions", "sessions.db"));
        var dbB = CreateDatabase(Path.Combine(home, ".local", "share", "Block", "goose", "sessions", "sessions.db"));
        InsertSession(dbA, new SessionFixture(
            "shared-session", """{"model_name":"gpt-5.1"}""", null, "2026-05-01 01:02:03", Total: 12, Input: 10, Output: 2));
        InsertSession(dbB, new SessionFixture(
            "shared-session", """{"model_name":"gpt-5.1"}""", null, "2026-05-01 01:02:04", Total: 24, Input: 20, Output: 4));
        SetEnvironment("USERPROFILE", home);
        SetEnvironment("HOME", null);
        SetEnvironment("GOOSE_PATH_ROOT", null);

        var result = await CreateAdapter(null).ScanAsync();

        result.Records.Should().HaveCount(2, "去重键为 (库路径, session id)，不同库的同名会话都保留（蓝本键形状）");
        result.Records.Select(record => record.TotalTokens).Should().BeEquivalentTo([12, 24]);
    }

    /// <summary>配置根优先于 GOOSE_PATH_ROOT（与 ZCode 配置根同语义；蓝本为单根，无逗号分隔）。</summary>
    [Fact]
    public async Task Configured_root_takes_priority_over_environment_variable()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertSession(db, new SessionFixture(
            "session-a", """{"model_name":"gpt-5.1"}""", null, "2026-05-01 01:02:03", Total: 12, Input: 10, Output: 2));
        var fromEnvironment = CreateTempDir();
        SetEnvironment("GOOSE_PATH_ROOT", fromEnvironment);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.FilesDiscovered.Should().Be(1);
    }

    /// <summary>记录按时间戳升序（跨库汇总后仍有序）。</summary>
    [Fact]
    public async Task Sorts_records_by_timestamp()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertSession(db, new SessionFixture(
            "session-late", """{"model_name":"gpt-5.1"}""", null, "2026-05-02 01:02:03", Total: 12, Input: 10, Output: 2));
        InsertSession(db, new SessionFixture(
            "session-early", """{"model_name":"gpt-5.1"}""", null, "2026-05-01 01:02:03", Total: 12, Input: 10, Output: 2));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Select(record => record.SessionId).Should().ContainInOrder("session-early", "session-late");
    }

    /// <summary>WAL 兼容：写连接持未提交事务时，只读连接仍能读到已提交行（共享 SqliteReadonly 语义）。</summary>
    [Fact]
    public async Task Reads_committed_rows_from_wal_database_while_writer_holds_transaction()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        var writer = new SqliteConnection($"Data Source={db}");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL;";
            command.ExecuteScalar();
        }

        InsertSession(db, new SessionFixture(
            "session-committed", """{"model_name":"gpt-5.1"}""", null, "2026-05-01 01:02:03", Total: 12, Input: 10, Output: 2));
        using var transaction = writer.BeginTransaction();
        using (var pending = writer.CreateCommand())
        {
            pending.Transaction = transaction;
            pending.CommandText = "INSERT INTO sessions (id, model_config_json, created_at, accumulated_total_tokens, accumulated_input_tokens, accumulated_output_tokens) VALUES ('session-pending', '{\"model_name\":\"gpt-5.1\"}', '2026-05-01 01:02:04', 5, 4, 1)";
            pending.ExecuteNonQuery();
        }

        try
        {
            var result = await CreateAdapter(root).ScanAsync();

            result.Records.Should().ContainSingle().Subject.SessionId.Should().Be("session-committed");
        }
        finally
        {
            transaction.Rollback();
            writer.Dispose();
        }
    }

    // ---- 定位器（蓝本 paths.rs 测试 + 默认三候选）----

    [Fact]
    public void Discovers_goose_path_root_database()
    {
        var root = CreateTempDir();
        CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        SetEnvironment("GOOSE_PATH_ROOT", root);

        new GooseLogLocator().ResolveDatabases(null)
            .Should().ContainSingle()
            .Which.Should().EndWith(Path.Combine("data", "sessions", "sessions.db"));
    }

    [Fact]
    public void Empty_goose_path_root_falls_back_to_default_candidates()
    {
        var home = CreateTempDir();
        var defaultDb = CreateDatabase(Path.Combine(home, ".local", "share", "goose", "sessions", "sessions.db"));
        SetEnvironment("GOOSE_PATH_ROOT", "   ");
        SetEnvironment("USERPROFILE", home);
        SetEnvironment("HOME", null);

        new GooseLogLocator().ResolveDatabases(null).Should().BeEquivalentTo([defaultDb]);
    }

    [Fact]
    public void Probes_all_three_default_candidates_in_order()
    {
        var home = CreateTempDir();
        var unixDb = CreateDatabase(Path.Combine(home, ".local", "share", "goose", "sessions", "sessions.db"));
        var macDb = CreateDatabase(Path.Combine(home, "Library", "Application Support", "goose", "sessions", "sessions.db"));
        var blockDb = CreateDatabase(Path.Combine(home, ".local", "share", "Block", "goose", "sessions", "sessions.db"));
        SetEnvironment("USERPROFILE", home);
        SetEnvironment("HOME", null);

        new GooseLogLocator().ResolveDatabases(null)
            .Should().BeEquivalentTo([unixDb, macDb, blockDb], "双默认根 + Block 根三候选按序全探测");
    }

    [Fact]
    public async Task Missing_home_and_databases_yield_no_data()
    {
        SetEnvironment("USERPROFILE", null);
        SetEnvironment("HOME", null);
        SetEnvironment("GOOSE_PATH_ROOT", null);

        new GooseLogLocator().ResolveDatabases(null).Should().BeEmpty();

        var root = CreateTempDir();
        var result = await CreateAdapter(root).ScanAsync();
        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    // ---- 映射器纯函数（蓝本 parser.rs 无内联测试，按实现语义补齐）----

    [Fact]
    public void Normalizes_provider_by_name_then_model_prefix()
    {
        GooseSessionMapper.NormalizeProvider("github-copilot", "claude-sonnet-4-5").Should().Be("github_copilot");
        GooseSessionMapper.NormalizeProvider("  anthropic  ", "gpt-5.1").Should().Be("anthropic");
        GooseSessionMapper.NormalizeProvider(null, "claude-sonnet-4-5").Should().Be("anthropic");
        GooseSessionMapper.NormalizeProvider(null, "gpt-5.1").Should().Be("openai");
        GooseSessionMapper.NormalizeProvider(null, "chatgpt-x").Should().Be("openai");
        GooseSessionMapper.NormalizeProvider(null, "o3").Should().Be("openai");
        GooseSessionMapper.NormalizeProvider(null, "gemini-3-pro").Should().Be("google");
        GooseSessionMapper.NormalizeProvider(null, "Qwen3-Coder").Should().Be("openrouter");
        GooseSessionMapper.NormalizeProvider(null, "llama-4").Should().Be("goose");
    }

    [Fact]
    public void Rejects_invalid_epoch_values()
    {
        // 蓝 本分支：> 1e12 直接按毫秒用（无 ×1000、无上限门槛——越界由 Map 的防御边界拒绝）。
        GooseSessionMapper.TryParseTimestamp(long.MaxValue.ToString(CultureInfo.InvariantCulture), out _)
            .Should().BeTrue("蓝本对 > 1e12 的整数原样按毫秒取用，越界由 Map 层 TimestampOutOfRange 兜底");
        GooseSessionMapper.TryParseTimestamp("99999999999999999999", out _)
            .Should().BeFalse("超出整数域且不是可解析时间文本");
        GooseSessionMapper.TryParseTimestamp("0", out _).Should().BeFalse("millis 须 > 0");
        GooseSessionMapper.TryParseTimestamp("-5", out _).Should().BeFalse();
        GooseSessionMapper.TryParseTimestamp("   ", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_timestamp_beyond_datetimeoffset_range()
    {
        var root = CreateTempDir();
        var db = CreateDatabase(Path.Combine(root, "data", "sessions", "sessions.db"));
        InsertSession(db, new SessionFixture(
            "session-far-future", """{"model_name":"gpt-5.1"}""", null, "253402300800000", Total: 12, Input: 10, Output: 2));
        InsertSession(db, new SessionFixture(
            "session-ok", """{"model_name":"gpt-5.1"}""", null, "2026-05-01 01:02:03", Total: 12, Input: 10, Output: 2));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Subject.SessionId.Should().Be("session-ok");
        result.Diagnostics.InvalidRecords.Should().Be(1, "越界毫秒按行级无效处理（I-006 防御边界），不击穿扫描");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    // ---- helpers ----

    private GooseSqliteAdapter CreateAdapter(string? configuredRoot) => new(configuredRoot: configuredRoot);

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-goose-{Guid.NewGuid():N}");
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
                model_config_json TEXT,
                provider_name TEXT,
                created_at TEXT,
                total_tokens INTEGER,
                input_tokens INTEGER,
                output_tokens INTEGER,
                accumulated_total_tokens INTEGER,
                accumulated_input_tokens INTEGER,
                accumulated_output_tokens INTEGER
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
                id, model_config_json, provider_name, created_at,
                total_tokens, input_tokens, output_tokens,
                accumulated_total_tokens, accumulated_input_tokens, accumulated_output_tokens
            ) VALUES ($id, $modelConfig, $provider, $createdAt, $plainTotal, $plainInput, $plainOutput, $total, $input, $output)
            """;
        command.Parameters.AddWithValue("$id", fixture.Id);
        command.Parameters.AddWithValue("$modelConfig", fixture.ModelConfig);
        command.Parameters.AddWithValue("$provider", (object?)fixture.Provider ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", fixture.CreatedAt);
        command.Parameters.AddWithValue("$plainTotal", fixture.PlainTotal);
        command.Parameters.AddWithValue("$plainInput", fixture.PlainInput);
        command.Parameters.AddWithValue("$plainOutput", fixture.PlainOutput);
        command.Parameters.AddWithValue("$total", fixture.Total);
        command.Parameters.AddWithValue("$input", fixture.Input);
        command.Parameters.AddWithValue("$output", fixture.Output);
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

    /// <summary>fixture 行参数（Plain* 为 total/input/output 列的对照组，默认给不同值以证明 accumulated 优先）。</summary>
    private sealed record SessionFixture(
        string Id,
        string ModelConfig,
        string? Provider,
        string CreatedAt,
        long Total,
        long Input,
        long Output,
        long PlainTotal = -1,
        long PlainInput = -1,
        long PlainOutput = -1);

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
