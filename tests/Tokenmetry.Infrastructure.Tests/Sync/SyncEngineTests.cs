namespace Tokenmetry.Infrastructure.Tests.Sync;

using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;
using Tokenmetry.Infrastructure.Cache;
using Tokenmetry.Infrastructure.Sync;

/// <summary>
/// 同步引擎（relay 计划 Task 9 / 设计 A13、G3）：增量 push（游标之后本机行）→ pull 循环
/// （hasMore 续拉）→ 远端落地 → 设备清单对账清退 → 游标推进；未开启时零出站。
/// </summary>
public sealed class SyncEngineTests : IAsyncLifetime
{
    private string _tempDbPath = null!;
    private SqliteUsageStore _store = null!;
    private string? _localDeviceId;

    public async Task InitializeAsync()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"tokenmetry-sync-{Guid.NewGuid():N}.db");
        _store = new SqliteUsageStore(_tempDbPath);
        await _store.InitializeAsync();
        _localDeviceId = await _store.GetLocalDeviceIdAsync();
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

    /// <summary>捕获 push/pull 调用的 fake 后端（编排断言的观察点）。</summary>
    private sealed class FakeBackend : ISyncBackend
    {
        public int PushCount;

        public SyncPushRequest? LastPush;

        public List<long> PullSequences { get; } = new();

        public Func<long, SyncPullResponse> PullResponder { get; set; } =
            since => new SyncPullResponse(0, new List<UsageFact>(), new List<SessionFact>(), new List<SyncDeviceInfo>(), false);

        public int DeviceListQueries;

        public Task PushAsync(SyncPushRequest request, CancellationToken ct = default)
        {
            PushCount++;
            LastPush = request;
            return Task.CompletedTask;
        }

        public async Task<SyncPullResponse> PullAsync(long sinceSeq, CancellationToken ct = default)
        {
            PullSequences.Add(sinceSeq);
            return PullResponder(sinceSeq);
        }

        public Task<SyncDeviceListResponse> GetDevicesAsync(CancellationToken ct = default)
        {
            DeviceListQueries++;
            return Task.FromResult(new SyncDeviceListResponse(new List<SyncDeviceInfo>()));
        }

        public Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAccountAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<LeaderboardPreferenceResponse> GetLeaderboardPreferenceAsync(CancellationToken ct = default) =>
            Task.FromResult(new LeaderboardPreferenceResponse(false, null));

        public Task<LeaderboardPreferenceResponse> SetLeaderboardPreferenceAsync(LeaderboardPreferenceRequest request, CancellationToken ct = default) =>
            Task.FromResult(new LeaderboardPreferenceResponse(request.OptIn, request.DisplayName));
    }

    private static SyncDeviceInfo Device(string id, string name = "Device") =>
        new(id, name, new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task RunOnce_PushesLocalSinceCursor_PullsRemote_LandsRows()
    {
        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 10 },
            new() { Day = "2026-06-20", Grain = UsageFactGrain.Full, TotalTokens = 20 },
        });
        var deviceId = await _store.GetLocalDeviceIdAsync();

        var remoteAt = new DateTimeOffset(2026, 6, 19, 11, 0, 0, TimeSpan.Zero);
        var backend = new FakeBackend
        {
            PullResponder = _ => new SyncPullResponse(
                Seq: 7,
                UsageFacts: new List<UsageFact>
                {
                    new() { Device = "devB", Day = "2026-06-21", Grain = UsageFactGrain.Full, TotalTokens = 30, CostUsd = 0.3m, UpdatedAt = remoteAt },
                },
                SessionFacts: new List<SessionFact>
                {
                    new() { Device = "devB", SessionId = "s-b", TotalTokens = 30, FirstSeenAt = remoteAt, LastSeenAt = remoteAt, UpdatedAt = remoteAt },
                },
                Devices: new List<SyncDeviceInfo> { Device("devB", "Laptop") },
                HasMore: false),
        };
        var engine = new SyncEngine(_store, () => backend);

        var result = await engine.RunOnceAsync();

        result.Should().NotBeNull();
        result!.PushedUsage.Should().Be(2);
        result.PushedSessions.Should().Be(0);
        result.PulledUsage.Should().Be(1);
        result.PulledSessions.Should().Be(1);
        backend.LastPush.Should().NotBeNull();
        backend.LastPush!.UsageFacts.Should().HaveCount(2, "push 只含本机行");
        backend.LastPush.UsageFacts.Select(row => row.Device).Should().OnlyContain(device => device == deviceId);

        var facts = await _store.GetUsageFactsAsync();
        facts.Should().Contain(row => row.Device == "devB" && row.TotalTokens == 30, "远端行落地");
        facts.Should().Contain(row => row.Device == deviceId);
        var sessions = await _store.GetSessionFactsAsync();
        sessions.Should().ContainSingle(row => row.SessionId == "s-b");
        (await _store.GetRemoteDevicesAsync()).Should().ContainSingle(device => device.DeviceId == "devB" && device.Name == "Laptop");
    }

    [Fact]
    public async Task RunOnce_PurgesDevicesMissingFromList()
    {
        var at = new DateTimeOffset(2026, 6, 19, 11, 0, 0, TimeSpan.Zero);
        await _store.UpsertRemoteUsageFactsAsync(new List<UsageFact>
        {
            new() { Device = "X", Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 7, UpdatedAt = at },
        });
        await _store.UpsertRemoteDevicesAsync(new List<SyncDeviceInfo> { Device("X", "Ghost") });

        var backend = new FakeBackend
        {
            PullResponder = _ => new SyncPullResponse(
                Seq: 9, new List<UsageFact>(), new List<SessionFact>(),
                new List<SyncDeviceInfo> { Device("devB") }, false),
        };
        var engine = new SyncEngine(_store, () => backend);

        var result = await engine.RunOnceAsync();

        result!.PurgedDeviceIds.Should().Equal(new[] { "X" }, "A13：清单里消失的已知设备被对账清退");
        (await _store.GetUsageFactsAsync()).Should().NotContain(row => row.Device == "X");
        (await _store.GetRemoteDevicesAsync()).Select(device => device.DeviceId).Should().NotContain("X");
        (await _store.GetRemoteDevicesAsync()).Select(device => device.DeviceId).Should().Contain("devB");
    }

    [Fact]
    public async Task RunOnce_AdvancesCursors()
    {
        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 10 },
        });
        var localUpdatedAt = (await _store.GetLocalUsageFactsSinceAsync(DateTimeOffset.MinValue))[0].UpdatedAt!.Value;

        var remoteAt = new DateTimeOffset(2026, 6, 19, 11, 0, 0, TimeSpan.Zero);
        var page = 0;
        var backend = new FakeBackend
        {
            PullResponder = _ =>
            {
                page++;
                return page == 1
                    ? new SyncPullResponse(10, new List<UsageFact>(), new List<SessionFact>(), new List<SyncDeviceInfo> { Device("devB") }, HasMore: true)
                    : new SyncPullResponse(
                        42,
                        new List<UsageFact> { new() { Device = "devB", Day = "2026-06-22", Grain = UsageFactGrain.Full, TotalTokens = 5, UpdatedAt = remoteAt } },
                        new List<SessionFact>(),
                        new List<SyncDeviceInfo> { Device("devB") },
                        HasMore: false);
            },
        };
        var engine = new SyncEngine(_store, () => backend);

        await engine.RunOnceAsync();

        backend.PullSequences.Should().ContainInOrder(new long[] { 0, 10 }, "hasMore 续拉以上一响应 Seq 为游标");
        backend.PullSequences.Should().HaveCount(2);
        var cursor = await _store.GetSyncCursorAsync();
        cursor.LastPushedAt.Should().Be(localUpdatedAt, "推送游标 = 批次内最大 updated_at");
        cursor.LastPullSeq.Should().Be(42, "拉取游标 = 末次响应 Seq");
    }

    [Fact]
    public async Task RunOnce_RemoteDevices_ExcludesCaller()
    {
        // 终审 C2/I3：pull 响应的设备清单包含 caller 自身（服务端如此返回），
        // 落本地 remote_devices 时必须剔除——本机不是"远端设备"，进清单会让 UI 重复出本机、
        // 且让对账清退把本机当作可清退对象。
        var backend = new FakeBackend
        {
            PullResponder = _ => new SyncPullResponse(
                Seq: 3, new List<UsageFact>(), new List<SessionFact>(),
                new List<SyncDeviceInfo> { Device(_localDeviceId!, "Self"), Device("devB", "Laptop") }, false),
        };
        var engine = new SyncEngine(_store, () => backend);

        await engine.RunOnceAsync();

        (await _store.GetRemoteDevicesAsync()).Select(device => device.DeviceId)
            .Should().ContainSingle("本机不得进远端设备清单").Which.Should().Be("devB");
    }

    [Fact]
    public async Task RunOnce_NeverPurgesSelf_EvenWhenListLacksCaller()
    {
        // 终审 C2：设备被远端解绑（或中继重建）后，响应清单缺 caller——
        // 对账清退必须豁免本机，否则空闲设备的本地历史被整表删除（不可恢复）。
        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 10 },
        });

        var backend = new FakeBackend
        {
            PullResponder = _ => new SyncPullResponse(
                Seq: 3, new List<UsageFact>(), new List<SessionFact>(),
                new List<SyncDeviceInfo> { Device("devB") }, false),
        };
        var engine = new SyncEngine(_store, () => backend);

        var result = await engine.RunOnceAsync();

        result!.PurgedDeviceIds.Should().BeEmpty("本机不得被对账清退");
        (await _store.GetUsageFactsAsync()).Should().Contain(row => row.TotalTokens == 10, "本地历史完整保留");
    }

    [Fact]
    public async Task Disabled_NoBackend_NoRequests()
    {
        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 10 },
        });
        var backend = new FakeBackend();
        var engine = new SyncEngine(_store, () => null);

        var result = await engine.RunOnceAsync();

        result.Should().BeNull("未开启同步 → null，零出站（G3）");
        backend.PushCount.Should().Be(0);
        backend.PullSequences.Should().BeEmpty();
        backend.DeviceListQueries.Should().Be(0);
    }
}
