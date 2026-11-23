namespace Tokenmetry.Relay.Tests;

using System.Text;

/// <summary>
/// 自建中继的运营者管理 API/页面（docs/selfhost.md「Web 管理页面」）：
/// 成员 token 403、匿名 401、运营者 token 可列表/签发/吊销；页面本身是匿名空壳。
/// </summary>
public sealed class AdminEndpointsTests
{
    [Fact]
    public async Task AdminMembers_RequiresOperatorToken()
    {
        var dbPath = NewDbPath();
        await using var factory = new RelayFactory(token: "operator-token", dbPath: dbPath);
        try
        {
            var database = new RelayDatabase(dbPath);
            await database.EnsureCreatedAsync();
            var member = await RelayTokenCommands.AddAsync(database, "Alice");

            using var anonymous = factory.CreateClient();
            (await anonymous.GetAsync("/api/admin/members")).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);

            using var memberClient = RelayTestSync.Client(factory, member.Token);
            (await memberClient.GetAsync("/api/admin/members")).StatusCode
                .Should().Be(HttpStatusCode.Forbidden, "成员 token 不能进入管理 API");

            using var operatorClient = RelayTestSync.Client(factory, "operator-token");
            var response = await operatorClient.GetAsync("/api/admin/members");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("Alice");
            body.Should().Contain(member.AccountId);
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    [Fact]
    public async Task AdminMembers_AddListRevoke_RoundTrips()
    {
        var dbPath = NewDbPath();
        await using var factory = new RelayFactory(token: "operator-token", dbPath: dbPath);
        try
        {
            using var client = RelayTestSync.Client(factory, "operator-token");

            var add = await client.PostAsync("/api/admin/members", JsonContent("""{"name":"Alice"}"""));
            add.StatusCode.Should().Be(HttpStatusCode.OK);
            var added = await add.Content.ReadFromJsonAsync<JsonElement>();
            var accountId = added.GetProperty("accountId").GetString()!;
            var token = added.GetProperty("token").GetString()!;
            token.Should().HaveLength(64, "签发响应必须带一次性明文 token");

            var list = await client.GetAsync("/api/admin/members");
            var listJson = await list.Content.ReadFromJsonAsync<JsonElement>();
            listJson.GetProperty("members").GetArrayLength().Should().Be(1);
            listJson.GetProperty("members")[0].GetProperty("displayName").GetString().Should().Be("Alice");

            using (var memberClient = RelayTestSync.Client(factory, token))
            {
                (await memberClient.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK);
                (await memberClient.GetAsync("/api/admin/members")).StatusCode
                    .Should().Be(HttpStatusCode.Forbidden);
            }

            var revoke = await client.DeleteAsync($"/api/admin/members/{accountId}");
            revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);

            using (var revokedClient = RelayTestSync.Client(factory, token))
            {
                (await revokedClient.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }

            var after = await (await client.GetAsync("/api/admin/members")).Content.ReadFromJsonAsync<JsonElement>();
            after.GetProperty("members").GetArrayLength().Should().Be(0);
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    [Fact]
    public async Task AdminMembers_RejectDuplicateAndInvalidNames()
    {
        var dbPath = NewDbPath();
        await using var factory = new RelayFactory(token: "operator-token", dbPath: dbPath);
        try
        {
            using var client = RelayTestSync.Client(factory, "operator-token");

            (await client.PostAsync("/api/admin/members", JsonContent("""{"name":"Alice"}""")))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            // 重名（不区分大小写）与空名都是可修正的输入错误。
            (await client.PostAsync("/api/admin/members", JsonContent("""{"name":"alice"}""")))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await client.PostAsync("/api/admin/members", JsonContent("""{"name":"   "}""")))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await client.DeleteAsync("/api/admin/members/does-not-exist"))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    [Fact]
    public async Task AdminPage_IsServedAnonymously_WithSecurityHeaders()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var csp = response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Subject;
        csp.Should().Contain("connect-src 'self'");
        csp.Should().Contain("script-src 'self'");
        csp.Should().NotContain("script-src 'unsafe-inline'", "页面脚本一律同源外部资源");
        response.Headers.GetValues("X-Robots-Tag").Should().ContainSingle()
            .Which.Should().Contain("noindex");

        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("src=\"assets/relay-common.js\"");
        html.Should().Contain("src=\"assets/admin.js\"");
        html.Should().NotContain("<script>", "CSP 不允许内联脚本——页面里不能有裸 <script> 块");
        html.Should().Contain("TOKENMETRY_RELAY_TOKEN");
    }

    [Fact]
    public async Task AdminPage_TrailingSlash_IsServed()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = factory.CreateClient();

        (await client.GetAsync("/admin/")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"tokenmetry-admin-{Guid.NewGuid():N}.db");

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");
}
