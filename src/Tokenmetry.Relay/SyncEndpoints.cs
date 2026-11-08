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

        // 单写者不变量：push 只收归属 caller 设备自身的行（归属错行会污染其他设备的单写者 LWW）。
        if (payload.UsageFacts.Any(fact => fact.Device != deviceId)
            || payload.SessionFacts.Any(fact => fact.Device != deviceId))
        {
            return Results.BadRequest(new { error = "All pushed rows must belong to the caller device." });
        }

        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        // 账号级单调 seq：usage + session 两表合并取 MAX，与 accounts.seq 计数器互相兜底。
        var seq = await MaxAccountSeqAsync(connection, transaction, account.Id, ct);

        foreach (var fact in payload.UsageFacts)
        {
            seq++;
            await UpsertUsageRowAsync(connection, transaction, account.Id, fact, seq, ct);
        }

        foreach (var fact in payload.SessionFacts)
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

        await using var connection = database.OpenConnection();

        // 行数上限跨 usage/session 两表合并计（peek 一行探测 hasMore，探测行不返回）。
        var usage = await ReadUsageRowsAsync(connection, account.Id, deviceId, since, PullRowLimit + 1, ct);
        var hasMore = usage.Count > PullRowLimit;
        var usageRows = usage.Take(PullRowLimit).ToList();

        var remaining = PullRowLimit - usageRows.Count;
        var sessions = await ReadSessionRowsAsync(connection, account.Id, deviceId, since, remaining + 1, ct);
        if (sessions.Count > remaining)
        {
            hasMore = true;
            sessions = sessions.Take(remaining).ToList();
        }

        // hasMore 时响应 Seq = 本批最大 seq（客户端续拉游标，不得跳过未拉行）；
        // 拉尽时 = 账号当前头部（caller 自身行的 seq 被跳过是安全的：未来他机行必然更大）。
        var responseSeq = hasMore
            ? Enumerable.Concat(usageRows.Select(row => row.Seq), sessions.Select(row => row.Seq)).DefaultIfEmpty(since).Max()
            : await AccountSeqAsync(connection, account.Id, ct);

        var devices = await ReadDevicesAsync(connection, account.Id, ct);
        return Results.Json(
            new SyncPullResponse(responseSeq, usageRows.Select(row => row.Fact).ToList(), sessions.Select(row => row.Fact).ToList(), devices, hasMore),
            SyncJsonContext.Default.Options);
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

    private static async Task TouchDeviceAsync(
        SqliteConnection connection, SqliteTransaction transaction, string accountId, string deviceId, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO devices (account_id, device_id, name, last_seen_at) VALUES ($accountId, $deviceId, $deviceId, $now)
            ON CONFLICT(account_id, device_id) DO UPDATE SET last_seen_at = excluded.last_seen_at
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$deviceId", deviceId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<(long Seq, UsageFact Fact)>> ReadUsageRowsAsync(
        SqliteConnection connection, string accountId, string callerDevice, long since, int limit, CancellationToken ct)
    {
        var rows = new List<(long, UsageFact)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT seq, device_id, day, grain, agent, model, provider, project, project_display_name,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at
            FROM usage_rows
            WHERE account_id = $accountId AND seq > $since AND device_id <> $caller
            ORDER BY seq
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$since", since);
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
        SqliteConnection connection, string accountId, string callerDevice, long since, int limit, CancellationToken ct)
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
            WHERE account_id = $accountId AND seq > $since AND device_id <> $caller
            ORDER BY seq
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$since", since);
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
