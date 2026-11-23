using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;
using Xunit;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// SettingsSyncRunner（settings-sync 设计 §5.2）：首同步采纳远端、本地变更只推差异、
/// 远端更新才应用、本地未推变更不被旧远端覆盖、关开关零出站。
/// </summary>
public sealed class SettingsSyncRunnerTests
{
    private sealed class FakeBackend : ISyncBackend
    {
        public List<SyncedSetting> Remote { get; } = new();

        public List<SyncedSettingUpdate> LastPut { get; } = new();

        public int GetCount;

        public int PutCount;

        public bool ThrowOnPut;

        private DateTimeOffset _clock = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        public Task<IReadOnlyList<SyncedSetting>> GetSettingsAsync(CancellationToken ct = default)
        {
            GetCount++;
            return Task.FromResult<IReadOnlyList<SyncedSetting>>(
                Remote.OrderBy(row => row.Key, StringComparer.Ordinal).ToList());
        }

        public Task<IReadOnlyList<SyncedSetting>> PutSettingsAsync(IReadOnlyList<SyncedSettingUpdate> settings, CancellationToken ct = default)
        {
            PutCount++;
            if (ThrowOnPut)
            {
                throw new HttpRequestException("relay unavailable");
            }
            _clock = _clock.AddSeconds(1);
            var written = settings.Select(update => new SyncedSetting(update.Key, update.Value, _clock)).ToList();
            foreach (var row in written)
            {
                Remote.RemoveAll(existing => existing.Key == row.Key);
                Remote.Add(row);
            }

            LastPut.AddRange(settings);
            return Task.FromResult<IReadOnlyList<SyncedSetting>>(written);
        }

        public Task PushAsync(SyncPushRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SyncPullResponse> PullAsync(long sinceSeq, CancellationToken ct = default) =>
            Task.FromResult(new SyncPullResponse(0, new List<UsageFact>(), new List<SessionFact>(), new List<SyncDeviceInfo>(), false));
        public Task<SyncDeviceListResponse> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult(new SyncDeviceListResponse(new List<SyncDeviceInfo>()));
        public Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAccountAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<LeaderboardPreferenceResponse> GetLeaderboardPreferenceAsync(CancellationToken ct = default) =>
            Task.FromResult(new LeaderboardPreferenceResponse(false, null));
        public Task<LeaderboardPreferenceResponse> SetLeaderboardPreferenceAsync(LeaderboardPreferenceRequest request, CancellationToken ct = default) =>
            Task.FromResult(new LeaderboardPreferenceResponse(request.OptIn, request.DisplayName));
    }

    private sealed class MemoryStateStore : ISettingsSyncStateStore
    {
        public Dictionary<string, SyncedSettingState> Rows { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<SyncedSettingState>> GetSettingsSyncStateAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SyncedSettingState>>(Rows.Values.ToList());

        public Task UpsertSettingsSyncStateAsync(IReadOnlyList<SyncedSettingState> rows, CancellationToken ct = default)
        {
            foreach (var row in rows)
            {
                Rows[row.Key] = row;
            }

            return Task.CompletedTask;
        }

        public Task ClearSettingsSyncStateAsync(CancellationToken ct = default)
        {
            Rows.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public int SaveCount;

        public bool ThrowOnSave;

        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            if (ThrowOnSave)
            {
                throw new IOException("disk unavailable");
            }

            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        public AppSettings Settings { get; } = new();

        public FakeBackend Backend { get; } = new();

        public MemoryStateStore State { get; } = new();

        public FakeSettingsStore SettingsStore { get; } = new();

        public SettingsSyncRunner Runner => new(Settings, State, SettingsStore, () => Backend);

        public Fixture(bool enabled = true) => Settings.SettingsSyncEnabled = enabled;
    }

    [Fact]
    public async Task FirstSync_EmptyRemote_PushesAllKeys()
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunOnceAsync();

        outcome.Applied.Should().BeEmpty("远端为空时没有可应用的值");
        outcome.HasConflict.Should().BeFalse("远端为空没有冲突可问");
        fixture.Backend.LastPut.Should().HaveCount(SettingsSyncKeys.All.Count, "首同步把本机全部偏好推上去");
        fixture.State.Rows.Should().HaveCount(SettingsSyncKeys.All.Count);
        fixture.SettingsStore.SaveCount.Should().Be(0, "没有远端值写回就不需要落盘");
    }

