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
