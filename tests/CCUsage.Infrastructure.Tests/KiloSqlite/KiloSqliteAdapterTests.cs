using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.KiloSqlite;
using Microsoft.Data.Sqlite;

namespace CCUsage.Infrastructure.Tests.KiloSqlite;

/// <summary>
/// I-017: Kilo SQLite 适配器测试（临时库 fixtures，合成数据；语义对照
/// ../ccusage/rust/adapters/kilo/ 的 loader.rs / parser.rs / paths.rs 内联测试，同名场景逐一对齐）。
/// </summary>
public class KiloSqliteAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly List<string> _environmentVariables = new();

    /// <summary>TC-001: 蓝本 loads_kilo_messages_from_sqlite：五桶 + reasoning 并入 + stored cost 直采。</summary>
    [Fact]
    public async Task Loads_kilo_messages_from_sqlite()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-1", "session-a", """
            {"id":"msg-1","role":"assistant","providerID":"anthropic","modelID":"claude-sonnet-4-20250514","time":{"created":1767312000000},"tokens":{"input":100,"output":50,"reasoning":5,"cache":{"read":10,"write":20}},"cost":0.02,"agent":"build"}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().ContainSingle();
        var record = result.Records[0];
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        record.Agent.Should().Be("Kilo");
        record.ProjectPath.Should().Be("Kilo");
        record.SessionId.Should().Be("session-a");
        record.MessageId.Should().Be("msg-1", "载荷内嵌 id 优先于列值");
        record.Model.Should().Be("claude-sonnet-4-20250514");
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(55, "reasoning = 5 并入 output（I-006 口径）");
        record.CacheCreationTokens.Should().Be(20);
        record.CacheReadTokens.Should().Be(10);
        record.CostUsd.Should().Be(0.02m, "载荷 cost 直采（蓝本 Auto 模式）");

        var diagnostics = result.Diagnostics;
        diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        diagnostics.FilesDiscovered.Should().Be(1);
        diagnostics.FilesRead.Should().Be(1);
        diagnostics.MalformedLines.Should().Be(0);
        diagnostics.InvalidRecords.Should().Be(0);
        diagnostics.DuplicateRecords.Should().Be(0);
    }

    /// <summary>I-024 接线：providerID 参与蓝本 model_candidates 候选链（归一化后非 unknown/kilo 且
    /// 精确命中时限定名生效，门槛由引擎匹配承载）；分组/显示仍用 modelID。</summary>
    [Fact]
    public async Task Carries_provider_qualified_candidates_and_prices_model()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-kimi", "session-a", """
            {"id":"msg-kimi","role":"assistant","providerID":"moonshot","modelID":"kimi-k2.6","time":{"created":1767312000000},"tokens":{"input":1000,"output":100},"agent":"build"}
            """);

        var result = await CreateAdapter(directory).ScanAsync();
        var record = result.Records.Should().ContainSingle().Subject;

        record.Pricing.Should().NotBeNull();
        record.Pricing!.ModelCandidates.Should().Equal("moonshot/kimi-k2.6", "kimi-k2.6");
        record.Model.Should().Be("kimi-k2.6", "分组/显示仍用 modelID");

        var priced = new PricingEngine().PriceRecord(record);
        priced.CostUsd.Should().Be(0.001m + 0.000416m, "1000×$1/M + 100×$4.16/M（快照 moonshot/kimi-k2.6，2026-10-12 刷新价）");
        priced.MatchedKey.Should().Be("moonshot/kimi-k2.6");
    }

    /// <summary>I-024 接线：providerID 缺失 / 为 kilo（兜底）时不限定（蓝本 unknown/kilo 过滤同式）。</summary>
    [Fact]
    public async Task Skips_qualification_for_missing_or_kilo_provider()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-no-provider", "session-a", """
            {"id":"msg-a","role":"assistant","modelID":"mystery-a","time":{"created":1767312000000},"tokens":{"input":10},"agent":"build"}
            """);
        InsertMessage(db, "row-kilo-provider", "session-a", """
            {"id":"msg-b","role":"assistant","providerID":"kilo","modelID":"mystery-b","time":{"created":1767312000001},"tokens":{"input":10},"agent":"build"}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records.Single(record => record.Model == "mystery-a").Pricing!.ModelCandidates
            .Should().Equal("mystery-a"); // providerID 缺失 → 不限定
        result.Records.Single(record => record.Model == "mystery-b").Pricing!.ModelCandidates
            .Should().Equal("mystery-b"); // providerID kilo 兜底 → 不限定
    }

    /// <summary>蓝本 ignores_kilo_messages_without_timestamps：有 tokens 但无 time.created → 整行不产出。</summary>
    [Fact]
    public async Task Ignores_messages_without_timestamps()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-1", "session-a", """
            {"role":"assistant","providerID":"openai","modelID":"gpt-5","tokens":{"input":1,"output":1,"cache":{"read":0,"write":0}}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.InvalidRecords.Should().Be(1, "有 usage 载荷但时间不可用按 invalid 计（蓝本静默跳过）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>非 assistant 行（含缺 role）按 skipped 计，不出记录。</summary>
    [Fact]
    public async Task Skips_non_assistant_rows()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-user", "session-a", """
            {"id":"msg-u","role":"user","sessionID":"session-a","time":{"created":1767312000000},"text":"hello"}
            """);
        InsertMessage(db, "row-no-role", "session-a", """
            {"id":"msg-n","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000001},"tokens":{"input":5,"output":1}}
            """);
        InsertRaw(db, "INSERT INTO message (id, session_id, data) VALUES ('row-space-role', 'session-a', '{\"id\":\"m\",\"role\":\" assistant \",\"modelID\":\"gpt-5\",\"time\":{\"created\":1767312000002},\"tokens\":{\"input\":5,\"output\":1}}')");

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.LinesSkipped.Should().Be(3, "role 原样比较（不 trim），空格角色行同样不产出");
    }

    /// <summary>蓝本 keeps_kilo_record_when_cache_field_is_not_an_object：cache 非对象按缺失（两桶 0），行保留。</summary>
    [Fact]
    public async Task Keeps_record_when_cache_field_is_not_an_object()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-1", "session-a", """
            {"id":"msg-1","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"input":100,"output":10,"cache":0}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(10);
        record.CacheCreationTokens.Should().Be(0);
        record.CacheReadTokens.Should().Be(0);
    }

    /// <summary>蓝本 falls_back_to_total_tokens_when_kilo_parts_are_missing：分量缺失时 total 补入 output。</summary>
    [Fact]
    public async Task Falls_back_to_total_tokens_when_parts_are_missing()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-1", "session-a", """
            {"id":"msg-1","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"total":234}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(234);
        record.TotalTokens.Should().Be(234);
        record.CostUsd.Should().BeNull("cost 缺失 → 定价引擎");
    }

    /// <summary>time.created ≤ 0 与秒级时间戳（<1e12 ×1000，蓝本 normalize_timestamp 全分支）。</summary>
    [Fact]
    public async Task Normalizes_timestamp_scales_and_rejects_non_positive()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-sec", "s1", """
            {"id":"m1","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000},"tokens":{"input":5,"output":1}}
            """);
        InsertMessage(db, "row-zero", "s1", """
            {"id":"m2","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":0},"tokens":{"input":5,"output":1}}
            """);
        InsertMessage(db, "row-neg", "s1", """
            {"id":"m3","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":-5},"tokens":{"input":5,"output":1}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().ContainSingle().Subject.Timestamp
            .Should().Be(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "秒级 ×1000");
        result.Diagnostics.InvalidRecords.Should().Be(2, "≤ 0 的 created 按蓝本 normalize_timestamp 静默跳过，此处计 invalid");
    }

    /// <summary>session id / message id 的列值回退与 "&lt;db&gt;:&lt;row&gt;" message id 兜底（蓝本同式）。</summary>
    [Fact]
    public async Task Falls_back_to_row_ids_when_payload_ids_missing()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-9", "session-col", """
            {"role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"input":5,"output":1}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SessionId.Should().Be("session-col", "载荷无 sessionID → 列值");
        record.MessageId.Should().Be($"{db}:row-9", "载荷无 id → <db 路径>:<row id>（蓝本同式）");
    }

    /// <summary>行级诊断：坏 JSON 计 malformed；有载荷缺 modelID 计 invalid；坏库降级 Partial。</summary>
    [Fact]
    public async Task Counts_row_level_rejections_and_degrades_on_bad_database()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "kilo.db");
        CreateDatabase(db);
        InsertRaw(db, "INSERT INTO message (id, session_id, data) VALUES ('row-bad', 's', '{not-json')");
        InsertRaw(db, "INSERT INTO message (id, session_id, data) VALUES ('row-no-model', 's', '{\"role\":\"assistant\",\"time\":{\"created\":1767312000000},\"tokens\":{\"input\":5,\"output\":1}}')");
        InsertRaw(db, "INSERT INTO message (id, session_id, data) VALUES (NULL, 's', '{\"role\":\"assistant\",\"modelID\":\"gpt-5\",\"time\":{\"created\":1767312000000},\"tokens\":{\"input\":5,\"output\":1}}')");
        InsertMessage(db, "row-ok", "s", """
            {"id":"m","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"input":5,"output":1}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().ContainSingle().Subject.MessageId.Should().Be("m");
        result.Diagnostics.MalformedLines.Should().Be(1);
        result.Diagnostics.InvalidRecords.Should().Be(2);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);

        var notADatabase = Path.Combine(CreateTempDir(), "kilo.db");
        File.WriteAllText(notADatabase, "this is not a sqlite database");
        var badResult = await CreateAdapter(Path.GetDirectoryName(notADatabase)!).ScanAsync();
        badResult.Diagnostics.FilesDiscovered.Should().Be(1);
        badResult.Diagnostics.FilesRead.Should().Be(0);
        badResult.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>蓝本 deduplicates_kilo_messages_across_data_dirs：跨目录同 message id 只留先配置目录的行。</summary>
    [Fact]
    public async Task Deduplicates_messages_across_data_dirs_keeping_first()
    {
        var first = CreateTempDir();
        var second = CreateTempDir();
        var dbA = CreateDatabase(Path.Combine(first, "kilo.db"));
        var dbB = CreateDatabase(Path.Combine(second, "kilo.db"));
        InsertMessage(dbA, "row-1", "session-a", """
            {"id":"embedded-msg-1","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"input":10,"output":1,"cache":{"read":0,"write":0}}}
            """);
        InsertMessage(dbB, "row-1", "session-a", """
            {"id":"embedded-msg-1","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"input":20,"output":1,"cache":{"read":0,"write":0}}}
            """);

        var result = await CreateAdapter($"{first},{second}").ScanAsync();

        result.Records.Should().ContainSingle().Subject.InputTokens.Should().Be(10, "先配置目录的行保留（蓝本 seen 顺序）");
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    /// <summary>去重发生在映射成功之后：首现行无 usage 载荷不占去重名额，另一目录的有效行保留。</summary>
    [Fact]
    public async Task Dedup_happens_after_mapping_so_valid_row_survives_payload_less_first_occurrence()
    {
        var first = CreateTempDir();
        var second = CreateTempDir();
        CreateDatabase(Path.Combine(first, "kilo.db"));
        InsertMessage(Path.Combine(first, "kilo.db"), "row-1", "session-a", """
            {"id":"shared","role":"user","time":{"created":1767312000000},"text":"no tokens"}
            """);
        var dbB = CreateDatabase(Path.Combine(second, "kilo.db"));
        InsertMessage(dbB, "row-1", "session-b", """
            {"id":"shared","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"input":20,"output":1}}
            """);

        var result = await CreateAdapter($"{first},{second}").ScanAsync();

        result.Records.Should().ContainSingle().Subject.SessionId.Should().Be("session-b");
        result.Diagnostics.DuplicateRecords.Should().Be(0);
        result.Diagnostics.LinesSkipped.Should().Be(1);
    }

    /// <summary>记录按时间戳升序（跨库汇总后仍有序）。</summary>
    [Fact]
    public async Task Sorts_records_by_timestamp()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-late", "s", """
            {"id":"late","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312900000},"tokens":{"input":5,"output":1}}
            """);
        InsertMessage(db, "row-early", "s", """
            {"id":"early","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"input":5,"output":1}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Select(record => record.MessageId).Should().ContainInOrder("early", "late");
    }

    /// <summary>WAL 兼容：写连接持未提交事务时，只读连接仍能读到已提交行（共享 SqliteReadonly 语义）。</summary>
    [Fact]
    public async Task Reads_committed_rows_from_wal_database_while_writer_holds_transaction()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        var writer = new SqliteConnection($"Data Source={db}");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL;";
            command.ExecuteScalar();
        }

        InsertMessage(db, "row-1", "session-a", """
            {"id":"committed","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"input":5,"output":1}}
            """);
        using var transaction = writer.BeginTransaction();
        using (var pending = writer.CreateCommand())
        {
            pending.Transaction = transaction;
            pending.CommandText = "INSERT INTO message (id, session_id, data) VALUES ('row-2', 'session-a', '{\"id\":\"pending\",\"role\":\"assistant\",\"providerID\":\"openai\",\"modelID\":\"gpt-5\",\"time\":{\"created\":1767312000001},\"tokens\":{\"input\":5,\"output\":1}}')";
            pending.ExecuteNonQuery();
        }

        try
        {
            var result = await CreateAdapter(directory).ScanAsync();

            result.Records.Should().ContainSingle().Subject.MessageId.Should().Be("committed");
        }
        finally
        {
            transaction.Rollback();
            writer.Dispose();
        }
    }

    // ---- 定位器（蓝本 paths.rs 口径）----

    [Fact]
    public void Kilo_data_dir_environment_variable_is_respected()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(root);
        SetEnvironment("KILO_DATA_DIR", root);

        new KiloDataLocator().ResolveDataDirectories(null).Should().BeEquivalentTo([root]);
    }

    [Fact]
    public void Keeps_configured_data_dirs_and_deduplicates()
    {
        var root = CreateTempDir();
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        var raw = $" {first}, {second}, {first}, {Path.Combine(root, "missing")} ";
        new KiloDataLocator().ResolveDataDirectories(raw)
            .Should().BeEquivalentTo([first, second], "去重且跳过不存在的目录");
    }

    [Fact]
    public void Does_not_fall_back_when_configured_data_dir_is_empty_or_missing()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, ".local", "share", "kilo"));
        SetEnvironment("USERPROFILE", root);
        SetEnvironment("HOME", null);

        new KiloDataLocator().ResolveDataDirectories(string.Empty).Should().BeEmpty();
        new KiloDataLocator().ResolveDataDirectories(Path.Combine(root, "missing")).Should().BeEmpty();
    }

    [Fact]
    public void Falls_back_to_local_share_kilo_under_home()
    {
        var root = CreateTempDir();
        var defaultDir = Path.Combine(root, ".local", "share", "kilo");
        Directory.CreateDirectory(defaultDir);
        SetEnvironment("USERPROFILE", root);
        SetEnvironment("HOME", null);
        SetEnvironment("KILO_DATA_DIR", null);

        new KiloDataLocator().ResolveDataDirectories(null).Should().BeEquivalentTo([defaultDir]);
    }

    [Fact]
    public async Task Missing_default_directory_yields_no_data()
    {
        var root = CreateTempDir();
        SetEnvironment("USERPROFILE", root);
        SetEnvironment("HOME", null);
        SetEnvironment("KILO_DATA_DIR", null);

        new KiloDataLocator().ResolveDataDirectories(null).Should().BeEmpty();

        var result = await CreateAdapter(root).ScanAsync();
        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    // ---- 映射器纯函数（蓝本 parser.rs 内联测试的候选部分）----

    /// <summary>蓝本 uses_exact_provider_qualified_pricing_when_available：qualified exact 命中时插在最前。</summary>
    [Fact]
    public void Includes_provider_qualified_candidate_when_exact()
    {
        KiloMessageParser.BuildModelCandidates("deepseek-v4-flash", "deepseek", _ => true)
            .Should().Equal("deepseek/deepseek-v4-flash", "deepseek-v4-flash");
    }

    /// <summary>蓝本 uses_raw_model_for_timestamped_pricing_when_provider_match_is_not_exact：qualified 非
    /// exact 时不插入；unknown/kilo 兜底 provider 不插限定名。</summary>
    [Fact]
    public void Uses_raw_model_when_provider_match_is_not_exact()
    {
        KiloMessageParser.BuildModelCandidates("deepseek-v4-flash", "deepseek", _ => false)
            .Should().Equal("deepseek-v4-flash");
        KiloMessageParser.BuildModelCandidates("m", "unknown", _ => true)
            .Should().Equal("m");
        KiloMessageParser.BuildModelCandidates("m", "kilo", _ => true)
            .Should().Equal("m");
        KiloMessageParser.BuildModelCandidates("m", "github-copilot", _ => true)
            .Should().Equal(new[] { "github_copilot/m", "m" }, "provider '-'→'_' 归一化");
    }

    // ---- 计价（蓝本 calculate_kilo_cost_from_tokens 的 Auto 语义）----

    /// <summary>stored cost 0.0 直采记 0（蓝本 Auto 对 Some(0.0) 不再计价——与 OpenCode 的 &gt;0 过滤不同）；
    /// cost 缺失走定价引擎（内置快照 gpt-5 命中）。</summary>
    [Fact]
    public async Task Stored_zero_cost_records_zero_and_missing_cost_goes_to_engine()
    {
        var directory = CreateTempDir();
        var db = CreateDatabase(Path.Combine(directory, "kilo.db"));
        InsertMessage(db, "row-zero", "s1", """
            {"id":"z","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000000},"tokens":{"input":100,"output":10},"cost":0}
            """);
        InsertMessage(db, "row-none", "s1", """
            {"id":"n","role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1767312000001},"tokens":{"input":100,"output":10}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();
        var engine = new PricingEngine();

        var zero = result.Records.Single(record => record.MessageId == "z");
        zero.CostUsd.Should().Be(0m, "蓝本 Auto：Some(0.0) 记 0 不再计价");
        engine.PriceRecord(zero).CostUsd.Should().Be(0m);
        var none = result.Records.Single(record => record.MessageId == "n");
        none.CostUsd.Should().BeNull();
        engine.PriceRecord(none).CostUsd.Should().BeGreaterThan(0m, "gpt-5 命中内置快照");
    }

    // ---- helpers ----

    private KiloSqliteAdapter CreateAdapter(string configuredDataDir) => new(configuredDataDir: configuredDataDir);

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-kilo-{Guid.NewGuid():N}");
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
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE message (id TEXT, session_id TEXT, data TEXT)";
        command.ExecuteNonQuery();
        return path;
    }

    private static void InsertMessage(string db, string id, string sessionId, string data)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO message (id, session_id, data) VALUES ($id, $sessionId, $data)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$data", data);
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
