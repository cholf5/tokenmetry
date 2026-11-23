namespace Tokenmetry.Relay.Tests;

/// <summary>
/// 中继网页脚本资源（RelayAssetEndpoints）：CSP 收紧到 script-src 'self' 后，
/// 两页的全部脚本走这里——匿名可读、同源 JS 内容类型、ETag 条件请求、未知文件 404；
/// 带尾斜杠的页面 URL 相对解析会落到 /{page}/assets/，三个挂载点都要通。
/// </summary>
public sealed class RelayAssetTests
{
    [Theory]
    [InlineData("relay-common.js", "window.TokenmetryRelay")]
    [InlineData("admin.js", "api/admin/members")]
    [InlineData("leaderboard.js", "api/leaderboard")]
    public async Task Assets_AreServedAnonymously_AsJavaScript(string file, string marker)
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/assets/{file}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle()
            .Which.Should().Be("nosniff");

        var script = await response.Content.ReadAsStringAsync();
        script.Should().Contain(marker);
    }

    [Theory]
    [InlineData("/admin/assets/admin.js")]
    [InlineData("/admin/assets/relay-common.js")]
    [InlineData("/leaderboard/assets/leaderboard.js")]
    [InlineData("/leaderboard/assets/relay-common.js")]
    public async Task Assets_AreServedFromTrailingSlashPagePaths(string path)
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = factory.CreateClient();

        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Assets_Return304_WhenETagMatches()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = factory.CreateClient();

        var first = await client.GetAsync("/assets/admin.js");
        var etag = first.Headers.ETag!;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/admin.js");
        request.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(request);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await second.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Assets_UnknownFile_Is404()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = factory.CreateClient();

        (await client.GetAsync("/assets/does-not-exist.js")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }
}
