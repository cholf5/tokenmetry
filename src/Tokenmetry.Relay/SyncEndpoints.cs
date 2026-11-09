using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Relay;

/// <summary>
/// 中继同步端点（relay 计划 Task 6 / 设计 §4.2、§4.4）：push 按 (device, PK) LWW 落库 +
/// 账号级单调 seq 发号 + caller 设备 last_seen 更新；pull 返回其他设备的行增量
/// （账号级 seq 游标续拉、行数上限、hasMore）与全设备清单。
/// </summary>
internal static class SyncEndpoints
{
    /// <summary>单响应行数上限（设计 §7 留给 builder 的批量大小；自建文档记录）。</summary>
    public const int PullRowLimit = 2000;

    public static void MapSyncEndpoints(this WebApplication app)
    {
        app.MapPost("/api/sync/push", async (HttpContext context, RelayDatabase database) =>
            await PushAsync(
                database,
                (RelayAccount)context.Items[RelayAuth.AccountItemKey]!,
                context.Request,
                context.RequestAborted));

        app.MapGet("/api/sync/pull", async (HttpContext context, RelayDatabase database) =>
            await PullAsync(
                database,
                (RelayAccount)context.Items[RelayAuth.AccountItemKey]!,
                context.Request,
                context.RequestAborted));
    }

    private static async Task<IResult> PushAsync(RelayDatabase database, RelayAccount account, HttpRequest request, CancellationToken ct)
    {
        var deviceId = request.Headers["X-Tokenmetry-Device"].FirstOrDefault();
        if (string.IsNullOrEmpty(deviceId))
        {
            return Results.BadRequest(new { error = "X-Tokenmetry-Device header is required." });
        }

        SyncPushRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync(request.Body, SyncJsonContext.Default.SyncPushRequest, ct);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Malformed push payload." });
        }

        if (payload is null)
        {
            return Results.BadRequest(new { error = "Push payload is required." });
        }

        // 畸形载荷 400 化：缺省/显式 null 的两个列表按空批处理（幂等 no-op），不进 NRE 500。
        var usageFacts = payload.UsageFacts ?? [];
        var sessionFacts = payload.SessionFacts ?? [];

        // 行级校验前移到事务外（DTO 允许缺省，服务端是唯一闸门）：缺 UpdatedAt 的行无法做 LWW，
        // day 是 usage_rows/rollup 主键成分——乱码 day 会毒化榜单月窗口分桶，进库前锁死 yyyy-MM-dd。
        if (usageFacts.Any(fact => fact.UpdatedAt is null) || sessionFacts.Any(fact => fact.UpdatedAt is null))
        {
            return Results.BadRequest(new { error = "All pushed rows must carry updatedAt (LWW version)." });
        }

        if (usageFacts.Any(fact => !DateOnly.TryParseExact(
                fact.Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)))
        {
            return Results.BadRequest(new { error = "Usage row day must be a yyyy-MM-dd date." });
        }

        // 单写者不变量：push 只收归属 caller 设备自身的行（归属错行会污染其他设备的单写者 LWW）。
        if (usageFacts.Any(fact => fact.Device != deviceId)
            || sessionFacts.Any(fact => fact.Device != deviceId))
        {
            return Results.BadRequest(new { error = "All pushed rows must belong to the caller device." });
        }

        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        // 解绑粘滞到行摄取：只收已注册设备的行——被解绑设备的 push 直接 400，行不落库。
        // 否则行回到 usage_rows：pull 的 A13 对账反复清退（churn），且 opt-in 账号的榜单重算会把
        // 解绑时已级联清掉的设备数据重新吸回公开榜单，defeating 解绑触发的 RebuildAllDaysAsync。
        // 设备行不复活（TouchDeviceAsync UPDATE-only）+ 行不回流 = 解绑语义完整；重新入网只走注册。
        if (!await IsDeviceRegisteredAsync(connection, transaction, account.Id, deviceId, ct))
        {
            return Results.BadRequest(new { error = "Device is not registered. Register via POST /api/devices first." });
        }

        // 账号级单调 seq：usage + session 两表合并取 MAX，与 accounts.seq 计数器互相兜底。
        var seq = await MaxAccountSeqAsync(connection, transaction, account.Id, ct);

