namespace Tokenmetry.Relay.Tests;

using Microsoft.Data.Sqlite;

/// <summary>
/// 中继 host 维度前向升级：旧库（usage_rows 无 host、devices 无 data_epoch）启动时必须整表重建
/// usage_rows 并把历史行回填 host=''，session_rows / devices 补属性列；既有行不得丢失。
/// </summary>
public sealed class RelayDatabaseMigrationTests
{
    [Fact]
    public async Task Upgrade_adds_host_to_usage_primary_key_and_preserves_existing_rows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tokenmetry-relay-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE devices (
                        account_id TEXT NOT NULL,
                        device_id TEXT NOT NULL,
                        name TEXT NOT NULL,
                        last_seen_at TEXT NOT NULL,
                        PRIMARY KEY (account_id, device_id)
                    );
                    CREATE TABLE usage_rows (
                        account_id TEXT NOT NULL,
                        device_id TEXT NOT NULL,
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
                        seq INTEGER NOT NULL,
                        PRIMARY KEY (account_id, device_id, day, grain, agent, model, provider, project)
                    );
                    CREATE TABLE session_rows (
                        account_id TEXT NOT NULL,
                        device_id TEXT NOT NULL,
                        session_id TEXT NOT NULL,
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
                        updated_at TEXT NOT NULL,
                        seq INTEGER NOT NULL,
                        PRIMARY KEY (account_id, session_id)
                    );
                    INSERT INTO devices (account_id, device_id, name, last_seen_at)
                    VALUES ('acct', 'dev', 'Old desktop', '2026-06-19T10:00:00.0000000+00:00');
                    INSERT INTO usage_rows (
                        account_id, device_id, day, grain, total_tokens, updated_at, seq)
                    VALUES ('acct', 'dev', '2026-06-19', 'day', 123, '2026-06-19T10:00:00.0000000+00:00', 7);
                    INSERT INTO session_rows (
                        account_id, device_id, session_id, first_seen_at, last_seen_at, total_tokens, updated_at, seq)
                    VALUES ('acct', 'dev', 's-old', '2026-06-19T10:00:00.0000000+00:00',
                        '2026-06-19T10:00:00.0000000+00:00', 123, '2026-06-19T10:00:00.0000000+00:00', 8);
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            await new RelayDatabase(dbPath).EnsureCreatedAsync();

            await using var check = new SqliteConnection($"Data Source={dbPath}");
            await check.OpenAsync();

            var usagePk = await ReadPrimaryKeyColumnsAsync(check, "usage_rows");
            usagePk.Should().Contain("host", "host 必须进入 relay 行身份，否则两个宿主在 relay 主键上互撞");

            (await ReadColumnsAsync(check, "session_rows")).Should().Contain("host");
            (await ReadColumnsAsync(check, "devices")).Should().Contain("data_epoch");

            await using (var usage = check.CreateCommand())
            {
                usage.CommandText = "SELECT host, total_tokens FROM usage_rows WHERE account_id = 'acct'";
                await using var reader = await usage.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue("历史行必须在整表重建中保留");
                reader.GetString(0).Should().BeEmpty("历史行无法诚实回填宿主 → 空串（未识别）");
                reader.GetInt64(1).Should().Be(123);
            }

            await using (var session = check.CreateCommand())
            {
                session.CommandText = "SELECT host FROM session_rows WHERE session_id = 's-old'";
                (await session.ExecuteScalarAsync()).Should().Be(string.Empty);
            }

            await using (var device = check.CreateCommand())
            {
                device.CommandText = "SELECT data_epoch FROM devices WHERE device_id = 'dev'";
                Convert.ToInt32(await device.ExecuteScalarAsync()).Should().Be(0,
                    "旧设备从 epoch 0 开始，等客户端升级后推整份快照再 +1");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    private static async Task<HashSet<string>> ReadPrimaryKeyColumnsAsync(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(5) && reader.GetInt32(5) > 0)
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }
}
