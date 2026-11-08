using Microsoft.Data.Sqlite;

namespace Tokenmetry.Relay;

/// <summary>
/// 中继 SQLite 存储（A4/A15）：刻意笨的"桶"——accounts / devices / usage_rows / session_rows 四表，
/// 幂等建表，无版本化迁移（自建部署可随时删库重建，客户端全量可重推）。
/// 账号级单调 seq 计数器住在 accounts.seq（行级变更账本的发号源，Task 6 消费）。
/// </summary>
internal sealed class RelayDatabase(string dbPath)
{
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        return connection;
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        await ExecuteAsync(connection, """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS accounts (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                credential_hash TEXT NOT NULL UNIQUE,
                github_sub TEXT,
                display_name TEXT,
                avatar_url TEXT,
                created_at TEXT NOT NULL,
                seq INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_github_sub
                ON accounts (github_sub) WHERE github_sub IS NOT NULL;

            -- GitHub 多设备登录各自持 token（一次登录签发一条；明文不落库，SHA-256 落库）。
            -- shared 单租户仍走 accounts.credential_hash（A15）。
            CREATE TABLE IF NOT EXISTS account_tokens (
                account_id TEXT NOT NULL,
                credential_hash TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                PRIMARY KEY (account_id, credential_hash)
            );

            CREATE TABLE IF NOT EXISTS devices (
                account_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                name TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                PRIMARY KEY (account_id, device_id)
            );

            CREATE TABLE IF NOT EXISTS usage_rows (
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
            CREATE INDEX IF NOT EXISTS idx_usage_rows_seq ON usage_rows (account_id, seq);

            CREATE TABLE IF NOT EXISTS session_rows (
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
            CREATE INDEX IF NOT EXISTS idx_session_rows_seq ON session_rows (account_id, seq);
            """, ct);
    }

    /// <summary>
    /// 单租户自举（A15）：TOKENMETRY_RELAY_TOKEN 的 SHA-256 即唯一账号凭据。
    /// INSERT OR IGNORE：同 token 重启复用既有账号；换 token = 新账号（自建文档警示）。
    /// </summary>
    public static async Task BootstrapSharedAccountAsync(RelayDatabase database, string token, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO accounts (id, kind, credential_hash, github_sub, display_name, avatar_url, created_at, seq)
            VALUES ($id, 'shared', $hash, NULL, 'self-hosted', NULL, $now, 0)
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$hash", RelayAuth.HashToken(token));
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// GitHub 身份 upsert（kind='github'）：首次登录建账号，再次登录更新资料并追加签发 token。
    /// token 明文不落库——credential_hash 是唯一凭据痕迹。
    /// </summary>
    public async Task UpsertGitHubAccountAsync(string githubSub, string? login, string? avatarUrl, string credentialHash, CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        string? accountId;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT id FROM accounts WHERE github_sub = $sub";
            cmd.Parameters.AddWithValue("$sub", githubSub);
            accountId = await cmd.ExecuteScalarAsync(ct) as string;
        }

        if (accountId is null)
        {
            accountId = Guid.NewGuid().ToString("N");
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO accounts (id, kind, credential_hash, github_sub, display_name, avatar_url, created_at, seq)
                VALUES ($id, 'github', $hash, $sub, $login, $avatar, $now, 0)
                """;
            insert.Parameters.AddWithValue("$id", accountId);
            insert.Parameters.AddWithValue("$hash", credentialHash);
            insert.Parameters.AddWithValue("$sub", githubSub);
            insert.Parameters.AddWithValue("$login", (object?)login ?? DBNull.Value);
            insert.Parameters.AddWithValue("$avatar", (object?)avatarUrl ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }
        else
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE accounts SET credential_hash = $hash, display_name = $login, avatar_url = $avatar WHERE id = $id
                """;
            update.Parameters.AddWithValue("$hash", credentialHash);
            update.Parameters.AddWithValue("$login", (object?)login ?? DBNull.Value);
            update.Parameters.AddWithValue("$avatar", (object?)avatarUrl ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", accountId);
            await update.ExecuteNonQueryAsync(ct);
        }

        using var tokenInsert = connection.CreateCommand();
        tokenInsert.Transaction = transaction;
        tokenInsert.CommandText = """
            INSERT OR IGNORE INTO account_tokens (account_id, credential_hash, created_at) VALUES ($id, $hash, $now)
            """;
        tokenInsert.Parameters.AddWithValue("$id", accountId);
        tokenInsert.Parameters.AddWithValue("$hash", credentialHash);
        tokenInsert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await tokenInsert.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
