namespace Tokenmetry.Infrastructure.Tests.Sync;

using System.Net;
using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;
using Tokenmetry.Infrastructure.Sync;

/// <summary>
/// RelaySyncBackend push 分帧回归：请求体必须显式 Content-Length（ByteArrayContent 缓冲），
/// 不得走 JsonContent 的 Transfer-Encoding: chunked——系统代理（V2Ray 家族本地 inbound）对
/// 超 ~8KB 的 chunked 请求体直接断连，真机 push 全量批次即死于该路径（同字节 Content-Length
/// 载荷通行）。载荷新形状（camelCase、X-Tokenmetry-Device、updated_at 在列）一并固化。
/// </summary>
public sealed class RelaySyncBackendPushTests
{
    private const string BaseUrl = "http://relay.example:8080";

    private static SyncPushRequest SamplePayload() => new(
        [new UsageFact
        {
            Device = "device-1",
            Day = "2026-11-22",
            Grain = UsageFactGrain.Full,
            Agent = "ZCode",
            Model = "glm-5.3-flash",
            Provider = "account:test",
            Project = "/tmp/project",
            ProjectDisplayName = "project",
            InputTokens = 10,
            OutputTokens = 20,
            TotalTokens = 30,
            CostUsd = 0.5m,
            UpdatedAt = new DateTimeOffset(2026, 11, 22, 1, 2, 3, TimeSpan.Zero),
        }],
        []);

    private static RelaySyncBackend CreateBackend(RecordingHandler handler) =>
        new(new HttpClient(handler), BaseUrl, "tok", "device-1");

    [Fact]
    public async Task PushAsync_SendsBufferedContentLength_NotChunked()
    {
        var handler = new RecordingHandler();
        var backend = CreateBackend(handler);

        await backend.PushAsync(SamplePayload());

        handler.LastTransferEncodingChunked.Should().BeFalse();
        handler.LastContentLength.Should().NotBeNull();
        handler.LastContentLength.Should().BePositive();
    }

    [Fact]
    public async Task PushAsync_PostsCamelCasePayloadWithCallerDeviceHeader()
    {
        var handler = new RecordingHandler();
        var backend = CreateBackend(handler);

        await backend.PushAsync(SamplePayload());

        handler.LastMethod.Should().Be(HttpMethod.Post);
        handler.LastUrl.Should().Be(BaseUrl + "/api/sync/push");
        handler.LastDeviceHeader.Should().Be("device-1");
        using var payload = JsonDocument.Parse(handler.LastBody!);
        payload.RootElement.GetProperty("usageFacts").GetArrayLength().Should().Be(1);
        var usage = payload.RootElement.GetProperty("usageFacts")[0];
        usage.GetProperty("device").GetString().Should().Be("device-1");
        usage.GetProperty("day").GetString().Should().Be("2026-11-22");
        usage.GetProperty("updatedAt").GetString().Should().Be("2026-11-22T01:02:03+00:00");
    }

    /// <summary>记录请求分帧特征与 body 的最小 stub handler。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUrl = request.RequestUri?.ToString();
            LastDeviceHeader = request.Headers.TryGetValues("X-Tokenmetry-Device", out var device)
                ? device.FirstOrDefault()
                : null;
            LastTransferEncodingChunked = request.Headers.TransferEncodingChunked == true;
            LastContentLength = request.Content?.Headers.ContentLength;
            LastBody = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) });
        }

        public HttpMethod? LastMethod { get; private set; }

        public string? LastUrl { get; private set; }

        public string? LastDeviceHeader { get; private set; }

        public bool LastTransferEncodingChunked { get; private set; }

        public long? LastContentLength { get; private set; }

        public string? LastBody { get; private set; }
    }
}
