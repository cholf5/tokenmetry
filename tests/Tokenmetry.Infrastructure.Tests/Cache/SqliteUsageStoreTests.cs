namespace Tokenmetry.Infrastructure.Tests.Cache;

using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Cache;

public class SqliteUsageStoreTests : IAsyncLifetime
{
    private string _tempDbPath = null!;
    private SqliteUsageStore _store = null!;

    public async Task InitializeAsync()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"ccusage-test-{Guid.NewGuid()}.db");
        _store = new SqliteUsageStore(_tempDbPath);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (File.Exists(_tempDbPath))
        {
            // A-1（Windows）：连接池默认开启（Pooling=True），池化连接会一直持有 .db 句柄，
            // 重试删除救不回来——先清池再删，POSIX 上无副作用。
            SqliteConnection.ClearAllPools();
            // SQLite may keep the file open for a short time. Retry a few times.
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.Delete(_tempDbPath);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    [Fact]
    public async Task Initialize_is_idempotent_and_preserves_data()
    {
        await _store.UpsertUsageFactsAsync(new List<UsageFact> { CreateFullFact(day: "2026-06-19", costUsd: 1.23m) });

        await _store.InitializeAsync();

        var facts = await _store.GetUsageFactsAsync();
        facts.Should().ContainSingle();
        facts[0].CostUsd.Should().Be(1.23m);
        MigrationVersions().Should().ContainSingle().Which.Should().Be(1L);
    }

    [Fact]
    public async Task Upsert_usage_facts_with_same_batch_is_idempotent()
    {
        var facts = new List<UsageFact>
        {
            CreateFullFact(day: "2026-06-19", costUsd: 1.23m),
            CreateFullFact(day: "2026-06-20", costUsd: 2.34m)
        };

        await _store.UpsertUsageFactsAsync(facts);
        await _store.UpsertUsageFactsAsync(facts);

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().HaveCount(2);
        stored.First(fact => fact.Day == "2026-06-19").CostUsd.Should().Be(1.23m);
        stored.First(fact => fact.Day == "2026-06-20").CostUsd.Should().Be(2.34m);
    }

    [Fact]
    public async Task Upsert_usage_facts_replaces_values_with_latest_write()
    {
        await _store.UpsertUsageFactsAsync(new List<UsageFact> { CreateFullFact("2026-06-19", costUsd: 1.23m, inputTokens: 100) });
        await _store.UpsertUsageFactsAsync(new List<UsageFact> { CreateFullFact("2026-06-19", costUsd: 2.34m, inputTokens: 200) });

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().ContainSingle();
        stored[0].CostUsd.Should().Be(2.34m);
        stored[0].InputTokens.Should().Be(200);
    }

    [Fact]
    public async Task Usage_facts_roundtrip_preserves_all_columns()
    {
        var fact = new UsageFact
        {
            Day = "2026-06-19",
            Grain = UsageFactGrain.Full,
            Agent = "Claude Code",
            Model = "claude-sonnet-4-20250514-fast",
            Project = "/Users/dev/project-a",
            ProjectDisplayName = "project-a",
            InputTokens = 1234,
            OutputTokens = 567,
            CacheCreationTokens = 89,
            CacheCreationEphemeral5mTokens = 12,
            CacheCreationEphemeral1hTokens = 77,
            CacheReadTokens = 246,
            TotalTokens = 2136,
            CostUsd = 12.3456789m,
        };
        var costless = CreateFullFact("2026-06-20", costUsd: null);
        costless.Model = string.Empty;

        await _store.UpsertUsageFactsAsync(new List<UsageFact> { fact, costless });

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().HaveCount(2);

        var restored = stored.Single(row => row.Day == "2026-06-19");
        restored.Should().BeEquivalentTo(fact);

        var restoredCostless = stored.Single(row => row.Day == "2026-06-20");
        restoredCostless.CostUsd.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_usage_facts_with_empty_model_dimension_is_idempotent()
    {
        var fact = CreateFullFact("2026-06-19", costUsd: null);
        fact.Model = string.Empty; // mapper 把 null model 归一为空串，仍是唯一约束的一部分

        await _store.UpsertUsageFactsAsync(new List<UsageFact> { fact });
        await _store.UpsertUsageFactsAsync(new List<UsageFact> { fact });

        (await _store.GetUsageFactsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_decomposition_facts_removes_non_full_rows_and_preserves_full_rows_and_sessions()
    {
        // I-005 数据源切换清理：旧 CLI 源的分解粒度行清掉，full 行与 session facts 不受影响。
        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            CreateFullFact("2026-06-19", costUsd: 1m),
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Day, TotalTokens = 500, CostUsd = 0.4m },
            new() { Day = "2026-06-20", Grain = UsageFactGrain.Agent, Agent = "Claude Code", TotalTokens = 100 },
            new() { Day = "2026-06-20", Grain = UsageFactGrain.Project, Project = "/p", ProjectDisplayName = "p", TotalTokens = 100 },
        });
        await _store.UpsertSessionFactsAsync(new List<SessionFact>
        {
            new() { SessionId = "session-1", FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow }
        });

        await _store.DeleteDecompositionFactsAsync();

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().ContainSingle();
        stored.Single().Grain.Should().Be(UsageFactGrain.Full);
        stored.Single().TotalTokens.Should().Be(180);
        (await _store.GetSessionFactsAsync()).Should().ContainSingle();

        // 幂等：重复删除为空操作。
        await _store.DeleteDecompositionFactsAsync();
        (await _store.GetUsageFactsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Replace_usage_facts_by_day_cleans_stale_decomposition_rows()
    {
        var first = new List<UsageFact>
        {
            CreateFullFact("2026-06-19", costUsd: 1m),
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Model, Model = "claude-opus-4", TotalTokens = 500, CostUsd = 0.4m },
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Model, Model = "claude-haiku-old", TotalTokens = 500, CostUsd = 0.6m },
        };
        await _store.ReplaceUsageFactsByDayAsync(first);

        // 第二次写入分解行收缩（旧缓存语义是整日替换，消失的条目不能残留）
        var second = new List<UsageFact>
        {
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Day, TotalTokens = 500, CostUsd = 0.4m },
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Model, Model = "claude-opus-4", TotalTokens = 500, CostUsd = 0.4m },
        };
        await _store.ReplaceUsageFactsByDayAsync(second);

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().HaveCount(3); // full 行保留 + day + 剩余 model
        stored.Should().NotContain(fact => fact.Model == "claude-haiku-old");
        stored.Count(fact => fact.Grain == UsageFactGrain.Model).Should().Be(1);

        // 其他日期不受影响
        await _store.ReplaceUsageFactsByDayAsync(new List<UsageFact>
        {
            new() { Day = "2026-06-20", Grain = UsageFactGrain.Day, TotalTokens = 10 },
        });
        stored = await _store.GetUsageFactsAsync();
        stored.Where(fact => fact.Day == "2026-06-19").Should().HaveCount(3);
        stored.Single(fact => fact.Day == "2026-06-20").TotalTokens.Should().Be(10);
    }

    [Fact]
    public async Task Session_facts_upsert_is_idempotent_and_roundtrips()
    {
        var first = new SessionFact
        {
            SessionId = "session-1",
            Agent = "Claude Code",
            Project = "/Users/dev/project-a",
            DominantModel = "claude-opus-4",
            FirstSeenAt = new DateTimeOffset(2026, 6, 19, 8, 0, 0, TimeSpan.FromHours(8)),
            LastSeenAt = new DateTimeOffset(2026, 6, 19, 9, 30, 0, TimeSpan.FromHours(8)),
            InputTokens = 1000,
            OutputTokens = 500,
            CacheCreationTokens = 100,
            CacheCreationEphemeral5mTokens = 40,
            CacheCreationEphemeral1hTokens = 60,
            CacheReadTokens = 200,
            TotalTokens = 1800,
            CostUsd = 3.21m,
        };
        var second = new SessionFact { SessionId = "session-2", FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };

        await _store.UpsertSessionFactsAsync(new List<SessionFact> { first, second });
        await _store.UpsertSessionFactsAsync(new List<SessionFact> { first, second });

        var stored = await _store.GetSessionFactsAsync();
        stored.Should().HaveCount(2);
        var restored = stored.Single(fact => fact.SessionId == "session-1");
        restored.Should().BeEquivalentTo(first);

        // 同一会话重复扫描更新数值而非插新行
        var updated = new SessionFact { SessionId = "session-2", Agent = "Claude Code", InputTokens = 10, FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        await _store.UpsertSessionFactsAsync(new List<SessionFact> { updated });

        stored = await _store.GetSessionFactsAsync();
        stored.Should().HaveCount(2);
        stored.Single(fact => fact.SessionId == "session-2").InputTokens.Should().Be(10);
    }

    [Fact]
    public async Task Legacy_json_cache_db_is_rebuilt_to_facts_schema()
    {
        var legacyDbPath = Path.Combine(Path.GetTempPath(), $"ccusage-legacy-{Guid.NewGuid()}.db");
        try
        {
            await CreateLegacyJsonCacheDb(legacyDbPath);

            var store = new SqliteUsageStore(legacyDbPath);
            await store.InitializeAsync();

            using (var connection = new SqliteConnection($"Data Source={legacyDbPath}"))
            {
                await connection.OpenAsync();
                var tables = await ReadTableNames(connection);
                tables.Should().Contain("usage_facts").And.Contain("session_facts").And.Contain("schema_migrations");
                tables.Should().NotContain("dashboard_cache").And.NotContain("daily_rows_cache");
            }

            (await store.GetUsageFactsAsync()).Should().BeEmpty();

            // 重建后存储可正常读写
            await store.UpsertUsageFactsAsync(new List<UsageFact> { CreateFullFact("2026-06-19", costUsd: 1m) });
            (await store.GetUsageFactsAsync()).Should().ContainSingle();
        }
        finally
        {
            // A-1（Windows）：store 的池化连接持有句柄，先清池再删（见 DisposeAsync 注释）。
            SqliteConnection.ClearAllPools();
            if (File.Exists(legacyDbPath))
            {
                File.Delete(legacyDbPath);
            }
        }
    }

    private static UsageFact CreateFullFact(string day, decimal? costUsd, long inputTokens = 100)
    {
        return new UsageFact
        {
            Day = day,
            Grain = UsageFactGrain.Full,
            Agent = "Claude Code",
            Model = "claude-sonnet-4-20250514",
            Project = "/Users/dev/project-a",
            InputTokens = inputTokens,
            OutputTokens = 50,
            CacheCreationTokens = 10,
            CacheReadTokens = 20,
            TotalTokens = inputTokens + 80,
            CostUsd = costUsd,
        };
    }

    private List<long> MigrationVersions()
    {
        using var connection = new SqliteConnection($"Data Source={_tempDbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_migrations ORDER BY version";
        using var reader = cmd.ExecuteReader();
        var versions = new List<long>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt64(0));
        }

        return versions;
    }

    private static async Task<List<string>> ReadTableNames(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using var reader = await cmd.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task CreateLegacyJsonCacheDb(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        const string createDashboardTableSql = """
            CREATE TABLE dashboard_cache (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;

        const string createDailyRowsTableSql = """
            CREATE TABLE daily_rows_cache (
                date TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = createDashboardTableSql;
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = createDailyRowsTableSql;
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "INSERT INTO dashboard_cache (id, json) VALUES (1, '{\"Status\":\"ok\"}')";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "INSERT INTO daily_rows_cache (date, json) VALUES ('2026-06-18', '{\"Date\":\"2026-06-18\"}')";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
