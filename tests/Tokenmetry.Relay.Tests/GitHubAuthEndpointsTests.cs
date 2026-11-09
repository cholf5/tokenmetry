using Microsoft.Extensions.DependencyInjection;

namespace Tokenmetry.Relay.Tests;

using System.Text;
using Tokenmetry.Core.Sync;

/// <summary>
/// 官方模式 GitHub OAuth 交换（relay 计划 Task 8 / 设计 U3、A15）：
/// code → access_token → /user → 按 github_sub upsert 账号 + 签发随机 token（SHA-256 落库）；
/// 无 CLIENT_ID/SECRET 配置 → 404（自建部署自然关闭）。
/// </summary>
public sealed class GitHubAuthEndpointsTests
{
    private const string ExchangeBody = """{"code":"oauth-code-1"}""";

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>GitHub OAuth 固定应答：换 token + /user。</summary>
    private sealed class FakeGithubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? JsonResponse("""{"id":12345,"login":"octocat","avatar_url":"https://avatars.githubusercontent.com/u/1?v=4"}""")
                : JsonResponse("""{"access_token":"gho_test_token","token_type":"bearer","scope":"read:user"}""");
            return Task.FromResult(response);
        }
    }

    private static HttpClient CreateExchangeClient(RelayFactory factory)
    {
        var wired = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHttpClient(GitHubAuthEndpoints.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new FakeGithubHandler())));
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
}
