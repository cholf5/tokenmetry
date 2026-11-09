using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Relay;

/// <summary>
/// 设备与账号管理端点（relay 计划 Task 7 / 设计 A11、A16）：
/// 注册（POST，含改名覆盖）、列表（GET）、改名（PATCH）、解绑（DELETE，级联清该设备全部行 + opt-in 全量重建 rollup）、
/// 删号（DELETE /api/me，级联清账号全部行/设备/账号行/榜单行——隐私立场的服务端兑现）。
/// </summary>
internal static class DeviceEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record DeviceRegistrationRequest(string DeviceId, string Name);

    private sealed record DeviceRenameRequest(string Name);

    public static void MapDeviceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/devices", async (HttpContext context, RelayDatabase database) =>
        {
            var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;
            await using var connection = database.OpenConnection();
            var devices = await SyncEndpoints.ReadDevicesAsync(connection, account.Id, context.RequestAborted);
            return Results.Json(new SyncDeviceListResponse(devices), SyncJsonContext.Default.Options);
        });

        app.MapPost("/api/devices", async (HttpContext context, RelayDatabase database) =>
        {
            var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;
            var request = await JsonSerializer.DeserializeAsync<DeviceRegistrationRequest>(
                context.Request.Body, WebJson, context.RequestAborted);
            if (request is null || string.IsNullOrEmpty(request.DeviceId) || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "deviceId and name are required." });
            }

            await using var connection = database.OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(context.RequestAborted);
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO devices (account_id, device_id, name, last_seen_at) VALUES ($accountId, $deviceId, $name, $now)
                    ON CONFLICT(account_id, device_id) DO UPDATE SET name = excluded.name
                    """;
                cmd.Parameters.AddWithValue("$accountId", account.Id);
                cmd.Parameters.AddWithValue("$deviceId", request.DeviceId);
                cmd.Parameters.AddWithValue("$name", request.Name.Trim());
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync(context.RequestAborted);
            }

            await transaction.CommitAsync(context.RequestAborted);
            return Results.Ok(new { deviceId = request.DeviceId, name = request.Name.Trim() });
        });

        app.MapPatch("/api/devices/{deviceId}", async (HttpContext context, RelayDatabase database, string deviceId) =>
        {
            var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;
            var request = await JsonSerializer.DeserializeAsync<DeviceRenameRequest>(
                context.Request.Body, WebJson, context.RequestAborted);
            if (request is null || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "name is required." });
            }

            await using var connection = database.OpenConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE devices SET name = $name WHERE account_id = $accountId AND device_id = $deviceId";
            cmd.Parameters.AddWithValue("$name", request.Name.Trim());
            cmd.Parameters.AddWithValue("$accountId", account.Id);
            cmd.Parameters.AddWithValue("$deviceId", deviceId);
            var affected = await cmd.ExecuteNonQueryAsync(context.RequestAborted);
            return affected > 0 ? Results.Ok(new { deviceId, name = request.Name.Trim() }) : Results.NotFound();
        });

        // 解绑（A16）：删 devices 行 + 该设备全部行；其余设备经 pull 的设备清单对账（A13）自然清除本地副本。
        app.MapDelete("/api/devices/{deviceId}", async (HttpContext context, RelayDatabase database, string deviceId) =>
        {
            var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;
            await using var connection = database.OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(context.RequestAborted);
            foreach (var sql in new[]
                     {
                         "DELETE FROM devices WHERE account_id = $accountId AND device_id = $deviceId",
                         "DELETE FROM usage_rows WHERE account_id = $accountId AND device_id = $deviceId",
                         "DELETE FROM session_rows WHERE account_id = $accountId AND device_id = $deviceId",
                     })
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$accountId", account.Id);
                cmd.Parameters.AddWithValue("$deviceId", deviceId);
                await cmd.ExecuteNonQueryAsync(context.RequestAborted);
            }

            // 排行榜触发点（leaderboard 设计 §4.2）：该设备 usage 行已删，opt-in 则全量重建 rollup——
            // 只由已消失设备构成的历史 day 不重算就虚高。
            if (await LeaderboardStore.IsOptedInAsync(connection, transaction, account.Id, context.RequestAborted))
            {
                await LeaderboardStore.RebuildAllDaysAsync(connection, transaction, account.Id, context.RequestAborted);
            }

            await transaction.CommitAsync(context.RequestAborted);
            return Results.NoContent();
        });

        // 删号（A11）：账号全部行/设备/账号行级联清（含排行榜 settings + rollup，设计 §4.2）；旧 token 随账号行消失 → 401。
        app.MapDelete("/api/me", async (HttpContext context, RelayDatabase database) =>
        {
            var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;
            await using var connection = database.OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(context.RequestAborted);
            foreach (var sql in new[]
                     {
                         "DELETE FROM usage_rows WHERE account_id = $accountId",
                         "DELETE FROM session_rows WHERE account_id = $accountId",
                         "DELETE FROM leaderboard_settings WHERE account_id = $accountId",
                         "DELETE FROM leaderboard_rollup WHERE account_id = $accountId",
                         "DELETE FROM devices WHERE account_id = $accountId",
                         "DELETE FROM account_tokens WHERE account_id = $accountId",
                         "DELETE FROM accounts WHERE id = $accountId",
                     })
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$accountId", account.Id);
                await cmd.ExecuteNonQueryAsync(context.RequestAborted);
            }

            await transaction.CommitAsync(context.RequestAborted);
            return Results.NoContent();
        });
    }
}
