namespace Tokenmetry.Core.Tests.Sync;

using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

/// <summary>
/// 同步协议契约（relay 计划 Task 4 / 设计 §4.4）：JSON source-gen（camelCase）往返无损——
/// 全字段（含 Device / CostUsd=null / UpdatedAt）逐字段相等。
/// </summary>
public sealed class SyncContractsTests
{
    [Fact]
    public void SyncJsonContext_RoundTripsUsageFact()
    {
        var fact = new UsageFact
        {
            Device = "devA",
            Day = "2026-06-19",
            Grain = UsageFactGrain.Full,
            Agent = "cc",
            Model = "claude-sonnet",
            Provider = "openrouter",
            Project = "/tmp/proj",
            ProjectDisplayName = "proj",
            InputTokens = 1,
            OutputTokens = 2,
            CacheCreationTokens = 3,
            CacheCreationEphemeral5mTokens = 4,
            CacheCreationEphemeral1hTokens = 5,
            CacheReadTokens = 6,
            TotalTokens = 7,
            CostUsd = null,
            UpdatedAt = new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero),
        };

        var json = JsonSerializer.Serialize(fact, SyncJsonContext.Default.UsageFact);
        var restored = JsonSerializer.Deserialize(json, SyncJsonContext.Default.UsageFact);

        restored.Should().NotBeNull();
        restored.Should().BeEquivalentTo(fact);
        json.Should().Contain("\"totalTokens\":7", "wire 走 camelCase");
        json.Should().Contain("\"costUsd\":null", "未知成本必须以 null 过线（A3 接收端不重算）");
        json.Should().Contain("\"updatedAt\":\"2026-06-19T10:00:00+00:00\"");
    }

    [Fact]
    public void SyncJsonContext_RoundTripsPullResponse()
    {
        var at = new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);
        var fact = new UsageFact
        {
            Device = "devB",
            Day = "2026-06-20",
            Grain = UsageFactGrain.Day,
            TotalTokens = 50,
            CostUsd = 0.5m,
            UpdatedAt = at,
        };
        var session = new SessionFact
        {
            Device = "devB",
            SessionId = "s-b",
            TotalTokens = 50,
            FirstSeenAt = at,
            LastSeenAt = at,
            CostUsd = null,
            UpdatedAt = at,
        };
        var response = new SyncPullResponse(
            Seq: 42,
            UsageFacts: new List<UsageFact> { fact },
            SessionFacts: new List<SessionFact> { session },
            Devices: new List<SyncDeviceInfo> { new("devB", "Device B", at) });

        var json = JsonSerializer.Serialize(response, SyncJsonContext.Default.SyncPullResponse);
        var restored = JsonSerializer.Deserialize(json, SyncJsonContext.Default.SyncPullResponse);

        restored.Should().NotBeNull();
        restored!.Seq.Should().Be(42);
        restored.UsageFacts.Should().ContainSingle().Which.Should().BeEquivalentTo(fact);
        restored.SessionFacts.Should().ContainSingle().Which.Should().BeEquivalentTo(session);
        restored.Devices.Should().Equal(new SyncDeviceInfo("devB", "Device B", at));
    }
}
