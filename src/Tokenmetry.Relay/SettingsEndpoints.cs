using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Relay;

/// <summary>
/// 应用设置跨设备同步端点（settings-sync 设计 §4.2）：逐 key 的纯偏好文本，Bearer 认证、无 CORS。
/// 写入时间戳由服务端赋值（S3：避免设备时钟偏移决定谁赢）；存储层只做 upsert，中继保持笨桶。
/// 校验仅防滥用（S9）：key ≤64、value ≤16KB、单请求 ≤64 项；重复 key 以最后一笔为准。
/// </summary>
internal static class SettingsEndpoints
{
    private const int MaxKeyLength = 64;
    private const int MaxValueLength = 16384;
    private const int MaxItems = 64;

    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/me/settings", async (HttpContext context, RelayDatabase database, CancellationToken ct) =>
        {
            var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;
            await using var connection = database.OpenConnection();
            var rows = await ReadAllAsync(connection, account.Id, ct);
            return Results.Json(new SettingsSyncResponse(rows), SyncJsonContext.Default.Options);
        });

        app.MapPut("/api/me/settings", async (HttpContext context, RelayDatabase database, CancellationToken ct) =>
        {
            var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;

            SettingsSyncPutRequest? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync(
                    context.Request.Body, SyncJsonContext.Default.SettingsSyncPutRequest, ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Malformed settings payload." });
            }

            if (payload?.Settings is null || payload.Settings.Count == 0)
            {
                return Results.BadRequest(new { error = "settings is required and must not be empty." });
            }

            if (payload.Settings.Count > MaxItems)
            {
                return Results.BadRequest(new { error = $"at most {MaxItems} settings per request." });
            }

            // 重复 key 以最后一笔为准（同批内的顺序语义）；校验先于任何落库。
            var updates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var update in payload.Settings)
            {
                if (update?.Key is not { Length: > 0 } key || key.Length > MaxKeyLength)
                {
                    return Results.BadRequest(new { error = $"key is required and must be at most {MaxKeyLength} characters." });
                }

                if (update.Value is null || update.Value.Length > MaxValueLength)
                {
                    return Results.BadRequest(new { error = $"value is required and must be at most {MaxValueLength} characters." });
                }

                updates[key] = update.Value;
            }

            var now = DateTimeOffset.UtcNow;
            await using var connection = database.OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            foreach (var (key, value) in updates)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO account_settings (account_id, key, value, updated_at) VALUES ($accountId, $key, $value, $now)
                    ON CONFLICT(account_id, key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
                    """;
                cmd.Parameters.AddWithValue("$accountId", account.Id);
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$value", value);
                cmd.Parameters.AddWithValue("$now", now.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);

            // 只回显本次写入的行（服务端时间戳）；客户端据它推进本机账面。
            var written = updates
                .Select(pair => new SyncedSetting(pair.Key, pair.Value, now))
                .OrderBy(row => row.Key, StringComparer.Ordinal)
                .ToList();
            return Results.Json(new SettingsSyncResponse(written), SyncJsonContext.Default.Options);
        });
    }

    private static async Task<List<SyncedSetting>> ReadAllAsync(SqliteConnection connection, string accountId, CancellationToken ct)
    {
        var rows = new List<SyncedSetting>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT key, value, updated_at FROM account_settings WHERE account_id = $accountId ORDER BY key";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SyncedSetting(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return rows;
    }
}
