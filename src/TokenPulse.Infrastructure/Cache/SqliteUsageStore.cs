using System.Globalization;
using Microsoft.Data.Sqlite;
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;

namespace TokenPulse.Infrastructure.Cache;

/// <summary>
/// 顺序迁移：版本号递增，事务内执行，落账到 schema_migrations。
/// 迁移列表在代码内注册（新增迁移 = 在 <see cref="SqliteUsageStore.Migrations"/> 末尾追加一条）；
/// 已发布的迁移禁止修改——需要变更 schema 时追加新版本迁移。
/// </summary>
/// <param name="Version">版本号（递增，作为 schema_migrations 主键）。</param>
/// <param name="Name">迁移名（仅用于人读）。</param>
/// <param name="ApplyAsync">迁移体；在同一连接的事务内执行。</param>
internal sealed record Migration(int Version, string Name, Func<SqliteConnection, CancellationToken, Task> ApplyAsync);

/// <summary>
/// SQLite facts 存储（I-002）：usage_facts（day × grain × agent × model × project）+ session_facts，
/// 配 schema_migrations 顺序迁移 runner，作为所有页面查询的唯一数据面。
/// upsert 幂等：同一维度组合重复写入更新数值而非插新行（ON CONFLICT ... DO UPDATE，最新值生效）。
/// 旧版 JSON 快照库（dashboard_cache / daily_rows_cache）由迁移 1 直接重建为 facts schema：
/// 缓存可由采集重建，丢失可接受；用户设置在独立的 settings.json，不受影响。
/// </summary>
public class SqliteUsageStore : IUsageStore
{
    // 迁移列表：新增迁移 = 追加一条（版本号递增），不修改既有条目。
    private static readonly IReadOnlyList<Migration> Migrations = new Migration[]
    {
        new(
            Version: 1,
            Name: "create-facts-schema",
            ApplyAsync: async (connection, ct) =>
            {
                // 旧版缓存是 JSON 快照表，没有迁移框架可循：缓存可由采集（JSONL 扫描 / CLI 刷新）重建，
                // 直接重建为 facts schema，不做数据搬运（用户设置在独立的 settings.json，不受影响）。
                await ExecuteAsync(connection, """
                    DROP TABLE IF EXISTS dashboard_cache;
                    DROP TABLE IF EXISTS daily_rows_cache;
                    """, ct);

                await ExecuteAsync(connection, """
                    CREATE TABLE IF NOT EXISTS usage_facts (
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
                    )
                    """, ct);

                await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_usage_facts_day ON usage_facts (day)", ct);

                await ExecuteAsync(connection, """
                    CREATE TABLE IF NOT EXISTS session_facts (
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
                    """, ct);
            }),
    };

    private const string UpsertUsageFactSql = """
        INSERT INTO usage_facts (
            day, grain, agent, model, project, project_display_name,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
            cache_read_tokens, total_tokens, cost_usd, updated_at)
        VALUES (
            $day, $grain, $agent, $model, $project, $projectDisplayName,
            $inputTokens, $outputTokens, $cacheCreationTokens,
            $cacheCreationEphemeral5mTokens, $cacheCreationEphemeral1hTokens,
            $cacheReadTokens, $totalTokens, $costUsd, $updatedAt)
        ON CONFLICT(day, grain, agent, model, project) DO UPDATE SET
            project_display_name = excluded.project_display_name,
            input_tokens = excluded.input_tokens,
            output_tokens = excluded.output_tokens,
            cache_creation_tokens = excluded.cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens = excluded.cache_creation_ephemeral_5m_tokens,
            cache_creation_ephemeral_1h_tokens = excluded.cache_creation_ephemeral_1h_tokens,
            cache_read_tokens = excluded.cache_read_tokens,
            total_tokens = excluded.total_tokens,
            cost_usd = excluded.cost_usd,
            updated_at = excluded.updated_at
        """;

    private const string UpsertSessionFactSql = """
        INSERT INTO session_facts (
            session_id, agent, project, project_display_name, dominant_model,
            first_seen_at, last_seen_at,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
            cache_read_tokens, total_tokens, cost_usd, updated_at)
        VALUES (
            $sessionId, $agent, $project, $projectDisplayName, $dominantModel,
            $firstSeenAt, $lastSeenAt,
            $inputTokens, $outputTokens, $cacheCreationTokens,
            $cacheCreationEphemeral5mTokens, $cacheCreationEphemeral1hTokens,
            $cacheReadTokens, $totalTokens, $costUsd, $updatedAt)
        ON CONFLICT(session_id) DO UPDATE SET
            agent = excluded.agent,
            project = excluded.project,
            project_display_name = excluded.project_display_name,
            dominant_model = excluded.dominant_model,
            first_seen_at = excluded.first_seen_at,
            last_seen_at = excluded.last_seen_at,
            input_tokens = excluded.input_tokens,
            output_tokens = excluded.output_tokens,
            cache_creation_tokens = excluded.cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens = excluded.cache_creation_ephemeral_5m_tokens,
            cache_creation_ephemeral_1h_tokens = excluded.cache_creation_ephemeral_1h_tokens,
            cache_read_tokens = excluded.cache_read_tokens,
            total_tokens = excluded.total_tokens,
            cost_usd = excluded.cost_usd,
            updated_at = excluded.updated_at
        """;

