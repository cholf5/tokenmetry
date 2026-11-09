using System.Globalization;
using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Infrastructure.Cache;

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
    // 迁移纪律（2026-10-16 定稿，新迁移动手前必读）：
    // 1. 改表 = 在末尾追加新版本迁移，不修改既有条目；全局版本就是 schema_migrations 账本，
    //    版本判断收敛在 ApplyMigrationsAsync 一处，业务 / 查询代码永远只面对当前语义——
    //    哪天需要按数据版本分支，说明漏了一个迁移，补迁移而不是在代码里分支。
    // 2. 迁移必须让旧语义的行在迁移后消失：
    //    - 新维度进主键（行身份变了）→ 旧行要么在迁移内 SQL 转换，要么丢弃。丢弃仅在
    //      "数据仍可从上游重建"的窗口内合法（本库龄 < 上游日志保留期；上游会清旧数据，
    //      超窗后本库就是那段历史唯一的存放处，不得再清）。
    //    - 新维度不进主键 → 回填的默认值必须语义诚实（如 provider '' = 未检测），无需转换。
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

        new(
            Version: 2,
            Name: "usage-facts-provider-dimension",
            ApplyAsync: async (connection, ct) =>
            {
                // I-036：三元组 agent × model × provider —— usage_facts 增 provider 维度并进入主键，
                // 使"同一模型经不同 provider（openrouter / z.ai / 自定义源）使用"成为可区分的行
                // （不同 provider 价目可能不同，混成一行则成本无法对账）。
                // 主键变更 SQLite 不支持 ALTER；且 provider 进入主键后，旧行（无 provider 维度）与新行
                // 主键不同、upsert 永不合并，原样搬数据 = 升级后历史用量在所有聚合里双算。所以迁移
                // 不搬数据，整表重建为空——升级后由本地采集首轮全量重建（增量扫描器每轮返回全量记录）。
                // 这是上面纪律第 2 条"丢弃"分支的一次特赦：本迁移发布时库龄尚在上游日志保留期内，
                // 全部数据可重建；此后不得再清（见 Migrations 列表旁的迁移纪律）。
                await ExecuteAsync(connection, """
                    DROP TABLE usage_facts;
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
                    """, ct);

                await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_usage_facts_day ON usage_facts (day)", ct);
            }),

        new(
            Version: 3,
            Name: "device-dimension",
            ApplyAsync: async (connection, ct) =>
            {
                // 跨端同步地基（relay 设计 §4.1）：device 进 usage_facts 主键，行身份变为
                // (device, day, grain, agent, model, provider, project)——两台机器同天同模型同名项目
                // 不再撞键。与迁移 2 的"丢弃"分支不同，这是纪律第 2 条的"转换"分支：
                // 历史行全部保留、device 回填本机 id（迁移时刻不存在远端行，回填即全量）。
                // 设备 id 无条件生成（不只在开启同步时）——迁移无条件执行才诚实，
                // 中继只有在用户开启同步后才见到它。
                var deviceId = Guid.NewGuid().ToString("N");

                // 自举表与 schema_migrations 同级，不进版本化迁移的语义；
                // last_pushed_at / last_pull_seq 是同步游标（Task 2 的 Get/SetSyncCursor 落点）。
                await ExecuteAsync(connection, """
                    CREATE TABLE IF NOT EXISTS device_registry (
                        device_id TEXT PRIMARY KEY,
                        name TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        last_pushed_at TEXT,
                        last_pull_seq INTEGER
                    )
                    """, ct);

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR IGNORE INTO device_registry (device_id, name, created_at) VALUES ($deviceId, $name, $createdAt)";
                    cmd.Parameters.AddWithValue("$deviceId", deviceId);
                    cmd.Parameters.AddWithValue("$name", Environment.MachineName);
                    cmd.Parameters.AddWithValue("$createdAt", FormatTimestamp(DateTimeOffset.UtcNow));
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await ExecuteAsync(connection, """
                    CREATE TABLE IF NOT EXISTS remote_devices (
                        device_id TEXT PRIMARY KEY,
                        name TEXT NOT NULL,
                        last_seen_at TEXT NOT NULL
                    )
                    """, ct);

                // usage_facts 整表重建：device 进主键，INSERT...SELECT 原样搬数 + 回填本机 id。
                await ExecuteAsync(connection, """
                    CREATE TABLE usage_facts_new (
                        device TEXT NOT NULL,
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
                        PRIMARY KEY (device, day, grain, agent, model, provider, project)
                    )
                    """, ct);

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO usage_facts_new (
                            device, day, grain, agent, model, provider, project, project_display_name,
                            input_tokens, output_tokens, cache_creation_tokens,
                            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                            cache_read_tokens, total_tokens, cost_usd, updated_at)
                        SELECT $device, day, grain, agent, model, provider, project, project_display_name,
                            input_tokens, output_tokens, cache_creation_tokens,
                            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                            cache_read_tokens, total_tokens, cost_usd, updated_at
                        FROM usage_facts
                        """;
                    cmd.Parameters.AddWithValue("$device", deviceId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await ExecuteAsync(connection, "DROP TABLE usage_facts", ct);
                await ExecuteAsync(connection, "ALTER TABLE usage_facts_new RENAME TO usage_facts", ct);
                await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_usage_facts_day ON usage_facts (day)", ct);

                // session_facts：device 只作属性列（session_id 已全局唯一，进主键无收益），
                // 回填默认值语义诚实：迁移时刻库内全部是本机行。
                await ExecuteAsync(connection, "ALTER TABLE session_facts ADD COLUMN device TEXT NOT NULL DEFAULT ''", ct);
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "UPDATE session_facts SET device = $device";
                    cmd.Parameters.AddWithValue("$device", deviceId);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_session_facts_device ON session_facts (device)", ct);
            }),
    };

    private const string UpsertUsageFactSql = """
        INSERT INTO usage_facts (
            device, day, grain, agent, model, provider, project, project_display_name,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
            cache_read_tokens, total_tokens, cost_usd, updated_at)
        VALUES (
            $device, $day, $grain, $agent, $model, $provider, $project, $projectDisplayName,
            $inputTokens, $outputTokens, $cacheCreationTokens,
            $cacheCreationEphemeral5mTokens, $cacheCreationEphemeral1hTokens,
            $cacheReadTokens, $totalTokens, $costUsd, $updatedAt)
        ON CONFLICT(device, day, grain, agent, model, provider, project) DO UPDATE SET
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
        -- A14 值变化检测：内容与现值完全一致时不更新（updated_at 保持原值），
        -- 否则每轮全量重聚合会把整个历史时间戳刷新，增量推送退化为每轮全量上传。
        -- IS NOT 为 NULL 安全的相等比较（cost_usd 常为 NULL）。
        WHERE excluded.project_display_name IS NOT project_display_name
           OR excluded.input_tokens IS NOT input_tokens
           OR excluded.output_tokens IS NOT output_tokens
           OR excluded.cache_creation_tokens IS NOT cache_creation_tokens
           OR excluded.cache_creation_ephemeral_5m_tokens IS NOT cache_creation_ephemeral_5m_tokens
           OR excluded.cache_creation_ephemeral_1h_tokens IS NOT cache_creation_ephemeral_1h_tokens
           OR excluded.cache_read_tokens IS NOT cache_read_tokens
           OR excluded.total_tokens IS NOT total_tokens
           OR excluded.cost_usd IS NOT cost_usd
        """;

    // 远端行落地（relay 设计 §4.2）：原样 device + updated_at，LWW 守卫——
    // 仅当载荷时间戳更新时覆盖（半途的旧批次不得覆盖新批次）。
    // 两端皆 UTC "O" 格式文本，同 offset 下字典序即时间序（Review Focus #5）。
    private const string UpsertRemoteUsageFactSql = """
        INSERT INTO usage_facts (
            device, day, grain, agent, model, provider, project, project_display_name,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
            cache_read_tokens, total_tokens, cost_usd, updated_at)
        VALUES (
            $device, $day, $grain, $agent, $model, $provider, $project, $projectDisplayName,
            $inputTokens, $outputTokens, $cacheCreationTokens,
            $cacheCreationEphemeral5mTokens, $cacheCreationEphemeral1hTokens,
            $cacheReadTokens, $totalTokens, $costUsd, $updatedAt)
        ON CONFLICT(device, day, grain, agent, model, provider, project) DO UPDATE SET
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
        WHERE excluded.updated_at > usage_facts.updated_at
        """;

    private const string UpsertSessionFactSql = """
        INSERT INTO session_facts (
            device, session_id, agent, project, project_display_name, dominant_model,
            first_seen_at, last_seen_at,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
            cache_read_tokens, total_tokens, cost_usd, updated_at)
        VALUES (
            $device, $sessionId, $agent, $project, $projectDisplayName, $dominantModel,
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
        -- A14 值变化检测（同 usage 行；IS NOT 为 NULL 安全比较）
        WHERE excluded.agent IS NOT agent
           OR excluded.project IS NOT project
           OR excluded.project_display_name IS NOT project_display_name
           OR excluded.dominant_model IS NOT dominant_model
           OR excluded.first_seen_at IS NOT first_seen_at
           OR excluded.last_seen_at IS NOT last_seen_at
           OR excluded.input_tokens IS NOT input_tokens
           OR excluded.output_tokens IS NOT output_tokens
           OR excluded.cache_creation_tokens IS NOT cache_creation_tokens
           OR excluded.cache_creation_ephemeral_5m_tokens IS NOT cache_creation_ephemeral_5m_tokens
           OR excluded.cache_creation_ephemeral_1h_tokens IS NOT cache_creation_ephemeral_1h_tokens
           OR excluded.cache_read_tokens IS NOT cache_read_tokens
           OR excluded.total_tokens IS NOT total_tokens
           OR excluded.cost_usd IS NOT cost_usd
        """;

    // 远端会话行落地：LWW 同 usage（session_id 全局唯一，冲突目标不含 device）
    private const string UpsertRemoteSessionFactSql = """
        INSERT INTO session_facts (
            device, session_id, agent, project, project_display_name, dominant_model,
            first_seen_at, last_seen_at,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
            cache_read_tokens, total_tokens, cost_usd, updated_at)
        VALUES (
            $device, $sessionId, $agent, $project, $projectDisplayName, $dominantModel,
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
        WHERE excluded.updated_at > session_facts.updated_at
        """;

    private const string SelectUsageFactsSql = """
        SELECT device, day, grain, agent, model, provider, project, project_display_name,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
            cache_read_tokens, total_tokens, cost_usd, updated_at
        FROM usage_facts
        ORDER BY device, day, grain, agent, model, provider, project
        """;

    private const string SelectSessionFactsSql = """
        SELECT device, session_id, agent, project, project_display_name, dominant_model,
            first_seen_at, last_seen_at,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
            cache_read_tokens, total_tokens, cost_usd, updated_at
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

    /// <inheritdoc />
    public async Task<string> GetLocalDeviceIdAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT device_id FROM device_registry LIMIT 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string
            ?? throw new InvalidOperationException("device_registry is empty: device migration did not run.");
    }

    public async Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var updatedAt = FormatTimestamp(DateTimeOffset.UtcNow);
        var deviceId = GetLocalDeviceId(connection);

        foreach (var fact in facts)
        {
            await UpsertUsageFactAsync(connection, (SqliteTransaction)transaction, fact, deviceId, updatedAt, ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var updatedAt = FormatTimestamp(DateTimeOffset.UtcNow);
        var deviceId = GetLocalDeviceId(connection);

        // 批次涉及的日期先清掉分解粒度行（日内消失的条目不残留），full 粒度行不动
        // —— 同日混源时读取侧以 full 粒度优先，保留它不会造成重复计数。
        // 作用域收口（§4.3 地雷 2）：只清本机行，远端设备的同日分解行不得误删。
        foreach (var day in facts.Select(fact => fact.Day).Distinct(StringComparer.Ordinal))
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM usage_facts WHERE day = $day AND grain <> 'full' AND device = $device";
            delete.Parameters.AddWithValue("$day", day);
            delete.Parameters.AddWithValue("$device", deviceId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var fact in facts)
        {
            await UpsertUsageFactAsync(connection, (SqliteTransaction)transaction, fact, deviceId, updatedAt, ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static async Task UpsertUsageFactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageFact fact,
        string deviceId,
        string updatedAt,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = UpsertUsageFactSql;
        cmd.Parameters.AddWithValue("$device", deviceId);
        BindUsageFactParameters(cmd.Parameters, fact, updatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>usage 行除 $device 外的全部参数绑定（本地/远端 upsert 共用；两者 SQL 形状一致）。</summary>
    private static void BindUsageFactParameters(SqliteParameterCollection parameters, UsageFact fact, string updatedAt)
    {
        parameters.AddWithValue("$day", fact.Day);
        parameters.AddWithValue("$grain", fact.Grain);
        parameters.AddWithValue("$agent", fact.Agent);
        parameters.AddWithValue("$model", fact.Model);
        parameters.AddWithValue("$provider", fact.Provider);
        parameters.AddWithValue("$project", fact.Project);
        parameters.AddWithValue("$projectDisplayName", fact.ProjectDisplayName);
        parameters.AddWithValue("$inputTokens", fact.InputTokens);
        parameters.AddWithValue("$outputTokens", fact.OutputTokens);
        parameters.AddWithValue("$cacheCreationTokens", fact.CacheCreationTokens);
        parameters.AddWithValue("$cacheCreationEphemeral5mTokens", fact.CacheCreationEphemeral5mTokens);
        parameters.AddWithValue("$cacheCreationEphemeral1hTokens", fact.CacheCreationEphemeral1hTokens);
        parameters.AddWithValue("$cacheReadTokens", fact.CacheReadTokens);
        parameters.AddWithValue("$totalTokens", fact.TotalTokens);
        AddCostParameter(parameters, "$costUsd", fact.CostUsd);
        parameters.AddWithValue("$updatedAt", updatedAt);
    }

    private static UsageFact MapUsageFact(SqliteDataReader reader)
    {
        return new UsageFact
        {
            Device = reader.GetString(0),
            Day = reader.GetString(1),
            Grain = reader.GetString(2),
            Agent = reader.GetString(3),
            Model = reader.GetString(4),
            Provider = reader.GetString(5),
            Project = reader.GetString(6),
            ProjectDisplayName = reader.GetString(7),
            InputTokens = reader.GetInt64(8),
            OutputTokens = reader.GetInt64(9),
            CacheCreationTokens = reader.GetInt64(10),
            CacheCreationEphemeral5mTokens = reader.GetInt64(11),
            CacheCreationEphemeral1hTokens = reader.GetInt64(12),
            CacheReadTokens = reader.GetInt64(13),
            TotalTokens = reader.GetInt64(14),
            CostUsd = ReadCost(reader, 15),
            UpdatedAt = ReadTimestamp(reader, 16),
        };
    }

    private static SessionFact MapSessionFact(SqliteDataReader reader)
    {
        return new SessionFact
        {
            Device = reader.GetString(0),
            SessionId = reader.GetString(1),
            Agent = reader.GetString(2),
            Project = reader.GetString(3),
            ProjectDisplayName = reader.GetString(4),
            DominantModel = reader.GetString(5),
            FirstSeenAt = ReadTimestamp(reader, 6),
            LastSeenAt = ReadTimestamp(reader, 7),
            InputTokens = reader.GetInt64(8),
            OutputTokens = reader.GetInt64(9),
            CacheCreationTokens = reader.GetInt64(10),
            CacheCreationEphemeral5mTokens = reader.GetInt64(11),
            CacheCreationEphemeral1hTokens = reader.GetInt64(12),
            CacheReadTokens = reader.GetInt64(13),
            TotalTokens = reader.GetInt64(14),
            CostUsd = ReadCost(reader, 15),
            UpdatedAt = ReadTimestamp(reader, 16),
        };
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
            facts.Add(MapUsageFact(reader));
        }

        return facts;
    }

    public async Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var updatedAt = FormatTimestamp(DateTimeOffset.UtcNow);
        var deviceId = GetLocalDeviceId(connection);

        foreach (var fact in facts)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = UpsertSessionFactSql;
            cmd.Parameters.AddWithValue("$device", deviceId);
            BindSessionFactParameters(cmd.Parameters, fact, updatedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>session 行除 $device 外的全部参数绑定（本地/远端 upsert 共用）。</summary>
    private static void BindSessionFactParameters(SqliteParameterCollection parameters, SessionFact fact, string updatedAt)
    {
        parameters.AddWithValue("$sessionId", fact.SessionId);
        parameters.AddWithValue("$agent", fact.Agent);
        parameters.AddWithValue("$project", fact.Project);
        parameters.AddWithValue("$projectDisplayName", fact.ProjectDisplayName);
        parameters.AddWithValue("$dominantModel", fact.DominantModel);
        parameters.AddWithValue("$firstSeenAt", FormatTimestamp(fact.FirstSeenAt));
        parameters.AddWithValue("$lastSeenAt", FormatTimestamp(fact.LastSeenAt));
        parameters.AddWithValue("$inputTokens", fact.InputTokens);
        parameters.AddWithValue("$outputTokens", fact.OutputTokens);
        parameters.AddWithValue("$cacheCreationTokens", fact.CacheCreationTokens);
        parameters.AddWithValue("$cacheCreationEphemeral5mTokens", fact.CacheCreationEphemeral5mTokens);
        parameters.AddWithValue("$cacheCreationEphemeral1hTokens", fact.CacheCreationEphemeral1hTokens);
        parameters.AddWithValue("$cacheReadTokens", fact.CacheReadTokens);
        parameters.AddWithValue("$totalTokens", fact.TotalTokens);
        AddCostParameter(parameters, "$costUsd", fact.CostUsd);
        parameters.AddWithValue("$updatedAt", updatedAt);
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
            facts.Add(MapSessionFact(reader));
        }

        return facts;
    }

    public async Task DeleteDecompositionFactsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        // 作用域收口（§4.3 地雷 3）：只清本机分解行。
        var deviceId = GetLocalDeviceId(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM usage_facts WHERE grain <> 'full' AND device = $device";
        cmd.Parameters.AddWithValue("$device", deviceId);
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

    /// <summary>连接内读取本机设备 id（device_registry 单行；迁移 3 自举）。</summary>
    private static string GetLocalDeviceId(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT device_id FROM device_registry LIMIT 1";
        return cmd.ExecuteScalar() as string
            ?? throw new InvalidOperationException("device_registry is empty: device migration did not run.");
    }

    // ---- 跨端同步存储面（relay 设计 §4.2/§4.3）----

    /// <inheritdoc />
    public async Task<string> GetLocalDeviceNameAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM device_registry WHERE device_id = $id";
        cmd.Parameters.AddWithValue("$id", GetLocalDeviceId(connection));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string ?? string.Empty;
    }

    /// <inheritdoc />
    public async Task SetLocalDeviceNameAsync(string name, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)transaction;
        cmd.CommandText = "UPDATE device_registry SET name = $name WHERE device_id = $id";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$id", GetLocalDeviceId(connection));
        await cmd.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpsertRemoteUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var fact in facts)
        {
            if (!fact.UpdatedAt.HasValue)
            {
                throw new ArgumentException("Remote usage fact must carry UpdatedAt (sync payload contract).", nameof(facts));
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = UpsertRemoteUsageFactSql;
            cmd.Parameters.AddWithValue("$device", fact.Device);
            BindUsageFactParameters(cmd.Parameters, fact, FormatTimestamp(fact.UpdatedAt.Value));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpsertRemoteSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var fact in facts)
        {
            if (!fact.UpdatedAt.HasValue)
            {
                throw new ArgumentException("Remote session fact must carry UpdatedAt (sync payload contract).", nameof(facts));
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = UpsertRemoteSessionFactSql;
            cmd.Parameters.AddWithValue("$device", fact.Device);
            BindSessionFactParameters(cmd.Parameters, fact, FormatTimestamp(fact.UpdatedAt.Value));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsageFact>> GetLocalUsageFactsSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        var facts = new List<UsageFact>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT device, day, grain, agent, model, provider, project, project_display_name,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at
            FROM usage_facts
            WHERE device = $device AND updated_at > $since
            ORDER BY updated_at, day, grain, agent, model, provider, project
            """;
        cmd.Parameters.AddWithValue("$device", GetLocalDeviceId(connection));
        cmd.Parameters.AddWithValue("$since", FormatTimestamp(since));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            facts.Add(MapUsageFact(reader));
        }

        return facts;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionFact>> GetLocalSessionFactsSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        var facts = new List<SessionFact>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT device, session_id, agent, project, project_display_name, dominant_model,
                first_seen_at, last_seen_at,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at
            FROM session_facts
            WHERE device = $device AND updated_at > $since
            ORDER BY updated_at, session_id
            """;
        cmd.Parameters.AddWithValue("$device", GetLocalDeviceId(connection));
        cmd.Parameters.AddWithValue("$since", FormatTimestamp(since));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            facts.Add(MapSessionFact(reader));
        }

        return facts;
    }

    /// <inheritdoc />
    public async Task<(DateTimeOffset? LastPushedAt, long LastPullSeq)> GetSyncCursorAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT last_pushed_at, last_pull_seq FROM device_registry WHERE device_id = $id";
        cmd.Parameters.AddWithValue("$id", GetLocalDeviceId(connection));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (null, 0);
        }

        DateTimeOffset? lastPushedAt = reader.IsDBNull(0) ? null : ReadTimestamp(reader, 0);
        var lastPullSeq = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        return (lastPushedAt, lastPullSeq);
    }

    /// <inheritdoc />
    public async Task SetSyncCursorAsync(DateTimeOffset? lastPushedAt, long? lastPullSeq, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)transaction;
        cmd.CommandText = "UPDATE device_registry SET last_pushed_at = $pushed, last_pull_seq = $pull WHERE device_id = $id";
        cmd.Parameters.AddWithValue("$pushed", lastPushedAt.HasValue ? FormatTimestamp(lastPushedAt.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$pull", lastPullSeq.HasValue ? lastPullSeq.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$id", GetLocalDeviceId(connection));
        await cmd.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncDeviceInfo>> GetRemoteDevicesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        var devices = new List<SyncDeviceInfo>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT device_id, name, last_seen_at FROM remote_devices ORDER BY device_id";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            devices.Add(new SyncDeviceInfo(reader.GetString(0), reader.GetString(1), ReadTimestamp(reader, 2)));
        }

        return devices;
    }

    /// <inheritdoc />
    public async Task UpsertRemoteDevicesAsync(IReadOnlyList<SyncDeviceInfo> devices, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var device in devices)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = """
                INSERT INTO remote_devices (device_id, name, last_seen_at) VALUES ($id, $name, $seen)
                ON CONFLICT(device_id) DO UPDATE SET
                    name = excluded.name,
                    last_seen_at = excluded.last_seen_at
                """;
            cmd.Parameters.AddWithValue("$id", device.DeviceId);
            cmd.Parameters.AddWithValue("$name", device.Name);
            cmd.Parameters.AddWithValue("$seen", FormatTimestamp(device.LastSeenAt));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteRemoteDeviceDataAsync(string deviceId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var sql in new[]
                 {
                     "DELETE FROM remote_devices WHERE device_id = $id",
                     "DELETE FROM usage_facts WHERE device = $id",
                     "DELETE FROM session_facts WHERE device = $id",
                 })
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", deviceId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
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
        // 统一归一到 UTC "O" 文本：updated_at 的 LWW 与 since 过滤依赖文本序数比较，
        // 混入非零 offset 的 "O" 串会使字典序偏离时间序（relay 设计 Review Focus #5）。
        return timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal)
    {
        return DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
