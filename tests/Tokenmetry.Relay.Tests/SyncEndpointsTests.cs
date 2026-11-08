namespace Tokenmetry.Relay.Tests;

using System.Text;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

/// <summary>
/// 中继 push/pull（relay 计划 Task 6）：设备间行同步、服务端 LWW、账号级单调 seq、
/// pull 排除 caller 自身行、响应携带全设备清单。
/// </summary>
public sealed class SyncEndpointsTests
{
    private static readonly DateTimeOffset T1 = new(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 6, 19, 11, 0, 0, TimeSpan.Zero);

    private static HttpClient Client(RelayFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "abc");
        return client;
    }

    private static UsageFact UsageRow(string device, string day, long tokens, decimal cost, DateTimeOffset at) => new()
    {
        Device = device,
        Day = day,
        Grain = UsageFactGrain.Day,
        TotalTokens = tokens,
        CostUsd = cost,
        UpdatedAt = at,
    };

    private static async Task<HttpResponseMessage> PushAsync(HttpClient client, string device, SyncPushRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/sync/push");
        message.Headers.Add("X-Tokenmetry-Device", device);
        message.Content = new StringContent(
            JsonSerializer.Serialize(request, SyncJsonContext.Default.SyncPushRequest),
            Encoding.UTF8,
            "application/json");
        return await client.SendAsync(message);
    }

    private static async Task<SyncPullResponse> PullAsync(HttpClient client, string device, long since)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"/api/sync/pull?since={since}");
        message.Headers.Add("X-Tokenmetry-Device", device);
        var response = await client.SendAsync(message);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize(await response.Content.ReadAsStreamAsync(), SyncJsonContext.Default.SyncPullResponse)!;
    }

    [Fact]
    public async Task PushThenPull_RoundTripsRowsBetweenDevices()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = Client(factory);
        using var devB = Client(factory);

        var push = await PushAsync(devA, "devA", new SyncPushRequest(
            UsageFacts: new List<UsageFact>
            {
                UsageRow("devA", "2026-06-19", 100, 1.00m, T1),
                UsageRow("devA", "2026-06-20", 200, 2.00m, T1),
            },
            SessionFacts: new List<SessionFact>
            {
                new() { Device = "devA", SessionId = "s-a", TotalTokens = 100, FirstSeenAt = T1, LastSeenAt = T1, CostUsd = 1.00m, UpdatedAt = T1 },
            }));
        push.EnsureSuccessStatusCode();

        var pulled = await PullAsync(devB, "devB", since: 0);
        pulled.UsageFacts.Should().HaveCount(2, "devB 收到 devA 的两行");
        pulled.UsageFacts.Select(row => row.Device).Should().OnlyContain(device => device == "devA");
        pulled.UsageFacts.Single(row => row.Day == "2026-06-19").TotalTokens.Should().Be(100);
        pulled.UsageFacts.Single(row => row.Day == "2026-06-19").CostUsd.Should().Be(1.00m);
        pulled.SessionFacts.Should().ContainSingle().Which.SessionId.Should().Be("s-a");
        pulled.HasMore.Should().BeFalse();

        var ownPull = await PullAsync(devA, "devA", since: 0);
        ownPull.UsageFacts.Should().BeEmpty("pull 不含 caller 自身行");
        ownPull.SessionFacts.Should().BeEmpty();
    }

    [Fact]
    public async Task Push_LwwSkipsOlderVersion()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = Client(factory);
        using var devB = Client(factory);

        (await PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { UsageRow("devA", "2026-06-19", 222, 2.22m, T2) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { UsageRow("devA", "2026-06-19", 111, 1.11m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var pulled = await PullAsync(devB, "devB", since: 0);
        var row = pulled.UsageFacts.Should().ContainSingle().Subject;
        row.TotalTokens.Should().Be(222, "旧批次不得覆盖新批次（服务端 LWW 防御）");
        row.CostUsd.Should().Be(2.22m);
        row.UpdatedAt.Should().Be(T2);
    }

    [Fact]
    public async Task Pull_SeqMonotonicAndResumable()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = Client(factory);
        using var devB = Client(factory);

        (await PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact>
            {
                UsageRow("devA", "2026-06-19", 100, 1.00m, T1),
                UsageRow("devA", "2026-06-20", 200, 2.00m, T1),
            },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var first = await PullAsync(devB, "devB", since: 0);
        first.UsageFacts.Should().HaveCount(2);

        (await PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { UsageRow("devA", "2026-06-21", 300, 3.00m, T2) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var resumed = await PullAsync(devB, "devB", since: first.Seq);
        resumed.UsageFacts.Should().ContainSingle("since 之后的增量只含第二批");
        resumed.UsageFacts[0].TotalTokens.Should().Be(300);
        resumed.Seq.Should().BeGreaterThan(first.Seq, "seq 账号级单调");
        resumed.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Pull_UpdatesDeviceList()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = Client(factory);
        using var devB = Client(factory);

        (await PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { UsageRow("devA", "2026-06-19", 100, 1.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await PushAsync(devB, "devB", new SyncPushRequest(
            new List<UsageFact> { UsageRow("devB", "2026-06-19", 50, 0.50m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var pulled = await PullAsync(devA, "devA", since: 0);
        pulled.Devices.Select(device => device.DeviceId).Should().BeEquivalentTo("devA", "devB");
        pulled.Devices.Select(device => device.Name).Should().OnlyContain(name => !string.IsNullOrEmpty(name));
    }
}