    private const string SelectUsageFactsSql = """
        SELECT day, grain, agent, model, project, project_display_name,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
            cache_read_tokens, total_tokens, cost_usd
        FROM usage_facts
        ORDER BY day, grain, agent, model, project
        """;

    private const string SelectSessionFactsSql = """
        SELECT session_id, agent, project, project_display_name, dominant_model,
            first_seen_at, last_seen_at,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
            cache_read_tokens, total_tokens, cost_usd
        FROM session_facts
        ORDER BY session_id
        """;

    private readonly string _dbPath;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private Task? _initialized;

    public SqliteUsageStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    /// <summary>
    /// 执行顺序迁移（启动时调用；公开方法内部也会确保已执行）。
    /// 重复调用为空操作；每次调用迁移在一个事务内应用并落账，失败不落账、下次重试。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized != null)
        {
            return;
        }

        await _initializeLock.WaitAsync(ct);
        try
        {
            if (_initialized != null)
            {
                return;
            }

            await ApplyMigrationsAsync(ct);
            _initialized = Task.CompletedTask;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var updatedAt = FormatTimestamp(DateTimeOffset.UtcNow);

        foreach (var fact in facts)
        {
            await UpsertUsageFactAsync(connection, (SqliteTransaction)transaction, fact, updatedAt, ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var updatedAt = FormatTimestamp(DateTimeOffset.UtcNow);

        // 批次涉及的日期先清掉分解粒度行（日内消失的条目不残留），full 粒度行不动
        // —— 同日混源时读取侧以 full 粒度优先，保留它不会造成重复计数。
        foreach (var day in facts.Select(fact => fact.Day).Distinct(StringComparer.Ordinal))
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM usage_facts WHERE day = $day AND grain <> 'full'";
            delete.Parameters.AddWithValue("$day", day);
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var fact in facts)
        {
            await UpsertUsageFactAsync(connection, (SqliteTransaction)transaction, fact, updatedAt, ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static async Task UpsertUsageFactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageFact fact,
        string updatedAt,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = UpsertUsageFactSql;
        cmd.Parameters.AddWithValue("$day", fact.Day);
        cmd.Parameters.AddWithValue("$grain", fact.Grain);
        cmd.Parameters.AddWithValue("$agent", fact.Agent);
        cmd.Parameters.AddWithValue("$model", fact.Model);
        cmd.Parameters.AddWithValue("$project", fact.Project);
        cmd.Parameters.AddWithValue("$projectDisplayName", fact.ProjectDisplayName);
        cmd.Parameters.AddWithValue("$inputTokens", fact.InputTokens);
        cmd.Parameters.AddWithValue("$outputTokens", fact.OutputTokens);
        cmd.Parameters.AddWithValue("$cacheCreationTokens", fact.CacheCreationTokens);
        cmd.Parameters.AddWithValue("$cacheCreationEphemeral5mTokens", fact.CacheCreationEphemeral5mTokens);
        cmd.Parameters.AddWithValue("$cacheCreationEphemeral1hTokens", fact.CacheCreationEphemeral1hTokens);
        cmd.Parameters.AddWithValue("$cacheReadTokens", fact.CacheReadTokens);
        cmd.Parameters.AddWithValue("$totalTokens", fact.TotalTokens);
        AddCostParameter(cmd.Parameters, "$costUsd", fact.CostUsd);
        cmd.Parameters.AddWithValue("$updatedAt", updatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);

        var facts = new List<UsageFact>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectUsageFactsSql;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            facts.Add(new UsageFact
            {
                Day = reader.GetString(0),
                Grain = reader.GetString(1),
                Agent = reader.GetString(2),
                Model = reader.GetString(3),
                Project = reader.GetString(4),
                ProjectDisplayName = reader.GetString(5),
                InputTokens = reader.GetInt64(6),
                OutputTokens = reader.GetInt64(7),
                CacheCreationTokens = reader.GetInt64(8),
                CacheCreationEphemeral5mTokens = reader.GetInt64(9),
                CacheCreationEphemeral1hTokens = reader.GetInt64(10),
                CacheReadTokens = reader.GetInt64(11),
                TotalTokens = reader.GetInt64(12),
                CostUsd = ReadCost(reader, 13),
            });
        }

        return facts;
    }

    public async Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var updatedAt = FormatTimestamp(DateTimeOffset.UtcNow);

        foreach (var fact in facts)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = UpsertSessionFactSql;
            cmd.Parameters.AddWithValue("$sessionId", fact.SessionId);
            cmd.Parameters.AddWithValue("$agent", fact.Agent);
            cmd.Parameters.AddWithValue("$project", fact.Project);
            cmd.Parameters.AddWithValue("$projectDisplayName", fact.ProjectDisplayName);
            cmd.Parameters.AddWithValue("$dominantModel", fact.DominantModel);
            cmd.Parameters.AddWithValue("$firstSeenAt", FormatTimestamp(fact.FirstSeenAt));
            cmd.Parameters.AddWithValue("$lastSeenAt", FormatTimestamp(fact.LastSeenAt));
            cmd.Parameters.AddWithValue("$inputTokens", fact.InputTokens);
            cmd.Parameters.AddWithValue("$outputTokens", fact.OutputTokens);
            cmd.Parameters.AddWithValue("$cacheCreationTokens", fact.CacheCreationTokens);
            cmd.Parameters.AddWithValue("$cacheCreationEphemeral5mTokens", fact.CacheCreationEphemeral5mTokens);
            cmd.Parameters.AddWithValue("$cacheCreationEphemeral1hTokens", fact.CacheCreationEphemeral1hTokens);
            cmd.Parameters.AddWithValue("$cacheReadTokens", fact.CacheReadTokens);
            cmd.Parameters.AddWithValue("$totalTokens", fact.TotalTokens);
            AddCostParameter(cmd.Parameters, "$costUsd", fact.CostUsd);
            cmd.Parameters.AddWithValue("$updatedAt", updatedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);

        var facts = new List<SessionFact>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectSessionFactsSql;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            facts.Add(new SessionFact
            {
                SessionId = reader.GetString(0),
                Agent = reader.GetString(1),
                Project = reader.GetString(2),
                ProjectDisplayName = reader.GetString(3),
                DominantModel = reader.GetString(4),
                FirstSeenAt = ReadTimestamp(reader, 5),
                LastSeenAt = ReadTimestamp(reader, 6),
                InputTokens = reader.GetInt64(7),
                OutputTokens = reader.GetInt64(8),
                CacheCreationTokens = reader.GetInt64(9),
                CacheCreationEphemeral5mTokens = reader.GetInt64(10),
                CacheCreationEphemeral1hTokens = reader.GetInt64(11),
                CacheReadTokens = reader.GetInt64(12),
                TotalTokens = reader.GetInt64(13),
                CostUsd = ReadCost(reader, 14),
            });
        }

        return facts;
    }

