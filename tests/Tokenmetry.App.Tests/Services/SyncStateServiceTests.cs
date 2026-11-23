namespace Tokenmetry.App.Tests.Services;

using Microsoft.Data.Sqlite;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Infrastructure.Cache;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

/// <summary>
/// SyncStateService 应用层装配（relay 计划 Task 10）：Enable 持久化并立即跑一轮、
/// Disable 停定时器不再出站、改名本地+远端贯通。fake backend 计数，真实临时 SqliteUsageStore。
/// </summary>
public sealed class SyncStateServiceTests : IAsyncLifetime
{
    private string _tempDbPath = null!;
    private SqliteUsageStore _store = null!;

    public async Task InitializeAsync()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"tokenmetry-syncstate-{Guid.NewGuid():N}.db");
        _store = new SqliteUsageStore(_tempDbPath);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (!File.Exists(_tempDbPath))
        {
            return;
        }

        for (var i = 0; i < 5; i++)
        {
            try
            {
                File.Delete(_tempDbPath);
                break;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>捕获调用的 fake 后端。</summary>
    private sealed class FakeBackend : ISyncBackend
    {
        public int PushCount;

        public List<long> PullSequences { get; } = new();

        public (string DeviceId, string Name)? Renamed { get; private set; }

        public Task PushAsync(SyncPushRequest request, CancellationToken ct = default)
        {
            PushCount++;
            return Task.CompletedTask;
        }

        public Task<SyncPullResponse> PullAsync(long sinceSeq, CancellationToken ct = default)
        {
            PullSequences.Add(sinceSeq);
            return Task.FromResult(new SyncPullResponse(
                Seq: 5,
                UsageFacts: new List<UsageFact>(),
                SessionFacts: new List<SessionFact>(),
                Devices: new List<SyncDeviceInfo>()));
        }

        public Task<SyncDeviceListResponse> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult(new SyncDeviceListResponse(new List<SyncDeviceInfo>()));

        public Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default)
        {
            Renamed = (deviceId, name);
            return Task.CompletedTask;
        }

        public Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAccountAsync(CancellationToken ct = default) => Task.CompletedTask;

        public LeaderboardPreferenceRequest? LeaderboardSaved { get; private set; }

        public Task<LeaderboardPreferenceResponse> GetLeaderboardPreferenceAsync(CancellationToken ct = default) =>
            Task.FromResult(new LeaderboardPreferenceResponse(true, "relay-alice"));

        public Task<LeaderboardPreferenceResponse> SetLeaderboardPreferenceAsync(LeaderboardPreferenceRequest request, CancellationToken ct = default)
        {
            LeaderboardSaved = request;
            return Task.FromResult(new LeaderboardPreferenceResponse(request.OptIn, request.DisplayName));
        }

        public Task<IReadOnlyList<SyncedSetting>> GetSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SyncedSetting>>(Array.Empty<SyncedSetting>());

        public Task<IReadOnlyList<SyncedSetting>> PutSettingsAsync(IReadOnlyList<SyncedSettingUpdate> settings, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SyncedSetting>>(Array.Empty<SyncedSetting>());
    }

    /// <summary>捕获保存调用的 settings 存储桩。</summary>
    private sealed class FakeSettingsStore : IAppSettingsStore
    {
        public int SaveCount;

        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private static DateTimeOffset Clock() => new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSettingsSyncStateStore : ISettingsSyncStateStore
    {
        public Dictionary<string, SyncedSettingState> Rows { get; } = new(StringComparer.Ordinal);

        public int ClearCount;

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
            ClearCount++;
            Rows.Clear();
            return Task.CompletedTask;
        }
    }

    private static SyncStateService CreateService(
        IUsageStore store,
        AppSettings settings,
        FakeSettingsStore settingsStore,
        FakeBackend backend,
        Func<DateTimeOffset>? clock = null,
        ISettingsSyncStateStore? settingsSyncStateStore = null)
    {
        return new SyncStateService(
            store,
            settings,
            settingsStore,
            backendFactory: (_, _) => backend,
            clock: clock ?? Clock,
            timerPeriod: Timeout.InfiniteTimeSpan,
            settingsSyncStateStore: settingsSyncStateStore);
    }

    [Fact]
    public async Task Enable_SameCredentials_KeepsSettingsSyncState()
    {
        var settings = new AppSettings();
        var state = new FakeSettingsSyncStateStore();
        var service = CreateService(_store, settings, new FakeSettingsStore(), new FakeBackend(), settingsSyncStateStore: state);

        await service.InitializeAsync();
        await service.EnableAsync("http://relay.example:8080", "tok"); // 首次启用：新账号 → 清一次
        state.Rows["theme"] = new SyncedSettingState("theme", "dark", Clock().AddHours(1));
        await service.DisableAsync();
        await service.EnableAsync("http://relay.example:8080", "tok");

        state.ClearCount.Should().Be(1, "同账号关闭→再开启保留设置同步账面");
        state.Rows.Should().ContainKey("theme", "断网期间的本地改动仍按本地优先处理");
    }

    [Fact]
    public async Task Enable_NewCredentials_ClearsSettingsSyncState()
    {
        var settings = new AppSettings();
        var state = new FakeSettingsSyncStateStore();
        state.Rows["theme"] = new SyncedSettingState("theme", "dark", Clock());
        var service = CreateService(_store, settings, new FakeSettingsStore(), new FakeBackend(), settingsSyncStateStore: state);

        await service.InitializeAsync();
        await service.EnableAsync("http://relay.example:8080", "tok");
        await service.EnableAsync("http://relay.example:8080", "other-token");

        state.ClearCount.Should().Be(2, "换 token = 换账号，首同步采纳新桶");
        state.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Enable_PersistsAndRunsOnce()
    {
        var settings = new AppSettings();
        var settingsStore = new FakeSettingsStore();
        var backend = new FakeBackend();
        var service = CreateService(_store, settings, settingsStore, backend);

        await service.InitializeAsync();
        await service.EnableAsync("http://relay.example:8080", "tok");

        settings.SyncEnabled.Should().BeTrue("开关持久化");
        settings.SyncEndpoint.Should().Be("http://relay.example:8080");
        settings.SyncToken.Should().Be("tok");
        settingsStore.SaveCount.Should().BeGreaterThanOrEqualTo(1);
        service.IsEnabled.Should().BeTrue();
        backend.PullSequences.Should().HaveCount(1, "Enable 立即跑一轮同步（LastError={0}）", service.LastError);
        service.LocalDeviceId.Should().Be(await _store.GetLocalDeviceIdAsync());
    }

    [Fact]
    public async Task Disable_StopsTimer_NoFurtherRuns()
    {
        var settings = new AppSettings { RefreshMinutes = 1 };
        var settingsStore = new FakeSettingsStore();
        var backend = new FakeBackend();
        var now = Clock();
        var service = CreateService(_store, settings, settingsStore, backend, clock: () => now);

        await service.InitializeAsync();
        await service.EnableAsync("http://relay.example:8080", "tok");
        backend.PullSequences.Should().HaveCount(1);

        await service.DisableAsync();

        settings.SyncEnabled.Should().BeFalse();
        service.IsEnabled.Should().BeFalse();
        // 定时器已停：把时钟拨过任何节流窗口并手动触发一次 tick，也不得再出站。
        now = now.AddHours(3);
        service.TimerTickForTests();
        await Task.Delay(200);
        backend.PullSequences.Should().HaveCount(1, "Disable 后零出站（G3）");
    }

    [Fact]
    public async Task Enable_ResetsCursors_FullResync()
    {
        // 终审 I5：Enable 必须重置同步游标（中继重建 / 切换账号场景）——否则旧游标让全量重推
        // 不会发生，新账号/新中继永远看不到历史；重置后的全量 push/pull 由两端 LWW 幂等消化。
        var settings = new AppSettings();
        var settingsStore = new FakeSettingsStore();
        var backend = new FakeBackend();
        var stale = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await _store.SetSyncCursorAsync(stale, 42);

        var service = CreateService(_store, settings, settingsStore, backend);
        await service.InitializeAsync();
        await service.EnableAsync("http://relay.example:8080", "tok");

        // Enable 内重置游标后立即跑一轮：pull 从 0 起（fake 收到 Pull(0)），游标最终 = 本轮结果
        //（pull Seq=5 而非旧值 42/max(42,5)），且推送游标不再残留旧值语义。
        backend.PullSequences.Should().Contain(0L, "游标已重置：首轮从 0 全量拉取");
        var cursor = await _store.GetSyncCursorAsync();
        cursor.LastPullSeq.Should().Be(5L);
    }

    [Fact]
    public async Task Rename_UpdatesLocalAndRemote()
    {
        var settings = new AppSettings();
        var settingsStore = new FakeSettingsStore();
        var backend = new FakeBackend();
        var service = CreateService(_store, settings, settingsStore, backend);

        await service.InitializeAsync();
        await service.EnableAsync("http://relay.example:8080", "tok");

        await service.RenameDeviceAsync("我的笔记本");

        (await _store.GetLocalDeviceNameAsync()).Should().Be("我的笔记本");
        backend.Renamed.Should().Be((service.LocalDeviceId, "我的笔记本"));
    }

    [Fact]
    public async Task Enable_BlankCredentials_ThrowsAndLeavesStateUntouched()
    {
        // 审查 #4：设置页 VM 只是第一道闸，服务层也必须拒绝空凭据（否则每轮 401 且无人提示）。
        var settings = new AppSettings();
        var backend = new FakeBackend();
        var service = CreateService(_store, settings, new FakeSettingsStore(), backend);
        await service.InitializeAsync();

        var enable = async () => await service.EnableAsync("http://relay.example:8080", "   ");
        await enable.Should().ThrowAsync<ArgumentException>();

        settings.SyncEnabled.Should().BeFalse();
        settings.SyncToken.Should().BeEmpty("拒绝必须发生在覆盖配置之前");
        backend.PullSequences.Should().BeEmpty("没有建后端、没有出站");
    }

    [Fact]
    public async Task Initialize_EnabledWithEmptyToken_SelfHealsToDisabled()
    {
        // 审查 #4：历史 bug 会把持久化 token 覆盖成空串；已启用态会把配置区藏起来，用户无从修复。
        var settings = new AppSettings
        {
            SyncEnabled = true,
            SyncEndpoint = "http://relay.example:8080",
            SyncToken = string.Empty,
        };
        var settingsStore = new FakeSettingsStore();
        var backend = new FakeBackend();
        var built = 0;
        var service = new SyncStateService(
            _store,
            settings,
            settingsStore,
            backendFactory: (_, _) =>
            {
                built++;
                return backend;
            },
            clock: Clock,
            timerPeriod: Timeout.InfiniteTimeSpan);

        await service.InitializeAsync();

        built.Should().Be(0, "空 token 不建后端、不出站");
        service.IsEnabled.Should().BeFalse("坏掉的启用态就地降为关闭");
        settings.SyncEnabled.Should().BeFalse("降级要持久化，重启后不再假装已启用");
        settingsStore.SaveCount.Should().Be(1);
        service.LastError.Should().Be(Translations.Instance.SyncCredentialsRequired, "状态行要写明原因");
    }

    [Fact]
    public async Task LeaderboardPreference_PassthroughReachesBackend()
    {
        var settings = new AppSettings();
        var settingsStore = new FakeSettingsStore();
        var backend = new FakeBackend();
        var service = CreateService(_store, settings, settingsStore, backend);
        await service.InitializeAsync();
        await service.EnableAsync("http://relay.example:8080", "tok");

        (await service.GetLeaderboardPreferenceAsync())
            .Should().Be(new LeaderboardPreferenceResponse(true, "relay-alice"));

        await service.SetLeaderboardPreferenceAsync(new LeaderboardPreferenceRequest(true, "alice"));

        backend.LeaderboardSaved.Should().Be(new LeaderboardPreferenceRequest(true, "alice"));
    }

    [Fact]
    public async Task LeaderboardPreference_WhenSyncDisabled_Throws()
    {
        var settings = new AppSettings();
        var settingsStore = new FakeSettingsStore();
        var backend = new FakeBackend();
        var service = CreateService(_store, settings, settingsStore, backend);
        await service.InitializeAsync(); // 未 Enable：_backend 为 null

        var get = async () => await service.GetLeaderboardPreferenceAsync();
        await get.Should().ThrowAsync<InvalidOperationException>();
        var set = async () => await service.SetLeaderboardPreferenceAsync(new LeaderboardPreferenceRequest(false, null));
        await set.Should().ThrowAsync<InvalidOperationException>();
        backend.LeaderboardSaved.Should().BeNull("未启用时不得出站");
    }
}