    [Fact]
    public async Task FirstSync_AdoptsRemoteValues_AndDoesNotPushThemBack()
    {
        var fixture = new Fixture();
        fixture.Settings.SettingsSyncFirstSyncPolicy = SettingsSyncFirstSyncPolicies.UseRemote;
        var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        fixture.Backend.Remote.Add(new SyncedSetting(SettingsSyncKeys.Theme, "dark", at));
        fixture.Backend.Remote.Add(new SyncedSetting(SettingsSyncKeys.SharePosterMargin, "42", at));

        var outcome = await fixture.Runner.RunOnceAsync();

        outcome.Applied.Should().BeEquivalentTo([SettingsSyncKeys.Theme, SettingsSyncKeys.SharePosterMargin]);
        fixture.Settings.Theme.Should().Be("dark");
        fixture.Settings.SharePosterMargin.Should().Be(42);
        fixture.Backend.LastPut.Should().NotContain(update => update.Key == SettingsSyncKeys.Theme, "采纳的远端值不回推");
        fixture.Backend.LastPut.Should().NotContain(update => update.Key == SettingsSyncKeys.SharePosterMargin);
        fixture.SettingsStore.SaveCount.Should().Be(1, "远端值写回后落盘一次");
    }

    [Fact]
    public async Task LocalChange_PushesOnlyThatKey()
    {
        var fixture = new Fixture();
        await fixture.Runner.RunOnceAsync(); // 首同步：全量推
        fixture.Backend.LastPut.Clear();
        fixture.Settings.Theme = "light";

        var outcome = await fixture.Runner.RunOnceAsync();

        outcome.Applied.Should().BeEmpty();
        fixture.Backend.LastPut.Should().ContainSingle().Which.Key.Should().Be(SettingsSyncKeys.Theme);
    }

