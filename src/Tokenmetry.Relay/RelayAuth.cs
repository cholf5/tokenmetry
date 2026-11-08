using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Tokenmetry.Relay;

/// <summary>认证后的账号视图（/api/me 与中间件传递用）。</summary>
internal sealed record RelayAccount(string Id, string Kind, string? GithubSub, string? DisplayName, string? AvatarUrl);

/// <summary>
/// Bearer 认证（relay 设计 §4.4）：token 明文不落库，只存 SHA-256 hex；
/// MapAccountAsync 按凭据哈希反查账号。官方多租户与自建单租户共用同一套 accounts schema（A15）。
/// </summary>
internal static class RelayAuth
{
    public const string AccountItemKey = "relay.account";

    public static string? ExtractBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header["Bearer ".Length..].Trim();
        return token.Length > 0 ? token : null;
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>token → 账号；无匹配凭据返回 null（调用方落 401）。</summary>
    public static async Task<RelayAccount?> MapAccountAsync(SqliteConnection connection, string token)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, github_sub, display_name, avatar_url
            FROM accounts
            WHERE credential_hash = $hash
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$hash", HashToken(token));
        var result = await cmd.ExecuteScalarAsync();
        if (result is null)
        {
            return null;
        }

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new RelayAccount(
            Id: reader.GetString(0),
            Kind: reader.GetString(1),
            GithubSub: reader.IsDBNull(2) ? null : reader.GetString(2),
            DisplayName: reader.IsDBNull(3) ? null : reader.GetString(3),
            AvatarUrl: reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public static async Task UnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsync("");
    }
}