    public async Task DeleteDecompositionFactsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM usage_facts WHERE grain <> 'full'";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ApplyMigrationsAsync(CancellationToken ct)
    {
        using var connection = await OpenAsync(ct);

        // schema_migrations 本身是 runner 自举表，不属于任何版本化迁移。
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            )
            """, ct);

        var appliedVersions = new HashSet<long>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM schema_migrations";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                appliedVersions.Add(reader.GetInt64(0));
            }
        }

        // 库比代码新（新版应用写的库被旧版打开）时明确报错，而不是静默跳过后因缺列崩溃。
        var maxKnownVersion = Migrations.Max(migration => migration.Version);
        var unknownVersion = appliedVersions.Where(version => version > maxKnownVersion).OrderBy(version => version).FirstOrDefault();
        if (unknownVersion > 0)
        {
            throw new NotSupportedException(
                $"Database schema version {unknownVersion} is newer than this build supports (max {maxKnownVersion}). Upgrade the app first.");
        }

        foreach (var migration in Migrations.Where(m => !appliedVersions.Contains(m.Version)).OrderBy(m => m.Version))
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await migration.ApplyAsync(connection, ct);

            // INSERT OR IGNORE：跨进程并发首迁时，双方 DDL 均为 IF NOT EXISTS 幂等，
            // 版本落账撞唯一约束视为"他方已完成"，不使启动失败。
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)transaction;
                cmd.CommandText = "INSERT OR IGNORE INTO schema_migrations (version, name, applied_at) VALUES ($version, $name, $appliedAt)";
                cmd.Parameters.AddWithValue("$version", migration.Version);
                cmd.Parameters.AddWithValue("$name", migration.Name);
                cmd.Parameters.AddWithValue("$appliedAt", FormatTimestamp(DateTimeOffset.UtcNow));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddCostParameter(SqliteParameterCollection parameters, string name, decimal? cost)
    {
        // decimal 以固定文化文本存储，保证精确往返（SQLite 无 decimal 原生类型）。
        parameters.AddWithValue(name, cost.HasValue ? cost.Value.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
    }

    private static decimal? ReadCost(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : decimal.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal)
    {
        return DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
