using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Relay;

/// <summary>
/// 排行榜端点（leaderboard 计划 Task 3 / 设计 §4.3）：公开榜单读（匿名 + leaderboard-public CORS，
/// AllowAnyOrigin 仅 GET）与偏好读写（既有 Bearer 中间件）。opt-out 语义（关闭即删）由端点层组合：
/// optIn=true → UpsertSettingsAsync + RebuildAllDaysAsync（全量重建拾回历史）；optIn=false → ClearAsync
/// （删 settings + 删全部 rollup）——UpsertSettingsAsync 只做存储原语，见 LeaderboardStore。
/// </summary>
internal static class LeaderboardEndpoints
{
    public const string PublicCorsPolicyName = "leaderboard-public";

    private const string CostBoard = "cost";
    private const string TokensBoard = "tokens";
    private const int DefaultLimit = 100;
    private const int MaxDisplayNameLength = 64;
    private const string AnonymousFallbackName = "anonymous";

    public static void MapLeaderboardEndpoints(this WebApplication app)
    {
        // 公开榜单读：参数校验先行（非法 400），查询交给 LeaderboardStore.QueryBoardAsync（L4 C# 求和排名）。
        app.MapGet("/api/leaderboard", async (RelayDatabase database, HttpRequest request, CancellationToken ct) =>
        {
            var board = request.Query["board"].FirstOrDefault() is { Length: > 0 } rawBoard ? rawBoard : CostBoard;
            if (board is not (CostBoard or TokensBoard))
            {
                return Results.BadRequest(new { error = "board must be cost or tokens." });
            }

            var month = request.Query["month"].FirstOrDefault() is { Length: > 0 } rawMonth
                ? rawMonth
                : DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedMonth))
            {
                return Results.BadRequest(new { error = "month must be a valid YYYY-MM." });
            }

            // QueryBoardAsync 的月窗口端点 = monthStart.AddMonths(1)：parsedMonth 为 DateOnly 可表示的
            // 最后一个月（9999-12）时下一个月越界，会在查询内抛 ArgumentOutOfRangeException 变 500——
            // 在校验边界拒收，守住匿名公开端点的 400 契约（设计 §4.3）。
            if (parsedMonth.Year == DateOnly.MaxValue.Year && parsedMonth.Month == DateOnly.MaxValue.Month)
            {
                return Results.BadRequest(new { error = "month must be within the representable range." });
            }

            var limit = DefaultLimit;
            if (request.Query.TryGetValue("limit", out var rawLimit))
            {
                if (!int.TryParse(rawLimit.FirstOrDefault(), out var parsedLimit))
                {
                    return Results.BadRequest(new { error = "limit must be an integer." });
                }

                limit = Math.Clamp(parsedLimit, 1, DefaultLimit);
            }

            await using var connection = database.OpenConnection();
            var response = await LeaderboardStore.QueryBoardAsync(connection, board, month, limit, ct);
            return Results.Json(response, SyncJsonContext.Default.Options);
        }).RequireCors(PublicCorsPolicyName);

        // 偏好读：displayName = 生效名（settings 自定义 ?? accounts 默认 ?? "anonymous"，L8 回落链）。
        app.MapGet("/api/me/leaderboard", async (HttpContext context, RelayDatabase database, CancellationToken ct) =>
        {
            var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;
            await using var connection = database.OpenConnection();
            var settings = await LeaderboardStore.ReadSettingsAsync(connection, account.Id, ct);
            return Results.Json(
                new LeaderboardPreferenceResponse(
                    settings?.OptIn ?? false,
                    EffectiveDisplayName(settings?.DisplayName, account.DisplayName)),
                SyncJsonContext.Default.Options);
        });

        // 偏好写（设计 §4.2 触发表）：事务内 optIn=true → upsert + 全量重建；optIn=false → 关闭即删。
        app.MapPut("/api/me/leaderboard", async (HttpContext context, RelayDatabase database, CancellationToken ct) =>
        {
            var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;

            LeaderboardPreferenceRequest? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync(
                    context.Request.Body, SyncJsonContext.Default.LeaderboardPreferenceRequest, ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Malformed leaderboard preference payload." });
            }

            if (payload is null)
            {
                return Results.BadRequest(new { error = "Leaderboard preference payload is required." });
            }

            // displayName trim 后 ≤64；空串/缺省 = NULL（用默认名）。
            var trimmed = payload.DisplayName?.Trim();
            if (trimmed is { Length: > MaxDisplayNameLength })
            {
                return Results.BadRequest(new { error = $"displayName must be at most {MaxDisplayNameLength} characters." });
            }

            var customName = string.IsNullOrEmpty(trimmed) ? null : trimmed;

            await using var connection = database.OpenConnection();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            if (payload.OptIn)
            {
                await LeaderboardStore.UpsertSettingsAsync(connection, transaction, account.Id, optIn: true, displayName: customName, ct);
                await LeaderboardStore.RebuildAllDaysAsync(connection, transaction, account.Id, ct);
            }
            else
            {
                await LeaderboardStore.ClearAsync(connection, transaction, account.Id, ct);
            }

            await transaction.CommitAsync(ct);
            return Results.Json(
                new LeaderboardPreferenceResponse(
                    payload.OptIn,
                    EffectiveDisplayName(payload.OptIn ? customName : null, account.DisplayName)),
                SyncJsonContext.Default.Options);
        });
    }

    /// <summary>L8 展示名回落链：settings.display_name → accounts.display_name → "anonymous"。</summary>
    private static string EffectiveDisplayName(string? custom, string? accountDefault) =>
        !string.IsNullOrEmpty(custom) ? custom
        : !string.IsNullOrEmpty(accountDefault) ? accountDefault
        : AnonymousFallbackName;
}
