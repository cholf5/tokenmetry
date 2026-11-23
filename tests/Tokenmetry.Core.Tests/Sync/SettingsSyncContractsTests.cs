namespace Tokenmetry.Core.Tests.Sync;

using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

/// <summary>
/// 设置同步线协议（settings-sync 设计 §3/§4.2）：camelCase 往返、服务端时间戳保留、
/// 预设清单 JSON 数组可经 source-gen 编码（value 文本里的二次编码）。
/// </summary>
public sealed class SettingsSyncContractsTests
{
    [Fact]
    public void SyncJsonContext_RoundTripsSettingsPayload()
    {
        var at = new DateTimeOffset(2026, 11, 23, 12, 34, 56, TimeSpan.Zero);
        var response = new SettingsSyncResponse(
        [
            new SyncedSetting("theme", "dark", at),
            new SyncedSetting("sharePosterMargin", "42.5", at),
        ]);

        var json = JsonSerializer.Serialize(response, SyncJsonContext.Default.SettingsSyncResponse);
        var restored = JsonSerializer.Deserialize(json, SyncJsonContext.Default.SettingsSyncResponse);

        restored.Should().NotBeNull();
        restored!.Settings.Should().BeEquivalentTo(response.Settings);
        json.Should().Contain("\"key\":\"theme\"");
        json.Should().Contain("\"updatedAt\":\"2026-11-23T12:34:56+00:00\"");
    }

    [Fact]
    public void SyncJsonContext_RoundTripsSettingsPutRequest()
    {
        var request = new SettingsSyncPutRequest(
        [
            new SyncedSettingUpdate("language", "zh-hant"),
        ]);

        var json = JsonSerializer.Serialize(request, SyncJsonContext.Default.SettingsSyncPutRequest);
        var restored = JsonSerializer.Deserialize(json, SyncJsonContext.Default.SettingsSyncPutRequest);

        restored.Should().NotBeNull();
        restored!.Settings.Should().ContainSingle().Which.Should().Be(new SyncedSettingUpdate("language", "zh-hant"));
    }

    [Fact]
    public void SyncJsonContext_RoundTripsPresetList()
    {
        var presets = new List<RelativeRangePresetSetting>
        {
            new() { Id = "preset:1", Kind = "RollingDays", Offset = 0, Days = 14 },
        };

        var json = JsonSerializer.Serialize(presets, SyncJsonContext.Default.ListRelativeRangePresetSetting);
        var restored = JsonSerializer.Deserialize(json, SyncJsonContext.Default.ListRelativeRangePresetSetting);

        restored.Should().NotBeNull();
        restored!.Should().BeEquivalentTo(presets);
    }
}
