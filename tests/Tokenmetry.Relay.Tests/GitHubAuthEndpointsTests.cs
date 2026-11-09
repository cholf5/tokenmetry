using Microsoft.Extensions.DependencyInjection;

namespace Tokenmetry.Relay.Tests;

using System.Text;
using Tokenmetry.Core.Sync;

/// <summary>
/// 官方模式 GitHub OAuth 交换（relay 计划 Task 8 / 设计 U3、A15）：
/// code → access_token → /user → 按 github_sub upsert 账号 + 签发随机 token（SHA-256 落库）；
/// 无 CLIENT_ID/SECRET 配置 → 404（自建部署自然关闭）；上游失败/异常形状 → 401 不冒 500。
/// </summary>
public sealed class GitHubAuthEndpointsTests
{
    private const string ExchangeBody = """{"code":"oauth-code-1"}""";

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>GitHub OAuth 应答按 URL 分发；默认 HappyPath。</summary>
    private sealed class FakeGithubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeGithubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public static Func<HttpRequestMessage, HttpResponseMessage> HappyPath() => request =>
            request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? JsonResponse("""{"id":12345,"login":"octocat","avatar_url":"https://avatars.githubusercontent.com/u/1?v=4"}""")
                : JsonResponse("""{"access_token":"gho_test_token","token_type":"bearer","scope":"read:user"}""");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    private static HttpClient CreateExchangeClient(RelayFactory factory, Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var wired = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient(GitHubAuthEndpoints.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new FakeGithubHandler(respond ?? FakeGithubHandler.HappyPath()))));
        return wired.CreateClient();
    }

    [Fact]
    public async Task GitHubExchange_CreatesAccountAndReturnsToken()
    {
        await using var factory = new RelayFactory(
            token: "abc", githubClientId: "cid", githubClientSecret: "secret");
        using var client = CreateExchangeClient(factory);

        var first = await client.PostAsync("/api/auth/github", new StringContent(ExchangeBody, Encoding.UTF8, "application/json"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstToken = JsonSerializer.Deserialize(await first.Content.ReadAsStreamAsync(), SyncJsonContext.Default.SyncTokenResponse)!;
        firstToken.Token.Should().NotBeNullOrEmpty();
        firstToken.DeviceId.Should().BeEmpty("设备注册由客户端凭本机 id 另行走 POST /api/devices");

        // 新 token 可过 Bearer 认证，账号来自 GitHub 身份
        using var authorized = RelayTestSync.Client(factory, firstToken.Token);
        var me = await authorized.GetAsync("/api/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var account = await me.Content.ReadFromJsonAsync<JsonElement>();
        account.GetProperty("kind").GetString().Should().Be("github");
        account.GetProperty("displayName").GetString().Should().Be("octocat");

        // 重复换 code：同账号不同 token（官方多设备各自登录各自持 token）
        var second = await client.PostAsync("/api/auth/github", new StringContent(ExchangeBody, Encoding.UTF8, "application/json"));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondToken = JsonSerializer.Deserialize(await second.Content.ReadAsStreamAsync(), SyncJsonContext.Default.SyncTokenResponse)!;
        secondToken.Token.Should().NotBe(firstToken.Token);

        using var secondClient = RelayTestSync.Client(factory, secondToken.Token);
        (await secondClient.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var firstAgain = RelayTestSync.Client(factory, firstToken.Token);
        (await firstAgain.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK, "多设备并存：先签发的 token 不因再次登录失效");
    }

    [Fact]
    public async Task GitHubExchange_DisabledWithoutEnv_Returns404()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);

        var response = await client.PostAsync("/api/auth/github", new StringContent(ExchangeBody, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "自建部署无配置即关闭此端点（A15）");
    }

    // ---- 失败路径：上游拒绝/异常形状一律 401（与凭据失败同形），不冒 500 ----

    [Fact]
    public async Task GitHubExchange_GithubRejectsCode_Returns401()
    {
        await using var factory = new RelayFactory(token: "abc", githubClientId: "cid", githubClientSecret: "secret");
        using var client = CreateExchangeClient(factory, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var response = await client.PostAsync("/api/auth/github", new StringContent(ExchangeBody, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "上游拒绝与凭据失败同形");
    }

    [Fact]
    public async Task GitHubExchange_TokenPayloadWithoutAccessToken_Returns401()
    {
        // GitHub 对无效 code 返回 200 + error 形状（{"error":"bad_verification_code"}）——缺 access_token 即拒。
        await using var factory = new RelayFactory(token: "abc", githubClientId: "cid", githubClientSecret: "secret");
        using var client = CreateExchangeClient(factory, _ =>
            JsonResponse("""{"error":"bad_verification_code","error_description":"Bad verification code."}"""));

        var response = await client.PostAsync("/api/auth/github", new StringContent(ExchangeBody, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GitHubExchange_UserEndpointFails_Returns401()
    {
        await using var factory = new RelayFactory(token: "abc", githubClientId: "cid", githubClientSecret: "secret");
        using var client = CreateExchangeClient(factory, request =>
            request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : JsonResponse("""{"access_token":"gho_test_token","token_type":"bearer","scope":"read:user"}"""));

        var response = await client.PostAsync("/api/auth/github", new StringContent(ExchangeBody, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GitHubExchange_UserPayloadMissingId_Returns401()
    {
        // /user 形状防御：缺 id（或非数字）走 TryGetProperty 401，不 GetProperty 抛 500。
        await using var factory = new RelayFactory(token: "abc", githubClientId: "cid", githubClientSecret: "secret");
        using var client = CreateExchangeClient(factory, request =>
            request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? JsonResponse("""{"login":"octocat","avatar_url":"https://avatars.githubusercontent.com/u/1?v=4"}""")
                : JsonResponse("""{"access_token":"gho_test_token","token_type":"bearer","scope":"read:user"}"""));

        var response = await client.PostAsync("/api/auth/github", new StringContent(ExchangeBody, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "异常身份形状不得冒 500");
    }
}