    [Fact]
    public async Task RemoteChange_WithNewerTimestamp_IsAppliedAndPersisted()
    {
        var fixture = new Fixture();
        fixture.Settings.Theme = "light";
        await fixture.Runner.RunOnceAsync();
        fixture.Backend.LastPut.Clear();

        fixture.Backend.Remote.RemoveAll(row => row.Key == SettingsSyncKeys.Theme);
        fixture.Backend.Remote.Add(new SyncedSetting(
            SettingsSyncKeys.Theme, "dark", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var outcome = await fixture.Runner.RunOnceAsync();

        outcome.Applied.Should().ContainSingle().Which.Should().Be(SettingsSyncKeys.Theme);
        fixture.Settings.Theme.Should().Be("dark");
        fixture.SettingsStore.SaveCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LocalPendingChange_IsNotOverwrittenByOlderRemote()
    {
        var fixture = new Fixture();
        fixture.Settings.Theme = "light";
        await fixture.Runner.RunOnceAsync();
        fixture.Backend.LastPut.Clear();

        // 本机改了、还没推；远端来了一条更新的时间戳但值仍是旧档——本地变更必须优先。
        fixture.Settings.Theme = "dark";
        fixture.Backend.Remote.RemoveAll(row => row.Key == SettingsSyncKeys.Theme);
        fixture.Backend.Remote.Add(new SyncedSetting(
            SettingsSyncKeys.Theme, "light", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var outcome = await fixture.Runner.RunOnceAsync();

        outcome.Applied.Should().BeEmpty("本地未推变更不被远端覆盖");
        fixture.Settings.Theme.Should().Be("dark");
        fixture.Backend.LastPut.Should().ContainSingle().Which.Value.Should().Be("dark");
    }

    [Fact]
    public async Task AppliedRemote_IsPersistedBeforeLedgerAdvances()
    {
        // 审查 #2：账面若先于磁盘推进，落盘失败后重启会把本机旧值当「本地变更」反推，覆盖更新的远端。
        var fixture = new Fixture();
        fixture.Settings.SettingsSyncFirstSyncPolicy = SettingsSyncFirstSyncPolicies.UseRemote;
        fixture.SettingsStore.ThrowOnSave = true;
        fixture.Backend.Remote.Add(new SyncedSetting(
            SettingsSyncKeys.Theme, "dark", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var run = async () => await fixture.Runner.RunOnceAsync();
        await run.Should().ThrowAsync<IOException>();

        fixture.State.Rows.Should().NotContainKey(SettingsSyncKeys.Theme, "落盘失败时账面不得领先");
        fixture.Backend.PutCount.Should().Be(0, "落盘失败即中止本轮，不再推送");
    }

    [Fact]
    public async Task PushFailure_StillPersistsAppliedRemoteValues()
    {
        var fixture = new Fixture();
        fixture.Settings.SettingsSyncFirstSyncPolicy = SettingsSyncFirstSyncPolicies.UseRemote;
        fixture.Backend.ThrowOnPut = true;
        fixture.Backend.Remote.Add(new SyncedSetting(
            SettingsSyncKeys.Theme, "dark", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var run = async () => await fixture.Runner.RunOnceAsync();
        await run.Should().ThrowAsync<HttpRequestException>();

        fixture.Settings.Theme.Should().Be("dark");
        fixture.SettingsStore.SaveCount.Should().Be(1, "采纳的远端值在推送之前就已落盘");
        fixture.State.Rows.Should().ContainKey(SettingsSyncKeys.Theme);
    }

    [Fact]
    public async Task FirstSync_RemoteDiffers_Unresolved_ReportsConflictWithoutApplyingOrPushing()
    {
        // 审查 #3：首同步远端与本机不同且用户未裁决——停下询问，不静默覆盖任何一方。
        var fixture = new Fixture();
        fixture.Backend.Remote.Add(new SyncedSetting(
            SettingsSyncKeys.Theme, "dark", new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero)));

        var outcome = await fixture.Runner.RunOnceAsync();

        outcome.HasConflict.Should().BeTrue();
        outcome.ConflictKeys.Should().Equal(SettingsSyncKeys.Theme);
        fixture.Settings.Theme.Should().Be("system", "未裁决前不应用远端");
        fixture.Backend.PutCount.Should().Be(0, "未裁决前不推送");
        fixture.State.Rows.Should().BeEmpty("未裁决前不记账");
    }

    [Fact]
    public async Task FirstSync_RemoteMatchesLocal_NoConflict()
    {
        var fixture = new Fixture();
        fixture.Backend.Remote.Add(new SyncedSetting(
            SettingsSyncKeys.Theme, fixture.Settings.Theme, new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero)));

        var outcome = await fixture.Runner.RunOnceAsync();

        outcome.HasConflict.Should().BeFalse("值相同没有可问的");
        fixture.Backend.LastPut.Should().NotBeEmpty("缺的本机 key 照常推上去");
    }

    [Fact]
    public async Task FirstSync_UseLocalPolicy_PushesLocalWithoutApplyingRemote()
    {
        var fixture = new Fixture();
        fixture.Settings.SettingsSyncFirstSyncPolicy = SettingsSyncFirstSyncPolicies.UseLocal;
        fixture.Settings.Theme = "light";
        fixture.Backend.Remote.Add(new SyncedSetting(
            SettingsSyncKeys.Theme, "dark", new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero)));

        var outcome = await fixture.Runner.RunOnceAsync();

        outcome.Applied.Should().BeEmpty("保留本机不采纳远端");
        fixture.Settings.Theme.Should().Be("light");
        fixture.Backend.Remote.Single(row => row.Key == SettingsSyncKeys.Theme).Value
            .Should().Be("light", "本机值覆盖云端");
    }

    [Fact]
    public async Task FreshBucket_RemoteEmptyWithLedger_PushesEverythingAgain()
    {
        // 审查 #5：中继删库重建（同凭据）——账面无变化可推时也必须全量重推，否则新桶永远空着。
        var fixture = new Fixture();
        await fixture.Runner.RunOnceAsync(); // 首同步推到「旧桶」
        fixture.Backend.LastPut.Clear();
        fixture.Backend.Remote.Clear(); // 服务端库被重建：远端全空，本机账面仍在

        var outcome = await fixture.Runner.RunOnceAsync();

        outcome.HasConflict.Should().BeFalse("远端为空没有冲突");
        fixture.Backend.LastPut.Should().HaveCount(SettingsSyncKeys.All.Count, "新桶必须全量重推本机偏好");
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        var fixture = new Fixture(enabled: false);

        var outcome = await fixture.Runner.RunOnceAsync();

        outcome.Applied.Should().BeEmpty();
        fixture.Backend.GetCount.Should().Be(0);
        fixture.Backend.PutCount.Should().Be(0);
    }
}
