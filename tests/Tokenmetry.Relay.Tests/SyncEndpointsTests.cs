namespace Tokenmetry.Relay.Tests;

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

    [Fact]
    public async Task PushThenPull_RoundTripsRowsBetweenDevices()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        var push = await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            UsageFacts: new List<UsageFact>
            {
                RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1),
                RelayTestSync.UsageRow("devA", "2026-06-20", 200, 2.00m, T1),
            },
            SessionFacts: new List<SessionFact>
            {
                new() { Device = "devA", SessionId = "s-a", TotalTokens = 100, FirstSeenAt = T1, LastSeenAt = T1, CostUsd = 1.00m, UpdatedAt = T1 },
            }));
        push.EnsureSuccessStatusCode();

        var pulled = await RelayTestSync.PullAsync(devB, "devB", since: 0);
        pulled.UsageFacts.Should().HaveCount(2, "devB 收到 devA 的两行");
        pulled.UsageFacts.Select(row => row.Device).Should().OnlyContain(device => device == "devA");
        pulled.UsageFacts.Single(row => row.Day == "2026-06-19").TotalTokens.Should().Be(100);
        pulled.UsageFacts.Single(row => row.Day == "2026-06-19").CostUsd.Should().Be(1.00m);
        pulled.SessionFacts.Should().ContainSingle().Which.SessionId.Should().Be("s-a");
        pulled.HasMore.Should().BeFalse();

        var ownPull = await RelayTestSync.PullAsync(devA, "devA", since: 0);
        ownPull.UsageFacts.Should().BeEmpty("pull 不含 caller 自身行");
        ownPull.SessionFacts.Should().BeEmpty();
    }

    [Fact]
    public async Task Push_LwwSkipsOlderVersion()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 222, 2.22m, T2) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 111, 1.11m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var pulled = await RelayTestSync.PullAsync(devB, "devB", since: 0);
        var row = pulled.UsageFacts.Should().ContainSingle().Subject;
        row.TotalTokens.Should().Be(222, "旧批次不得覆盖新批次（服务端 LWW 防御）");
        row.CostUsd.Should().Be(2.22m);
        row.UpdatedAt.Should().Be(T2);
    }

    [Fact]
    public async Task Pull_SeqMonotonicAndResumable()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact>
            {
                RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1),
                RelayTestSync.UsageRow("devA", "2026-06-20", 200, 2.00m, T1),
            },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var first = await RelayTestSync.PullAsync(devB, "devB", since: 0);
        first.UsageFacts.Should().HaveCount(2);

        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-21", 300, 3.00m, T2) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var resumed = await RelayTestSync.PullAsync(devB, "devB", since: first.Seq);
        resumed.UsageFacts.Should().ContainSingle("since 之后的增量只含第二批");
        resumed.UsageFacts[0].TotalTokens.Should().Be(300);
        resumed.Seq.Should().BeGreaterThan(first.Seq, "seq 账号级单调");
        resumed.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Pull_UpdatesDeviceList()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var devA = RelayTestSync.Client(factory);
        using var devB = RelayTestSync.Client(factory);

        (await RelayTestSync.PushAsync(devA, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", "2026-06-19", 100, 1.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await RelayTestSync.PushAsync(devB, "devB", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devB", "2026-06-19", 50, 0.50m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var pulled = await RelayTestSync.PullAsync(devA, "devA", since: 0);
        pulled.Devices.Select(device => device.DeviceId).Should().BeEquivalentTo("devA", "devB");
        pulled.Devices.Select(device => device.Name).Should().OnlyContain(name => !string.IsNullOrEmpty(name));
    }
}
