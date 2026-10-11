namespace CCUsage.Infrastructure.Tests.ZCodeSqlite;

using CCUsage.Core.Models;
using CCUsage.Infrastructure.ZCodeSqlite;
using Microsoft.Data.Sqlite;

/// <summary>
/// I-006: ZCode SQLite 适配器测试（临时库 fixtures，合成数据；语义对照
/// ../ccusage/rust/adapters/zcode/ 的 parser.rs / loader.rs 内联测试）。
/// </summary>
public class ZCodeSqliteAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    /// <summary>TC-001: 双缓存桶含在 input_tokens 内的归一化（对照蓝本 normalizes_both_inclusive_cache_buckets）。</summary>
    [Fact]
    public async Task Normalizes_inclusive_cache_buckets()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(home, legacy: false);
        InsertRow(db, new ZCodeRow(
            Id: "usage-1", SessionId: "session-1", StartedAt: 1_786_909_042_666, ModelId: "GLM-5.3",
            ProviderId: "e8b95b3c-35be-46ab-ae7c-035d7f37890e", Status: "completed", InputTokens: 100, OutputTokens: 10,
            CacheCreation: 15, CacheRead: 25, ComputedTotal: 110, Directory: "/workspace/project"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        var record = result.Records[0];
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 8, 16, 19, 37, 22, 666, TimeSpan.Zero));
        record.Agent.Should().Be("ZCode");
        record.ProjectPath.Should().Be("/workspace/project");
        record.SessionId.Should().Be("session-1");
        record.Model.Should().Be("GLM-5.3");
        record.MessageId.Should().Be("usage-1");
        // input 100 = 60 新内容 + 15 cache_creation + 25 cache_read，逐桶拆分不重复计数。
        record.InputTokens.Should().Be(60);
        record.CacheCreationTokens.Should().Be(15);
        record.CacheReadTokens.Should().Be(25);
        record.OutputTokens.Should().Be(10);
        record.TotalTokens.Should().Be(110);
        // 自定义 provider：不走定价引擎，CostUsd 记 0（与 CLI missingPricing/0 口径一致）。
        record.CostUsd.Should().Be(0m);

        var diagnostics = result.Diagnostics;
        diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        diagnostics.FilesDiscovered.Should().Be(1);
        diagnostics.FilesRead.Should().Be(1);
        diagnostics.InvalidRecords.Should().Be(0);
        diagnostics.DuplicateRecords.Should().Be(0);
        diagnostics.MissingCostRecords.Should().Be(0);
        diagnostics.UnknownModelRecords.Should().Be(0);
    }

    /// <summary>Z.ai 行把 cache_creation 并入 input（按标准 input 价计费，总量不变）。</summary>
    [Fact]
    public async Task Zai_provider_rows_fold_cache_creation_into_input()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(home, legacy: false);
        InsertRow(db, new ZCodeRow(
            "usage-zai", "session-1", 1_786_909_042_666, "GLM-5.3", "account:zai-start-plan",
            "completed", 100, 10, 15, 25, 110, "/workspace/project"));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Single();
        record.InputTokens.Should().Be(75); // 60 新内容 + 15 cache_creation
        record.CacheCreationTokens.Should().Be(0);
        record.CacheReadTokens.Should().Be(25);
        record.OutputTokens.Should().Be(10);
        record.TotalTokens.Should().Be(110); // 总量守恒
        record.CostUsd.Should().BeNull();
    }

    /// <summary>无 provider 的 legacy glm-* 行按 Z.ai 处理；非 glm 行不是 Z.ai。</summary>
    [Fact]
    public async Task Legacy_rows_without_provider_use_model_name_for_zai_gate()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(home, legacy: false);
        InsertRow(db, new ZCodeRow(
            "usage-glm", "s1", 1_786_909_042_666, "glm-5.3-flash", null,
            "completed", 40, 5, 0, 0, 45, "/w"));
        InsertRow(db, new ZCodeRow(
            "usage-ark", "s1", 1_786_909_043_000, "ark-code-latest", null,
            "completed", 40, 5, 0, 0, 45, "/w"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records.Single(record => record.Model == "glm-5.3-flash").CostUsd.Should().BeNull();
        result.Records.Single(record => record.Model == "ark-code-latest").CostUsd.Should().Be(0m);
        result.Diagnostics.MissingCostRecords.Should().Be(1);
    }

    /// <summary>自定义 provider（内部 UUID 等）：CostUsd 记 0，与发布版 CLI 的 missingPricing/0 一致。</summary>
    [Fact]
    public async Task Custom_provider_rows_record_zero_cost()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(home, legacy: false);
        InsertRow(db, new ZCodeRow(
            "usage-custom", "s1", 1_786_909_042_666, "glm-5.3-flash", "e8b95b3c-35be-46ab-ae7c-035d7f37890e",
            "completed", 40, 5, 0, 0, 45, "/w"));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Single();
        record.CostUsd.Should().Be(0m);
        // 非 Z.ai 不做 cache_creation 并入（本行无 creation，验证 input 不变即可）。
        record.InputTokens.Should().Be(40);
        result.Diagnostics.MissingCostRecords.Should().Be(0);
    }

    /// <summary>缓存桶截断到 input、computed_total 余量并入 output（对照蓝本 clamp 与 extra tokens 测试）。</summary>
    [Fact]
    public async Task Clamps_cache_buckets_and_folds_extra_total()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(home, legacy: false);
        // input=100：cacheRead 80 → 剩 20；cacheCreation 50 → 截到 20；input=0。
        // 分量和 = 0 + 10 + 20 + 80 = 110；computed 150 → 余量 40 并入 output。
        InsertRow(db, new ZCodeRow(
            "usage-clamp", "s1", 1_786_909_042_666, "some-model", "custom",
            "completed", 100, 10, 50, 80, 150, "/w"));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Single();
        record.InputTokens.Should().Be(0);
        record.CacheReadTokens.Should().Be(80);
        record.CacheCreationTokens.Should().Be(20);
        record.OutputTokens.Should().Be(50); // 10 + 40 余量
        record.TotalTokens.Should().Be(150);
    }

    /// <summary>仅 status='completed' 的行参与统计。</summary>
    [Fact]
    public async Task Filters_non_completed_rows()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(home, legacy: false);
        InsertRow(db, new ZCodeRow(
            "usage-run", "s1", 1_786_909_042_666, "m", "p", "running", 10, 1, 0, 0, 11, "/w"));
        InsertRow(db, new ZCodeRow(
            "usage-err", "s1", 1_786_909_042_667, "m", "p", "error", 10, 1, 0, 0, 11, "/w"));
        InsertRow(db, new ZCodeRow(
            "usage-ok", "s1", 1_786_909_042_668, "m", "p", "completed", 10, 1, 0, 0, 11, "/w"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].MessageId.Should().Be("usage-ok");
    }

    /// <summary>身份/时间无效与全零行作废，计入 InvalidRecords（对照蓝本 returns_no_entry_for_invalid_identity）。</summary>
    [Fact]
    public async Task Rejects_rows_with_invalid_identity_or_zero_tokens()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(home, legacy: false);
        InsertRow(db, new ZCodeRow(
            "usage-zero", "s1", 1_786_909_042_666, "m", "p", "completed", 0, 0, 0, 0, 0, "/w"));
        InsertRow(db, new ZCodeRow(
            "   ", "s1", 1_786_909_042_667, "m", "p", "completed", 10, 1, 0, 0, 11, "/w"));
        InsertRow(db, new ZCodeRow(
            "usage-t0", "s1", 0, "m", "p", "completed", 10, 1, 0, 0, 11, "/w"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.InvalidRecords.Should().Be(3);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>legacy schema（无可选列）：常量占位查询 + computed_total 回退 input+output。</summary>
    [Fact]
    public async Task Reads_legacy_schema_without_optional_columns()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(home, legacy: true);
        InsertRow(db, new ZCodeRow(
            "usage-legacy", "s1", 1_786_909_042_666, "m", "p", "completed", 100, 10, 0, 0, 110, "/w"));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Single();
        record.InputTokens.Should().Be(100); // 无 cache 列 → 0 占位，input 全额保留
        record.CacheReadTokens.Should().Be(0);
        record.CacheCreationTokens.Should().Be(0);
        record.OutputTokens.Should().Be(10);
        record.TotalTokens.Should().Be(110); // computed_total 回退 input + output，无余量
    }

    /// <summary>schema 不支持的库整体跳过；多库时其余库照常产出，状态降级 Partial。</summary>
    [Fact]
    public async Task Skips_unsupported_schema_and_degrades_to_partial()
    {
        var homeA = CreateTempHome();
        var goodDb = CreateDatabase(homeA, legacy: false);
        InsertRow(goodDb, new ZCodeRow(
            "usage-good", "s1", 1_786_909_042_666, "m", "p", "completed", 10, 1, 0, 0, 11, "/w"));
        // 坏库：model_usage 缺 input_tokens 必需列（schema 检查在查询前拒绝，行不会被读到）。
        var homeB = CreateTempHome();
        CreateDatabaseAt(Path.Combine(homeB, "cli", "db", "db.sqlite"), legacy: false, dropInputTokens: true);

        var adapter = new ZCodeSqliteAdapter(configuredHome: $"{homeA},{homeB}");

        var result = await adapter.ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].MessageId.Should().Be("usage-good");
        result.Diagnostics.FilesDiscovered.Should().Be(2);
        result.Diagnostics.FilesRead.Should().Be(1);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    /// <summary>多根同 id 跨库重复：保留先配置根的行（蓝本 duplicate usage ids keep the first root's row）。</summary>
    [Fact]
    public async Task Deduplicates_cross_database_rows_by_id_keeping_first_root()
    {
        var homeA = CreateTempHome();
        var homeB = CreateTempHome();
        var rowA = new ZCodeRow(
            "usage-dup", "s-a", 1_786_909_042_666, "m", "p", "completed", 10, 1, 0, 0, 11, "/a");
        var rowB = new ZCodeRow(
            "usage-dup", "s-b", 1_786_909_042_666, "m", "p", "completed", 20, 2, 0, 0, 22, "/b");
        InsertRow(CreateDatabase(homeA, legacy: false), rowA);
        InsertRow(CreateDatabase(homeB, legacy: false), rowB);

        var adapter = new ZCodeSqliteAdapter(
            configuredHome: $"{homeA},{homeB}");

        var result = await adapter.ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].SessionId.Should().Be("s-a");
        result.Diagnostics.DuplicateRecords.Should().Be(1);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
    }

    /// <summary>WAL 兼容：写连接持有未提交事务（-wal 非空）时，只读连接仍能读到已提交行。</summary>
    [Fact]
    public async Task Reads_committed_rows_from_wal_database_while_writer_holds_transaction()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(home, legacy: false);
        // 以 WAL 模式打开写连接，提交一行、再在显式事务里写一行且不提交，模拟 ZCode 运行中。
        var writer = new SqliteConnection($"Data Source={db}");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL;";
            command.ExecuteScalar();
        }

        InsertRow(db, new ZCodeRow(
            "usage-committed", "s1", 1_786_909_042_666, "m", "p", "completed", 10, 1, 0, 0, 11, "/w"));
        using var transaction = writer.BeginTransaction();
        using (var pending = writer.CreateCommand())
        {
            pending.Transaction = transaction;
            pending.CommandText = "INSERT INTO model_usage (id, session_id, started_at, model_id, status, input_tokens, output_tokens) VALUES ('usage-pending', 's1', 1, 'm', 'completed', 5, 1);";
            pending.ExecuteNonQuery();
        }

        try
        {
            var result = await CreateAdapter(home).ScanAsync();

            result.Records.Should().ContainSingle();
            result.Records[0].MessageId.Should().Be("usage-committed");
        }
        finally
        {
            transaction.Rollback();
            writer.Dispose();
        }
    }

    /// <summary>库不存在（目录无 cli/db/db.sqlite）：NoData，不抛异常。</summary>
    [Fact]
    public async Task Missing_database_returns_no_data()
    {
        var home = CreateTempHome();

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.FilesDiscovered.Should().Be(0);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>记录按时间戳升序（跨库汇总后仍有序）。</summary>
    [Fact]
    public async Task Sorts_records_by_timestamp()
    {
        var home = CreateTempHome();
        var db = CreateDatabase(home, legacy: false);
        InsertRow(db, new ZCodeRow(
            "usage-late", "s1", 1_786_909_043_000, "m", "p", "completed", 10, 1, 0, 0, 11, "/w"));
        InsertRow(db, new ZCodeRow(
            "usage-early", "s1", 1_786_909_042_000, "m", "p", "completed", 10, 1, 0, 0, 11, "/w"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Select(record => record.MessageId)
            .Should().ContainInOrder("usage-early", "usage-late");
    }

    // ---- helpers ----

    private string CreateTempHome()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-zcode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private string CreateDatabase(string home, bool legacy) =>
        CreateDatabaseAt(Path.Combine(home, "cli", "db", "db.sqlite"), legacy);

    private string CreateDatabaseAt(string path, bool legacy, bool dropInputTokens = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var optionalColumns = legacy
            ? string.Empty
            : """
              , cache_creation_input_tokens INTEGER NOT NULL DEFAULT 0
              , cache_read_input_tokens INTEGER NOT NULL DEFAULT 0
              , computed_total_tokens INTEGER NOT NULL DEFAULT 0
              , provider_id TEXT
              """;
        var inputColumn = dropInputTokens ? string.Empty : "input_tokens INTEGER NOT NULL DEFAULT 0,";
        var sessionColumns = legacy ? "id TEXT PRIMARY KEY, directory TEXT" : "id TEXT PRIMARY KEY, directory TEXT, version TEXT";

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE model_usage (
              id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL,
              started_at INTEGER NOT NULL,
              model_id TEXT NOT NULL,
              status TEXT NOT NULL,
              {inputColumn}
              output_tokens INTEGER NOT NULL DEFAULT 0
              {optionalColumns}
            );
            CREATE TABLE session ({sessionColumns});
            """;
        command.ExecuteNonQuery();
        return path;
    }

    private static void InsertRow(string db, ZCodeRow row)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();

        // 按实际 schema 决定可选列（legacy 库没有 cache/computed/provider 列）。
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(model_usage);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        var hasOptionalColumns = false;
        var insertColumns = "id, session_id, started_at, model_id, status, input_tokens, output_tokens";
        var insertValues = "$id, $sessionId, $startedAt, $modelId, $status, $input, $output";
        if (columns.Contains("cache_creation_input_tokens"))
        {
            hasOptionalColumns = true;
            insertColumns += ", cache_creation_input_tokens, cache_read_input_tokens, computed_total_tokens, provider_id";
            insertValues += ", $cacheCreation, $cacheRead, $computedTotal, $providerId";
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO model_usage ({insertColumns}) VALUES ({insertValues});\n"
            + "INSERT OR IGNORE INTO session (id, directory) VALUES ($sessionId, $directory);";
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$sessionId", row.SessionId);
        command.Parameters.AddWithValue("$startedAt", row.StartedAt);
        command.Parameters.AddWithValue("$modelId", row.ModelId);
        command.Parameters.AddWithValue("$status", row.Status);
        command.Parameters.AddWithValue("$input", row.InputTokens);
        command.Parameters.AddWithValue("$output", row.OutputTokens);
        if (hasOptionalColumns)
        {
            command.Parameters.AddWithValue("$cacheCreation", row.CacheCreation);
            command.Parameters.AddWithValue("$cacheRead", row.CacheRead);
            command.Parameters.AddWithValue("$computedTotal", row.ComputedTotal);
            command.Parameters.AddWithValue("$providerId", (object?)row.ProviderId ?? DBNull.Value);
        }

        command.Parameters.AddWithValue("$directory", (object?)row.Directory ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private ZCodeSqliteAdapter CreateAdapter(string home) => new(configuredHome: home);

    /// <summary>fixture 行参数（与生产 schema 列对应；legacy 库忽略可选列值）。</summary>
    private sealed record ZCodeRow(
        string Id,
        string SessionId,
        long StartedAt,
        string ModelId,
        string? ProviderId,
        string Status,
        long InputTokens,
        long OutputTokens,
        long CacheCreation,
        long CacheRead,
        long ComputedTotal,
        string? Directory);

    public void Dispose()
    {
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
