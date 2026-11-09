namespace Tokenmetry.Infrastructure.Tests.Sync;

using System.Net;
using System.Text.Json;
using Tokenmetry.Core.Sync;
using Tokenmetry.Infrastructure.Sync;

/// <summary>
/// RelaySyncBackend 排行榜偏好读写（leaderboard 计划 Task 4）：stub handler 断言
/// GET/PUT 的 method/path/payload 与响应解码；Bearer 认证沿用构造器统一装载（不逐端点重复）。
/// </summary>
public sealed class RelaySyncBackendLeaderboardTests
{
    private const string BaseUrl = "http://relay.example:8080";

    private const string Endpoint = BaseUrl + "/api/me/leaderboard";

    private static string ResponseBody(bool optIn, string displayName) =>
        JsonSerializer.Serialize(
            new LeaderboardPreferenceResponse(optIn, displayName),
            SyncJsonContext.Default.LeaderboardPreferenceResponse);

    private static RelaySyncBackend CreateBackend(StubHandler handler) =>
        new(new HttpClient(handler), BaseUrl, "tok", "device-1");

    [Fact]
    public async Task GetLeaderboardPreferenceAsync_RequestsEndpointAndDecodesResponse()
    {
        var handler = new StubHandler(ResponseBody(optIn: true, displayName: "alice"));
        var backend = CreateBackend(handler);

        var response = await backend.GetLeaderboardPreferenceAsync();

        response.OptIn.Should().BeTrue();
        response.DisplayName.Should().Be("alice");
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastUrl.Should().Be(Endpoint);
        handler.LastAuthorization.Should().Be("Bearer tok");
    }

    [Fact]
    public async Task SetLeaderboardPreferenceAsync_PutsCamelCasePayloadAndDecodesResponse()
    {
        var handler = new StubHandler(ResponseBody(optIn: true, displayName: "Alice 的工厂"));
        var backend = CreateBackend(handler);

        var response = await backend.SetLeaderboardPreferenceAsync(new LeaderboardPreferenceRequest(true, "Alice 的工厂"));

        response.OptIn.Should().BeTrue();
        response.DisplayName.Should().Be("Alice 的工厂");
        handler.LastMethod.Should().Be(HttpMethod.Put);
        handler.LastUrl.Should().Be(Endpoint);
        using var payload = JsonDocument.Parse(handler.LastBody!);
        payload.RootElement.GetProperty("optIn").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("displayName").GetString().Should().Be("Alice 的工厂");
    }

    [Fact]
    public async Task GetLeaderboardPreferenceAsync_NonSuccessStatusThrows()
    {
        var handler = new StubHandler("{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized);
        var backend = CreateBackend(handler);

        var act = async () => await backend.GetLeaderboardPreferenceAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task SetLeaderboardPreferenceAsync_NonSuccessStatusThrows()
    {
        var handler = new StubHandler("{\"error\":\"bad payload\"}", HttpStatusCode.BadRequest);
        var backend = CreateBackend(handler);

        var act = async () => await backend.SetLeaderboardPreferenceAsync(new LeaderboardPreferenceRequest(true, "alice"));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>记录 method/url/Authorization/body 的最小 stub handler。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responder = _ => new HttpResponseMessage(status) { Content = new StringContent(body) };
        }

        public HttpMethod? LastMethod { get; private set; }

        public string? LastUrl { get; private set; }

        public string? LastBody { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUrl = request.RequestUri?.ToString();
            LastAuthorization = $"{request.Headers.Authorization?.Scheme} {request.Headers.Authorization?.Parameter}";
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }
}