        foreach (var fact in usageFacts)
        {
            seq++;
            await UpsertUsageRowAsync(connection, transaction, account.Id, fact, seq, ct);
        }

        foreach (var fact in sessionFacts)
        {
            seq++;
            await UpsertSessionRowAsync(connection, transaction, account.Id, fact, seq, ct);
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = "UPDATE accounts SET seq = $seq WHERE id = $id AND seq < $seq";
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$id", account.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await TouchDeviceAsync(connection, transaction, account.Id, deviceId, ct);

        // 排行榜触发点（leaderboard 设计 §4.2）：batch 含 usage 行且账号 opt-in → 按 batch 涉及的
        // 去重 day 集合在既有写事务内增量重算 rollup；未 opt-in 直接跳过。session 行不进总量，不触发。
        if (usageFacts.Count > 0
            && await LeaderboardStore.IsOptedInAsync(connection, transaction, account.Id, ct))
        {
            await LeaderboardStore.RecomputeDaysAsync(
                connection,
                transaction,
                account.Id,
                usageFacts.Select(fact => fact.Day).Distinct().ToList(),
                ct);
        }

        await transaction.CommitAsync(ct);
        return Results.Ok();
    }

    private static async Task<IResult> PullAsync(RelayDatabase database, RelayAccount account, HttpRequest request, CancellationToken ct)
    {
        var deviceId = request.Headers["X-Tokenmetry-Device"].FirstOrDefault();
        if (string.IsNullOrEmpty(deviceId))
        {
            return Results.BadRequest(new { error = "X-Tokenmetry-Device header is required." });
        }

        if (!long.TryParse(request.Query["since"], out var since))
        {
            return Results.BadRequest(new { error = "since must be an integer seq." });
        }

        // limit 可调（设计 §4.4；钳到 [1, PullRowLimit]）——测试分页语义与部署调优用。
        // 非数字 limit 与 since、榜单公开端点同口径 400，不静默落默认值。
        var limit = PullRowLimit;
        if (request.Query.TryGetValue("limit", out var rawLimit))
        {
            if (!long.TryParse(rawLimit.FirstOrDefault(), out var parsedLimit))
            {
                return Results.BadRequest(new { error = "limit must be an integer." });
            }

            limit = (int)Math.Clamp(parsedLimit, 1, PullRowLimit);
        }

        await using var connection = database.OpenConnection();

        // 快照一致性（先读上界再读行，不靠事务）：先读游标上界（账号头部 seq），行读取钉在
        // (since, 上界] 窗口内——上界读之前的提交对后续行读必然可见（WAL 语句级快照单调），
        // 上界之后的提交被窗口挡在本批之外，拉尽游标 = 上界。旧做法（三读裸奔 + 拉尽取"当前头部"）
        // 里，行读与 seq 读之间落库的 push 会被游标越过：后续 pull 的 since ≥ 其 seq，永久跳行
        // （静默单侧少数据）。也不开读事务：Microsoft.Data.Sqlite 的 BEGIN 是 IMMEDIATE，
        // 读事务会握写锁挡住并发 push。
        var seqUpperBound = await AccountSeqAsync(connection, account.Id, ct);
        var response = await ReadPullBatchAsync(connection, account.Id, deviceId, since, seqUpperBound, limit, ct);
        return Results.Json(response, SyncJsonContext.Default.Options);
    }

