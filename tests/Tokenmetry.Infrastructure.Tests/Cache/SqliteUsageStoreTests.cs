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
        // 重复 Initialize 不重跑迁移：全部已注册版本各落账一次、顺序完整（迁移 3 起含设备维度，
        // 迁移 4 起含设置同步账面，迁移 5 起 host 进主键，迁移 6 起远端设备数据 epoch）。
        MigrationVersions().Should().Equal(1L, 2L, 3L, 4L, 5L, 6L);
    }

    /// <summary>I-036：provider 进入维度（三元组 agent × model × provider）——同模型不同 provider 分行的往返。</summary>
    [Fact]
    public async Task Upsert_usage_facts_splits_rows_by_provider()
    {
        var bare = CreateFullFact(day: "2026-06-19", costUsd: 1.23m);
        var routed = CreateFullFact(day: "2026-06-19", costUsd: 2.34m);
        routed.Provider = "openrouter";

        await _store.UpsertUsageFactsAsync(new List<UsageFact> { bare, routed });

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().HaveCount(2, "同一模型经不同 provider 的出账必须分行（价目可能不同）");
        stored.Single(fact => fact.Provider == string.Empty).CostUsd.Should().Be(1.23m);
        stored.Single(fact => fact.Provider == "openrouter").CostUsd.Should().Be(2.34m);
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
            Project = "C:/workspace/dev/project-a",
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
        // 本地写路径由 store 盖本机 device 戳（迁移 3 起），往返断言以盖戳后的值为准。
        fact.Device = await _store.GetLocalDeviceIdAsync();
        // updated_at 同为 store 盖戳（本地写路径忽略载荷值），只断言"已盖戳"，不参与逐列比较。
        restored.Should().BeEquivalentTo(fact, options => options.Excluding(row => row.UpdatedAt));
        restored.UpdatedAt.Should().NotBeNull();

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
    public async Task Full_facts_for_a_plugin_agent_survive_decomposition_cleanup()
    {
        // 插件停用/卸载不删历史 facts：facts 是 upsert-only，分解行清理（每轮管道固定执行）
        // 只能清非 full 行，不得误伤任何 agent（含插件 Agent）的 full 行。
        var pluginFact = CreateFullFact("2026-06-19", costUsd: 1m);
        pluginFact.Agent = "Foo";
        await _store.UpsertUsageFactsAsync(new List<UsageFact> { pluginFact });

        await _store.DeleteDecompositionFactsAsync();

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().ContainSingle().Which.Agent.Should().Be("Foo");
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
            Project = "C:/workspace/dev/project-a",
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
        // 本地写路径由 store 盖本机 device 戳（迁移 3 起），往返断言以盖戳后的值为准。
        first.Device = await _store.GetLocalDeviceIdAsync();
        // updated_at 同为 store 盖戳（本地写路径忽略载荷值），只断言"已盖戳"，不参与逐列比较。
        restored.Should().BeEquivalentTo(first, options => options.Excluding(row => row.UpdatedAt));
        restored.UpdatedAt.Should().NotBeNull();

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

    [Fact]
    public async Task Reconcile_removes_superseded_local_full_rows_for_covered_agent_days()
    {
        await _store.UpsertUsageFactsAsync([CreateFullFact(day: "2026-06-19", costUsd: 1m)]);
        var replacement = CreateFullFact(day: "2026-06-19", costUsd: 2m);
        replacement.Project = "C:/workspace/dev/project-b";

        await _store.ReconcileUsageFactsAsync(
            [replacement],
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["Claude Code"] = new HashSet<string> { "2026-06-19" },
            });

        var facts = await _store.GetUsageFactsAsync();
        facts.Should().ContainSingle("旧项目键（行身份）必须被清掉，否则 upsert 会留成幽灵行")
            .Which.Project.Should().Be("C:/workspace/dev/project-b");
    }

    [Fact]
    public async Task Reconcile_keeps_uncovered_days_and_other_agents()
    {
        var coveredDay = CreateFullFact(day: "2026-06-19", costUsd: 1m);
        var uncoveredDay = CreateFullFact(day: "2026-06-20", costUsd: 2m);
        var otherAgent = CreateFullFact(day: "2026-06-19", costUsd: 3m);
        otherAgent.Agent = "Codex";
        otherAgent.Project = "C:/workspace/dev/other";
        await _store.UpsertUsageFactsAsync([coveredDay, uncoveredDay, otherAgent]);

        await _store.ReconcileUsageFactsAsync(
            [CreateFullFact(day: "2026-06-19", costUsd: 9m)],
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["Claude Code"] = new HashSet<string> { "2026-06-19" },
            });

        var facts = await _store.GetUsageFactsAsync();
        facts.Should().Contain(fact => fact.Day == "2026-06-20", "未覆盖的日期不清理（历史保留）");
        facts.Should().Contain(fact => fact.Agent == "Codex", "别的 agent 不在清理范围");
        facts.Where(fact => fact.Agent == "Claude Code" && fact.Day == "2026-06-19")
            .Should().ContainSingle().Which.CostUsd.Should().Be(9m);
    }

    [Fact]
    public async Task Reconcile_does_not_touch_remote_device_rows()
    {
        var remote = CreateFullFact(day: "2026-06-19", costUsd: 4m);
        remote.Device = "remote-device";
        remote.UpdatedAt = DateTimeOffset.UtcNow;
        await _store.UpsertRemoteUsageFactsAsync([remote]);

        await _store.ReconcileUsageFactsAsync(
            [],
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["Claude Code"] = new HashSet<string> { "2026-06-19" },
            });

        var facts = await _store.GetUsageFactsAsync();
        facts.Should().ContainSingle().Which.Device.Should().Be("remote-device",
            "权威清理只作用于本机设备行");
    }

    [Fact]
    public async Task Reconcile_does_not_touch_unchanged_row_versions()
    {
        var fact = CreateFullFact(day: "2026-06-19", costUsd: 1m);
        await _store.UpsertUsageFactsAsync([fact]);
        var before = (await _store.GetUsageFactsAsync()).Single().UpdatedAt;
        await Task.Delay(20);

        await _store.ReconcileUsageFactsAsync(
            [fact],
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["Claude Code"] = new HashSet<string> { "2026-06-19" },
            });

        (await _store.GetUsageFactsAsync()).Single().UpdatedAt.Should().Be(before,
            "未变化行不重写 updated_at（增量同步不被每轮刷新退化成全量）");
    }

    [Fact]
    public async Task Upsert_usage_facts_splits_rows_by_host()
    {
        var viaCindy = CreateFullFact(day: "2026-06-19", costUsd: 1m);
        viaCindy.Host = "Cindy";
        var viaCli = CreateFullFact(day: "2026-06-19", costUsd: 2m);

        await _store.UpsertUsageFactsAsync([viaCindy, viaCli]);

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().HaveCount(2, "同一引擎同一天经不同宿主使用必须分行");
        stored.Single(fact => fact.Host == "Cindy").CostUsd.Should().Be(1m);
        stored.Single(fact => fact.Host == string.Empty).CostUsd.Should().Be(2m);
    }

    [Fact]
    public async Task Host_is_in_the_facts_primary_key_and_a_session_attribute()
    {
        using var connection = new SqliteConnection($"Data Source={_tempDbPath}");
        await connection.OpenAsync();

        var factsPk = ReadPrimaryKeyColumns(connection, "usage_facts");
        factsPk.Should().Contain("host", "host 是行身份维度：不进主键会让一个宿主覆盖另一个");

        var sessionColumns = ReadColumns(connection, "session_facts");
        sessionColumns.Should().Contain("host");
    }

    [Fact]
    public async Task Reconcile_removes_superseded_row_when_host_changes()
    {
        await _store.UpsertUsageFactsAsync([CreateFullFact(day: "2026-06-19", costUsd: 1m)]);
        var attributed = CreateFullFact(day: "2026-06-19", costUsd: 2m);
        attributed.Host = "Cindy";

        await _store.ReconcileUsageFactsAsync(
            [attributed],
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["Claude Code"] = new HashSet<string> { "2026-06-19" },
            });

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().ContainSingle("归属从空串变成 Cindy 后，旧行必须被清掉")
            .Which.Host.Should().Be("Cindy");
    }

    private static HashSet<string> ReadPrimaryKeyColumns(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (!reader.IsDBNull(5) && reader.GetInt32(5) > 0)
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static UsageFact CreateFullFact(string day, decimal? costUsd, long inputTokens = 100)
    {
        return new UsageFact
        {
            Day = day,
            Grain = UsageFactGrain.Full,
            Agent = "Claude Code",
            Model = "claude-sonnet-4-20250514",
            Project = "C:/workspace/dev/project-a",
            InputTokens = inputTokens,
            OutputTokens = 50,
            CacheCreationTokens = 10,
            CacheReadTokens = 20,
            TotalTokens = inputTokens + 80,
            CostUsd = costUsd,
        };
    }

    /// <summary>
    /// I-036 migration 2 升级路径：只应用过 migration 1 的库（已发布版本，无 provider 列、
    /// 主键不含 provider）升级时<b>不搬数据</b>——provider 进入主键后旧行与新行永不合并，
    /// 原样保留 = 升级后同源重采集双算。整表重建为空，历史由本地采集首轮全量重建
    /// （增量扫描器每轮返回全量记录；发布时库龄尚在上游日志保留期内，数据可重建）。
    /// </summary>
    [Fact]
    public async Task ProviderMigrationRebuildsFactsTableEmptyForRescan()
    {
        var v1DbPath = Path.Combine(Path.GetTempPath(), $"ccusage-v1-{Guid.NewGuid()}.db");
        try
        {
            await CreateV1FactsDb(v1DbPath);

            var store = new SqliteUsageStore(v1DbPath);
            await store.InitializeAsync();

            // 旧行不搬（主键换了语义，搬 = 双算）；表结构已带 provider 维度、可写。
            var facts = await store.GetUsageFactsAsync();
            facts.Should().BeEmpty("migration 2 不搬数据：升级后由采集首轮全量重建");

            var routed = CreateFullFact(day: "2026-06-19", costUsd: 9m);
            routed.Provider = "openrouter";
            await store.UpsertUsageFactsAsync(new List<UsageFact> { routed });

            var rebuilt = await store.GetUsageFactsAsync();
            rebuilt.Should().ContainSingle("重建后的表按新主键（含 provider）正常读写");
            rebuilt[0].Provider.Should().Be("openrouter");
            rebuilt[0].CostUsd.Should().Be(9m);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(v1DbPath))
            {
                File.Delete(v1DbPath);
            }
        }
    }

    private static async Task CreateV1FactsDb(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            );
            INSERT INTO schema_migrations (version, name, applied_at)
                VALUES (1, 'create-facts-schema', '2026-01-01T00:00:00Z');

            CREATE TABLE usage_facts (
                day TEXT NOT NULL,
                grain TEXT NOT NULL,
                agent TEXT NOT NULL DEFAULT '',
                model TEXT NOT NULL DEFAULT '',
                project TEXT NOT NULL DEFAULT '',
                project_display_name TEXT NOT NULL DEFAULT '',
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_ephemeral_5m_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_ephemeral_1h_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                total_tokens INTEGER NOT NULL DEFAULT 0,
                cost_usd TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (day, grain, agent, model, project)
            );

            CREATE TABLE session_facts (
                session_id TEXT NOT NULL PRIMARY KEY,
                agent TEXT NOT NULL DEFAULT '',
                project TEXT NOT NULL DEFAULT '',
                project_display_name TEXT NOT NULL DEFAULT '',
                dominant_model TEXT NOT NULL DEFAULT '',
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_ephemeral_5m_tokens INTEGER NOT NULL DEFAULT 0,
                cache_creation_ephemeral_1h_tokens INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens INTEGER NOT NULL DEFAULT 0,
                total_tokens INTEGER NOT NULL DEFAULT 0,
                cost_usd TEXT,
                updated_at TEXT NOT NULL
            );

            INSERT INTO usage_facts (
                day, grain, agent, model, project, project_display_name,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at)
            VALUES (
                '2026-06-19', 'full', 'Claude Code', 'claude-sonnet-4-20250514', '/p', 'p',
                100, 50, 10, 0, 0, 20, 180, '1.23', '2026-06-19T00:00:00Z');
            """;
        await cmd.ExecuteNonQueryAsync();
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
