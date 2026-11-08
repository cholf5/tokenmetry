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
        await using var factory = new RelayFactory();
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
}