    /// <summary>pull 的两表行读取 + 续拉游标 + 设备清单；行窗口钉在 (since, seqUpperBound]（快照一致性，见 PullAsync）。</summary>
    internal static async Task<SyncPullResponse> ReadPullBatchAsync(
        SqliteConnection connection,
        string accountId,
        string callerDevice,
        long since,
        long seqUpperBound,
        int limit,
        CancellationToken ct)
    {
        // 行数上限跨 usage/session 两表合并计（各 peek 一行探测 hasMore，探测行不返回）。
        var usage = await ReadUsageRowsAsync(connection, accountId, callerDevice, since, seqUpperBound, limit + 1, ct);
        var usageRows = usage.Take(limit).ToList();
        long? usageFrontier = usage.Count > limit ? usage[limit].Seq : null;

        var remaining = limit - usageRows.Count;
        var sessions = await ReadSessionRowsAsync(connection, accountId, callerDevice, since, seqUpperBound, remaining + 1, ct);
        long? sessionFrontier = sessions.Count > remaining ? sessions[remaining].Seq : null;
        var sessionRows = sessions.Take(remaining).ToList();

        var hasMore = usageFrontier is not null || sessionFrontier is not null;

        // 续拉游标（终审 C1）：seq 账号级交错于两表，按表分页的窗口边界之外可能还有更低 seq 的行——
        // 响应 Seq 必须取"两表首个被跳过行的最小 seq − 1"（水位线），而非本批最大 seq（那会永久跳行）。
        // 已交付的行在续拉中被重复返回是安全的：客户端落库走 LWW 幂等 upsert。
        // 拉尽时 = 游标上界：seq ≤ 上界的行在本批已全量覆盖，上界之后落库的留给下一轮
        // （caller 自身行的 seq 被跳过是安全的：未来他机行必然更大）。
        var responseSeq = hasMore
            ? new[] { usageFrontier, sessionFrontier }.Where(frontier => frontier is not null).Min()!.Value - 1
            : seqUpperBound;

        var devices = await ReadDevicesAsync(connection, accountId, ct);
        return new SyncPullResponse(
            responseSeq,
            usageRows.Select(row => row.Fact).ToList(),
            sessionRows.Select(row => row.Fact).ToList(),
            devices,
            hasMore);
    }

