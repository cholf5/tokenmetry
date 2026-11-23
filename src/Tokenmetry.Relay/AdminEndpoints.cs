using System.Text.Json;

namespace Tokenmetry.Relay;

/// <summary>
/// 自建中继的运营者管理 API（docs/selfhost.md「Web 管理页面」）：
/// 成员列表 / 签发 / 吊销。认证复用既有 Bearer 中间件，但额外要求账号是运营者
/// <c>kind='shared'</c>——成员 token 与 GitHub 账号一律 403。
///
/// 不做 Cookie / CORS / 服务端会话：页面用 Authorization 头调用，跨站表单无法驱动；
/// token 明文只在签发的响应里出现一次，库里仍只存 SHA-256。
/// </summary>
internal static class AdminEndpoints
{
    private const string OperatorKind = "shared";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record AddMemberRequest(string? Name);

    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/members", async (HttpContext context, RelayDatabase database, CancellationToken ct) =>
        {
            if (RequireOperator(context) is { } denied)
            {
                return denied;
            }

            var members = await RelayTokenCommands.ListAsync(database, ct);
            return Results.Ok(new
            {
                members = members.Select(member => new
                {
                    accountId = member.AccountId,
                    displayName = member.DisplayName,
                    createdAt = member.CreatedAt,
                    deviceCount = member.DeviceCount,
                    lastSeenAt = member.LastSeenAt,
                }),
            });
        });

        app.MapPost("/api/admin/members", async (HttpContext context, RelayDatabase database, CancellationToken ct) =>
        {
            if (RequireOperator(context) is { } denied)
            {
                return denied;
            }

            AddMemberRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<AddMemberRequest>(
                    context.Request.Body, WebJson, ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Malformed request body." });
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "name is required." });
            }

            try
            {
                var added = await RelayTokenCommands.AddAsync(database, request.Name, ct);
                return Results.Ok(new
                {
                    accountId = added.AccountId,
                    displayName = added.DisplayName,
                    token = added.Token,
                });
            }
            catch (InvalidOperationException ex)
            {
                // 重名 / 空名 / 超长 / 控制字符：都是调用方可修正的输入错误。
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/admin/members/{accountId}", async (
            HttpContext context,
            RelayDatabase database,
            string accountId,
            CancellationToken ct) =>
        {
            if (RequireOperator(context) is { } denied)
            {
                return denied;
            }

            try
            {
                await RelayTokenCommands.RevokeAsync(database, accountId, displayName: null, ct);
                return Results.NoContent();
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }

    private static IResult? RequireOperator(HttpContext context)
    {
        var account = (RelayAccount)context.Items[RelayAuth.AccountItemKey]!;
        return account.Kind == OperatorKind
            ? null
            : Results.Json(
                new { error = "Admin access requires the operator token (kind=shared)." },
                statusCode: StatusCodes.Status403Forbidden);
    }
}
