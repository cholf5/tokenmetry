namespace Tokenmetry.Relay.Tests;

using System.Text;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

/// <summary>Relay 集成测试共用 HTTP 助手：带 token 的 client、push/pull 收发。</summary>
internal static class RelayTestSync
{
    public static HttpClient Client(RelayFactory factory, string token = "abc")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static UsageFact UsageRow(string device, string day, long tokens, decimal cost, DateTimeOffset at) => new()
    {
        Device = device,
        Day = day,
        Grain = UsageFactGrain.Day,
        TotalTokens = tokens,
        CostUsd = cost,
        UpdatedAt = at,
    };

    /// <summary>注册设备（正式客户端流程，EnableAsync → RegisterDeviceAsync 的测试同形）；
    /// push 只收已注册设备的行，所有先 push 的用例必须先走这里。</summary>
    public static async Task RegisterAsync(HttpClient client, string deviceId, string name)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/devices");
        message.Headers.Add("X-Tokenmetry-Device", deviceId);
        message.Content = new StringContent(
            JsonSerializer.Serialize(new { deviceId, name }),
            Encoding.UTF8,
            "application/json");
        (await client.SendAsync(message)).EnsureSuccessStatusCode();
    }

    public static async Task<HttpResponseMessage> PushAsync(HttpClient client, string device, SyncPushRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/sync/push");
        message.Headers.Add("X-Tokenmetry-Device", device);
        message.Content = new StringContent(
            JsonSerializer.Serialize(request, SyncJsonContext.Default.SyncPushRequest),
            Encoding.UTF8,
            "application/json");
        return await client.SendAsync(message);
    }

    public static async Task<SyncPullResponse> PullAsync(HttpClient client, string device, long since, int? limit = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"/api/sync/pull?since={since}" + (limit is { } l ? $"&limit={l}" : string.Empty));
        message.Headers.Add("X-Tokenmetry-Device", device);
        var response = await client.SendAsync(message);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize(await response.Content.ReadAsStreamAsync(), SyncJsonContext.Default.SyncPullResponse)!;
    }

    public static Task<HttpResponseMessage> SendGetAsync(HttpClient client, string uri) => client.GetAsync(uri);
}
