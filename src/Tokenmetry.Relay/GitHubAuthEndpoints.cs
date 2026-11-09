using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Relay;

/// <summary>
/// 官方模式 GitHub OAuth 交换（relay 计划 Task 8 / 设计 U3、§7）：
/// code → GitHub access_token → /user → 按 github_sub upsert 账号 → 签发随机账号 token。
/// scope 只用 read:user（U3/§7 上线项）；两环境变量任一缺失 → 404——自建部署无配置即自然关闭（A15）。
/// </summary>
internal static class GitHubAuthEndpoints
{
    public const string HttpClientName = "tokenmetry-github";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed record ExchangeRequest(string Code);

    public static void MapGitHubAuthEndpoints(this WebApplication app)
    {
        // 匿名端点（认证前的身份交换）；/api/auth 前缀在认证中间件中豁免。
        app.MapPost("/api/auth/github", async (HttpContext context, RelayDatabase database, IHttpClientFactory httpClientFactory) =>
        {
            var clientId = Environment.GetEnvironmentVariable("TOKENMETRY_RELAY_GITHUB_CLIENT_ID");
            var clientSecret = Environment.GetEnvironmentVariable("TOKENMETRY_RELAY_GITHUB_CLIENT_SECRET");
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                return Results.NotFound();
            }

            var request = await JsonSerializer.DeserializeAsync<ExchangeRequest>(context.Request.Body, WebJson, context.RequestAborted);
            if (string.IsNullOrWhiteSpace(request?.Code))
            {
                return Results.BadRequest(new { error = "code is required." });
            }

            var http = httpClientFactory.CreateClient(HttpClientName);

            // 1) code → access_token（Accept: application/json 才返回 JSON 形状）
            // TODO(v1 上线项)：官方模式客户端流程落地时补 PKCE/state（中继只做机密交换半边，
            // 跳转与 code 防置换由客户端流程负责；自建模式无此端点，不受影响）。
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
            tokenRequest.Headers.Accept.ParseAdd("application/json");
            tokenRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = request.Code,
            });
            using var tokenResponse = await http.SendAsync(tokenRequest, context.RequestAborted);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                return Results.Unauthorized();
            }

            var tokenPayload = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            var accessToken = tokenPayload.TryGetProperty("access_token", out var accessTokenElement) ? accessTokenElement.GetString() : null;
            if (string.IsNullOrEmpty(accessToken))
            {
                return Results.Unauthorized();
            }

            // 2) /user 取身份（GitHub API 要求 User-Agent）
            using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            userRequest.Headers.UserAgent.ParseAdd("Tokenmetry-Relay/1.0");
            using var userResponse = await http.SendAsync(userRequest, context.RequestAborted);
            if (!userResponse.IsSuccessStatusCode)
            {
                return Results.Unauthorized();
            }

            var user = await userResponse.Content.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            // /user 形状防御：缺 id 或非数字一律 401，与上游失败路径同形——GitHub 返回异常形状不冒 500。
            if (!user.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var githubId))
            {
                return Results.Unauthorized();
            }

            var githubSub = githubId.ToString();
            var login = user.TryGetProperty("login", out var loginElement) ? loginElement.GetString() : null;
            var avatarUrl = user.TryGetProperty("avatar_url", out var avatarElement) ? avatarElement.GetString() : null;

            // 3) 按 github_sub upsert 账号 + 签发随机 token（明文只此一次过线，SHA-256 落库）
            var issuedToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await database.UpsertGitHubAccountAsync(githubSub, login, avatarUrl, RelayAuth.HashToken(issuedToken), context.RequestAborted);
            return Results.Json(new SyncTokenResponse(issuedToken, DeviceId: string.Empty), SyncJsonContext.Default.Options);
        });
    }
}
