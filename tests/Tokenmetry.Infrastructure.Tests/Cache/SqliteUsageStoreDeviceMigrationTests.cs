namespace Tokenmetry.Infrastructure.Tests.Cache;

using Microsoft.Data.Sqlite;
using Tokenmetry.Infrastructure.Cache;

/// <summary>
/// 迁移 3（设备维度）：device_registry 自举、device 进 usage_facts 主键、session_facts 加列。
/// 迁移纪律"转换"分支：v2 历史行必须保留并回填本机 device_id（relay 计划 Review Focus #2）。
/// </summary>
public sealed class SqliteUsageStoreDeviceMigrationTests : IAsyncLifetime
{
    private string _tempDbPath = null!;

    public Task InitializeAsync()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"tokenmetry-test-{Guid.NewGuid():N}.db");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // A-1（Windows）：连接池默认开启，池化连接持有 .db 句柄——先清池再删（同 SqliteUsageStoreTests）。
        SqliteConnection.ClearAllPools();
        if (!File.Exists(_tempDbPath))
        {
            return;
        }

        for (var i = 0; i < 5; i++)
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

    [Fact]
    public async Task FreshDb_Migration3_CreatesRegistryAndDevicePk()
    {
        var store = new SqliteUsageStore(_tempDbPath);
        await store.InitializeAsync();

        var first = await store.GetLocalDeviceIdAsync();
        var second = await store.GetLocalDeviceIdAsync();

        first.Should().NotBeNullOrEmpty();
        second.Should().Be(first, "本机设备 id 必须稳定");

        await using var connection = OpenConnection();
        var (name, pk) = await ReadPragmaColumnAsync(connection, "usage_facts", "device");
        name.Should().Be("device", "device 维度必须进 usage_facts");
        pk.Should().BeGreaterThanOrEqualTo(1, "device 是主键成员");
    }

    [Fact]
    public async Task V2Db_Upgrade_PreservesRowsWithLocalDeviceBackfill()
    {
        await CreateV2SchemaAsync(_tempDbPath);

        var store = new SqliteUsageStore(_tempDbPath);
        await store.InitializeAsync();

        var deviceId = await store.GetLocalDeviceIdAsync();

        var facts = await store.GetUsageFactsAsync();
        facts.Should().ContainSingle("v2 历史行必须保留（转换分支，禁丢弃）");
        facts[0].TotalTokens.Should().Be(1234);
        facts[0].CostUsd.Should().Be(1.25m);
        facts[0].Device.Should().Be(deviceId, "本地行回填本机 device_id");

        var sessions = await store.GetSessionFactsAsync();
        sessions.Should().ContainSingle();
        sessions[0].SessionId.Should().Be("sess-v2");
        sessions[0].TotalTokens.Should().Be(99);
        sessions[0].Device.Should().Be(deviceId);

        await using var connection = OpenConnection();
        var registryName = await ReadScalarAsync(connection, "SELECT name FROM device_registry");
        registryName.Should().Be(Environment.MachineName, "设备名初值 = 迁移时刻机器名");
    }

    // ---- helpers ----

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_tempDbPath}");
        connection.Open();
        return connection;
    }

    /// <summary>PRAGMA table_info 读取指定列：返回（列名, 主键序，非主键为 0）。</summary>
    private static async Task<(string Name, long Pk)> ReadPragmaColumnAsync(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == column)
            {
                return (reader.GetString(1), reader.GetInt64(5));
            }
        }

        return (string.Empty, 0);
    }

    private static async Task<string> ReadScalarAsync(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result as string ?? string.Empty;
    }

    /// <summary>手工搭建 v2 终态 schema（迁移 1+2 的产物）并落账版本 1、2，插入历史行各一。</summary>
    private static async Task CreateV2SchemaAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await ExecuteAsync(connection, """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            );
            INSERT INTO schema_migrations (version, name, applied_at) VALUES
                (1, 'create-facts-schema', '2026-01-01T00:00:00.0000000+00:00'),
                (2, 'usage-facts-provider-dimension', '2026-01-01T00:00:00.0000000+00:00');
            """);

        // 迁移 2 之后的 usage_facts 形态（照抄迁移 2 的 CREATE TABLE）
        await ExecuteAsync(connection, """
            CREATE TABLE usage_facts (
                day TEXT NOT NULL,
                grain TEXT NOT NULL,
                agent TEXT NOT NULL DEFAULT '',
                model TEXT NOT NULL DEFAULT '',
                provider TEXT NOT NULL DEFAULT '',
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
                PRIMARY KEY (day, grain, agent, model, provider, project)
            )
            """);

        // 迁移 1 的 session_facts 形态
        await ExecuteAsync(connection, """
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
            )
            """);

        await ExecuteAsync(connection, """
            INSERT INTO usage_facts (
                day, grain, agent, model, provider, project, project_display_name,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at)
            VALUES (
                '2026-06-19', 'full', 'claude-code', 'claude-sonnet', '', '/tmp/proj', 'proj',
                100, 200, 0, 0, 0, 300, 1234, '1.25', '2026-06-19T10:00:00.0000000+00:00')
            """);

        await ExecuteAsync(connection, """
            INSERT INTO session_facts (
                session_id, agent, project, project_display_name, dominant_model,
                first_seen_at, last_seen_at,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at)
            VALUES (
                'sess-v2', 'claude-code', '/tmp/proj', 'proj', 'claude-sonnet',
                '2026-06-19T09:00:00.0000000+00:00', '2026-06-19T10:00:00.0000000+00:00',
                10, 20, 0, 0, 0, 30, 99, NULL, '2026-06-19T10:00:00.0000000+00:00')
            """);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
