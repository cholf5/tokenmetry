namespace Tokenmetry.Core.Tests.Sync;

using System.Text.Json;
using Tokenmetry.Core.Sync;

/// <summary>
/// 排行榜线协议契约（leaderboard 设计 §4.3）：JSON source-gen（camelCase）往返无损——
/// 偏好读/写与公开榜单读形状（decimal 金额过线不得失真）。
/// </summary>
public sealed class LeaderboardContractsTests
{
    [Fact]
    public void SyncJsonContext_RoundTripsLeaderboardPreferenceRequest()
    {
        var request = new LeaderboardPreferenceRequest(true, "Alice");

        var json = JsonSerializer.Serialize(request, SyncJsonContext.Default.LeaderboardPreferenceRequest);
        var restored = JsonSerializer.Deserialize(json, SyncJsonContext.Default.LeaderboardPreferenceRequest);

        restored.Should().NotBeNull();
        restored.Should().BeEquivalentTo(request);
        json.Should().Contain("\"optIn\":true", "wire 走 camelCase");
        json.Should().Contain("\"displayName\":\"Alice\"");
    }

    [Fact]
    public void SyncJsonContext_RoundTripsLeaderboardPreferenceResponse()
    {
        var response = new LeaderboardPreferenceResponse(false, null);

        var json = JsonSerializer.Serialize(response, SyncJsonContext.Default.LeaderboardPreferenceResponse);
        var restored = JsonSerializer.Deserialize(json, SyncJsonContext.Default.LeaderboardPreferenceResponse);

        restored.Should().NotBeNull();
        restored.Should().BeEquivalentTo(response);
        json.Should().Contain("\"optIn\":false");
        json.Should().Contain("\"displayName\":null", "未自定义展示名以 null 过线");
    }

    [Fact]
    public void SyncJsonContext_RoundTripsLeaderboardResponse()
    {
        var entry = new LeaderboardEntry(1, "alice", "https://avatars.githubusercontent.com/u/1", 123456789, 1234.56m);
        var response = new LeaderboardResponse("2026-11", "cost", 42, new List<LeaderboardEntry> { entry });

        var json = JsonSerializer.Serialize(response, SyncJsonContext.Default.LeaderboardResponse);
        var restored = JsonSerializer.Deserialize(json, SyncJsonContext.Default.LeaderboardResponse);

        restored.Should().NotBeNull();
        restored!.Month.Should().Be("2026-11");
        restored.Board.Should().Be("cost");
        restored.Participants.Should().Be(42, "participants 数必须保持");
        restored.Entries.Should().ContainSingle().Which.Should().BeEquivalentTo(entry);
        restored.Entries[0].TotalCostUsd.Should().Be(1234.56m, "decimal 金额过线不得失真");
        restored.Entries[0].Rank.Should().Be(1, "排名必须保持");
        restored.Entries[0].TotalTokens.Should().Be(123456789);
        restored.Entries[0].AvatarUrl.Should().Be("https://avatars.githubusercontent.com/u/1");
    }
}
