namespace Tokenmetry.Relay.Tests;

/// <summary>
/// 自建排行榜页面（LeaderboardPageEndpoints）：匿名可读、同源只读公开 API、带安全头；
/// 页面本身不进入 /api Bearer 中间件。
/// </summary>
public sealed class LeaderboardPageTests
{
    [Fact]
    public async Task Page_IsServedAnonymously_WithSecurityHeaders()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/leaderboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var csp = response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Subject;
        csp.Should().Contain("connect-src 'self'");
        csp.Should().Contain("script-src 'self'");
        csp.Should().NotContain("script-src 'unsafe-inline'", "页面脚本一律同源外部资源");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle()
            .Which.Should().Be("nosniff");

        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Tokenmetry");
        html.Should().Contain("src=\"assets/relay-common.js\"");
        html.Should().Contain("src=\"assets/leaderboard.js\"");
        html.Should().NotContain("<script>", "CSP 不允许内联脚本——页面里不能有裸 <script> 块");
    }

    [Fact]
    public async Task Page_TrailingSlash_IsServed()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/leaderboard/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PublicLeaderboardApi_RemainsAnonymous()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/leaderboard?board=cost&month=2026-11");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
