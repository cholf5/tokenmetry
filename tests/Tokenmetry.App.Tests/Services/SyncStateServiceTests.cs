namespace Tokenmetry.App.Tests.Services;

using Microsoft.Data.Sqlite;
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

    private static SyncStateService CreateService(
        IUsageStore store,
        AppSettings settings,
        FakeSettingsStore settingsStore,
        FakeBackend backend,
        Func<DateTimeOffset>? clock = null)
    {
        return new SyncStateService(
            store,
            settings,
            settingsStore,
            backendFactory: (_, _) => backend,
            clock: clock ?? Clock,
            timerPeriod: Timeout.InfiniteTimeSpan);
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
}
