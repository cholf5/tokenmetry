using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Relay.Tests;

/// <summary>
/// Relay 骨架（relay 计划 Task 5）：/health 匿名、/api/* Bearer 认证、
/// TOKENMETRY_RELAY_TOKEN 单租户自举（A15）。
/// </summary>
public sealed class RelaySkeletonTests
{
    [Fact]
    public async Task Health_WithoutToken_Returns401_OnApiRoutes()
    {
        // 显式 token: null = 清空进程级 env（无参构造是 WithWebHostBuilder 派生实例专用，不动 env——
        // 先前测试留下的 TOKENMETRY_RELAY_TOKEN 不得泄漏进本用例的启动语义）。
        await using var factory = new RelayFactory(token: null);
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK);

        var api = await client.GetAsync("/api/me");
        api.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SelfHostBootstrap_EnvToken_Authenticates()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = factory.CreateClient();

        using var good = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        good.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "abc");
        var ok = await client.SendAsync(good);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("kind").GetString().Should().Be("shared");

        using var bad = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        var denied = await client.SendAsync(bad);
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static readonly DateTimeOffset T1 = new(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SelfHostTokenRotation_RevokesPreviousAccount()
    {
        // 终审 I4：换 token = 重签，旧账号必须被退役（含其行）——泄露轮换的语义才成立。
        // 同一库文件两次启动（bootstrap 各跑一次）模拟"改环境变量重启容器"。
        var dbPath = Path.Combine(Path.GetTempPath(), $"tokenmetry-relay-rotation-{Guid.NewGuid():N}.db");
        try
        {
            await using (var first = new RelayFactory(token: "old-token", dbPath: dbPath))
            {
                using var oldClient = RelayTestSync.Client(first, "old-token");
                await RelayTestSync.RegisterAsync(oldClient, "devA", "Desktop A");
                (await RelayTestSync.PushAsync(oldClient, "devA", new SyncPushRequest(
                    new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1) },
                    new List<SessionFact>()))).EnsureSuccessStatusCode();
            }

            await using (var second = new RelayFactory(token: "new-token", dbPath: dbPath))
            {
                using var newClient = RelayTestSync.Client(second, "new-token");
                (await newClient.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK, "新 token 即账号");

                using var oldClient = RelayTestSync.Client(second, "old-token");
                (await oldClient.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "旧 token 退役");

                var pulled = await RelayTestSync.PullAsync(newClient, "devX", since: 0);
                pulled.UsageFacts.Should().BeEmpty("轮换退役旧账号及其数据，新账号是干净的桶");
            }
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }
}