    private static async Task<long> MaxAccountSeqAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT MAX(m) FROM (
                SELECT (SELECT MAX(seq) FROM usage_rows WHERE account_id = $id) AS m
                UNION ALL
                SELECT (SELECT MAX(seq) FROM session_rows WHERE account_id = $id)
                UNION ALL
                SELECT (SELECT seq FROM accounts WHERE id = $id)
            )
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long value ? value : result is decimal decimalValue ? (long)decimalValue : 0;
    }

    private static async Task<long> AccountSeqAsync(SqliteConnection connection, string accountId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT seq FROM accounts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", accountId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long value ? value : 0;
    }

    private static async Task UpsertUsageRowAsync(
        SqliteConnection connection, SqliteTransaction transaction, string accountId, UsageFact fact, long seq, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO usage_rows (
                account_id, device_id, day, grain, agent, model, provider, project, project_display_name,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at, seq)
            VALUES (
                $accountId, $deviceId, $day, $grain, $agent, $model, $provider, $project, $projectDisplayName,
                $inputTokens, $outputTokens, $cacheCreationTokens,
                $cacheCreationEphemeral5mTokens, $cacheCreationEphemeral1hTokens,
                $cacheReadTokens, $totalTokens, $costUsd, $updatedAt, $seq)
            ON CONFLICT(account_id, device_id, day, grain, agent, model, provider, project) DO UPDATE SET
                project_display_name = excluded.project_display_name,
                input_tokens = excluded.input_tokens,
                output_tokens = excluded.output_tokens,
                cache_creation_tokens = excluded.cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens = excluded.cache_creation_ephemeral_5m_tokens,
                cache_creation_ephemeral_1h_tokens = excluded.cache_creation_ephemeral_1h_tokens,
                cache_read_tokens = excluded.cache_read_tokens,
                total_tokens = excluded.total_tokens,
                cost_usd = excluded.cost_usd,
                updated_at = excluded.updated_at,
                seq = excluded.seq
            WHERE excluded.updated_at > usage_rows.updated_at
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        BindUsageRowParameters(cmd, fact, seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertSessionRowAsync(
        SqliteConnection connection, SqliteTransaction transaction, string accountId, SessionFact fact, long seq, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO session_rows (
                account_id, device_id, session_id, agent, project, project_display_name, dominant_model,
                first_seen_at, last_seen_at,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at, seq)
            VALUES (
                $accountId, $deviceId, $sessionId, $agent, $project, $projectDisplayName, $dominantModel,
                $firstSeenAt, $lastSeenAt,
                $inputTokens, $outputTokens, $cacheCreationTokens,
                $cacheCreationEphemeral5mTokens, $cacheCreationEphemeral1hTokens,
                $cacheReadTokens, $totalTokens, $costUsd, $updatedAt, $seq)
            ON CONFLICT(account_id, session_id) DO UPDATE SET
                device_id = excluded.device_id,
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
                updated_at = excluded.updated_at,
                seq = excluded.seq
            WHERE excluded.updated_at > session_rows.updated_at
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        BindSessionRowParameters(cmd, fact, seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void BindUsageRowParameters(SqliteCommand cmd, UsageFact fact, long seq)
    {
        cmd.Parameters.AddWithValue("$deviceId", fact.Device);
        cmd.Parameters.AddWithValue("$day", fact.Day);
        cmd.Parameters.AddWithValue("$grain", fact.Grain);
        cmd.Parameters.AddWithValue("$agent", fact.Agent);
        cmd.Parameters.AddWithValue("$model", fact.Model);
        cmd.Parameters.AddWithValue("$provider", fact.Provider);
        cmd.Parameters.AddWithValue("$project", fact.Project);
        cmd.Parameters.AddWithValue("$projectDisplayName", fact.ProjectDisplayName);
        cmd.Parameters.AddWithValue("$inputTokens", fact.InputTokens);
        cmd.Parameters.AddWithValue("$outputTokens", fact.OutputTokens);
        cmd.Parameters.AddWithValue("$cacheCreationTokens", fact.CacheCreationTokens);
        cmd.Parameters.AddWithValue("$cacheCreationEphemeral5mTokens", fact.CacheCreationEphemeral5mTokens);
        cmd.Parameters.AddWithValue("$cacheCreationEphemeral1hTokens", fact.CacheCreationEphemeral1hTokens);
        cmd.Parameters.AddWithValue("$cacheReadTokens", fact.CacheReadTokens);
        cmd.Parameters.AddWithValue("$totalTokens", fact.TotalTokens);
        cmd.Parameters.AddWithValue("$costUsd", fact.CostUsd.HasValue ? fact.CostUsd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$updatedAt", fact.UpdatedAt?.ToString("O") ?? throw new ArgumentException("Sync payload row must carry UpdatedAt."));
        cmd.Parameters.AddWithValue("$seq", seq);
    }

    private static void BindSessionRowParameters(SqliteCommand cmd, SessionFact fact, long seq)
    {
        cmd.Parameters.AddWithValue("$deviceId", fact.Device);
        cmd.Parameters.AddWithValue("$sessionId", fact.SessionId);
        cmd.Parameters.AddWithValue("$agent", fact.Agent);
        cmd.Parameters.AddWithValue("$project", fact.Project);
        cmd.Parameters.AddWithValue("$projectDisplayName", fact.ProjectDisplayName);
        cmd.Parameters.AddWithValue("$dominantModel", fact.DominantModel);
        cmd.Parameters.AddWithValue("$firstSeenAt", fact.FirstSeenAt.ToString("O"));
        cmd.Parameters.AddWithValue("$lastSeenAt", fact.LastSeenAt.ToString("O"));
        cmd.Parameters.AddWithValue("$inputTokens", fact.InputTokens);
        cmd.Parameters.AddWithValue("$outputTokens", fact.OutputTokens);
        cmd.Parameters.AddWithValue("$cacheCreationTokens", fact.CacheCreationTokens);
        cmd.Parameters.AddWithValue("$cacheCreationEphemeral5mTokens", fact.CacheCreationEphemeral5mTokens);
        cmd.Parameters.AddWithValue("$cacheCreationEphemeral1hTokens", fact.CacheCreationEphemeral1hTokens);
        cmd.Parameters.AddWithValue("$cacheReadTokens", fact.CacheReadTokens);
        cmd.Parameters.AddWithValue("$totalTokens", fact.TotalTokens);
        cmd.Parameters.AddWithValue("$costUsd", fact.CostUsd.HasValue ? fact.CostUsd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$updatedAt", fact.UpdatedAt?.ToString("O") ?? throw new ArgumentException("Sync payload row must carry UpdatedAt."));
        cmd.Parameters.AddWithValue("$seq", seq);
    }

    private static async Task<bool> IsDeviceRegisteredAsync(
        SqliteConnection connection, SqliteTransaction transaction, string accountId, string deviceId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT 1 FROM devices WHERE account_id = $accountId AND device_id = $deviceId";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$deviceId", deviceId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task TouchDeviceAsync(
        SqliteConnection connection, SqliteTransaction transaction, string accountId, string deviceId, CancellationToken ct)
    {
        // 只更新已注册设备的 last_seen（终审 C2：INSERT 会让被解绑设备的 push 把自己"复活"回设备清单，
        // A13 对账永远清不掉它；重新入网只走 POST /api/devices 注册）。
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE devices SET last_seen_at = $now WHERE account_id = $accountId AND device_id = $deviceId";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$deviceId", deviceId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<(long Seq, UsageFact Fact)>> ReadUsageRowsAsync(
        SqliteConnection connection, string accountId, string callerDevice, long since, long seqUpperBound, int limit, CancellationToken ct)
    {
        var rows = new List<(long, UsageFact)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT seq, device_id, day, grain, agent, model, provider, project, project_display_name,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at
            FROM usage_rows
            WHERE account_id = $accountId AND seq > $since AND seq <= $upperBound AND device_id <> $caller
            ORDER BY seq
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$upperBound", seqUpperBound);
        cmd.Parameters.AddWithValue("$caller", callerDevice);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var ordinal = 1;
            var fact = new UsageFact
            {
                Device = reader.GetString(ordinal++),
                Day = reader.GetString(ordinal++),
                Grain = reader.GetString(ordinal++),
                Agent = reader.GetString(ordinal++),
                Model = reader.GetString(ordinal++),
                Provider = reader.GetString(ordinal++),
                Project = reader.GetString(ordinal++),
                ProjectDisplayName = reader.GetString(ordinal++),
                InputTokens = reader.GetInt64(ordinal++),
                OutputTokens = reader.GetInt64(ordinal++),
                CacheCreationTokens = reader.GetInt64(ordinal++),
                CacheCreationEphemeral5mTokens = reader.GetInt64(ordinal++),
                CacheCreationEphemeral1hTokens = reader.GetInt64(ordinal++),
                CacheReadTokens = reader.GetInt64(ordinal++),
                TotalTokens = reader.GetInt64(ordinal++),
                CostUsd = reader.IsDBNull(ordinal) ? null : decimal.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture),
            };
            ordinal++;
            fact.UpdatedAt = DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            rows.Add((reader.GetInt64(0), fact));
        }

        return rows;
    }

    private static async Task<List<(long Seq, SessionFact Fact)>> ReadSessionRowsAsync(
        SqliteConnection connection, string accountId, string callerDevice, long since, long seqUpperBound, int limit, CancellationToken ct)
    {
        var rows = new List<(long, SessionFact)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT seq, device_id, session_id, agent, project, project_display_name, dominant_model,
                first_seen_at, last_seen_at,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at
            FROM session_rows
            WHERE account_id = $accountId AND seq > $since AND seq <= $upperBound AND device_id <> $caller
            ORDER BY seq
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$upperBound", seqUpperBound);
        cmd.Parameters.AddWithValue("$caller", callerDevice);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var ordinal = 1;
            var fact = new SessionFact
            {
                Device = reader.GetString(ordinal++),
                SessionId = reader.GetString(ordinal++),
                Agent = reader.GetString(ordinal++),
                Project = reader.GetString(ordinal++),
                ProjectDisplayName = reader.GetString(ordinal++),
                DominantModel = reader.GetString(ordinal++),
                FirstSeenAt = DateTimeOffset.Parse(reader.GetString(ordinal++), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                LastSeenAt = DateTimeOffset.Parse(reader.GetString(ordinal++), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                InputTokens = reader.GetInt64(ordinal++),
                OutputTokens = reader.GetInt64(ordinal++),
                CacheCreationTokens = reader.GetInt64(ordinal++),
                CacheCreationEphemeral5mTokens = reader.GetInt64(ordinal++),
                CacheCreationEphemeral1hTokens = reader.GetInt64(ordinal++),
                CacheReadTokens = reader.GetInt64(ordinal++),
                TotalTokens = reader.GetInt64(ordinal++),
                CostUsd = reader.IsDBNull(ordinal) ? null : decimal.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture),
            };
            ordinal++;
            fact.UpdatedAt = DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            rows.Add((reader.GetInt64(0), fact));
        }

        return rows;
    }

    internal static async Task<List<SyncDeviceInfo>> ReadDevicesAsync(SqliteConnection connection, string accountId, CancellationToken ct)
    {
        var devices = new List<SyncDeviceInfo>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT device_id, name, last_seen_at FROM devices WHERE account_id = $accountId ORDER BY device_id";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            devices.Add(new SyncDeviceInfo(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return devices;
    }
}
