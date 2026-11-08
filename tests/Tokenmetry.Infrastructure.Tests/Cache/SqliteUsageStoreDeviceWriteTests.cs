namespace Tokenmetry.Infrastructure.Tests.Cache;

using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;
using Tokenmetry.Infrastructure.Cache;

/// <summary>
/// store 写路径设备化（relay 计划 Task 2 / 设计 §4.2、§4.3）：
/// 本机盖戳、值变化检测（A14）、Replace/Delete 作用域收窄到本机（防误删远端行）、
/// 远端 LWW 落地、同步游标与设备清单读写。
/// </summary>
public sealed class SqliteUsageStoreDeviceWriteTests : IAsyncLifetime
{
    private string _tempDbPath = null!;
    private SqliteUsageStore _store = null!;
    private string _deviceId = null!;

    public async Task InitializeAsync()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"tokenmetry-test-{Guid.NewGuid():N}.db");
        _store = new SqliteUsageStore(_tempDbPath);
        await _store.InitializeAsync();
        _deviceId = await _store.GetLocalDeviceIdAsync();
    }

    public async Task DisposeAsync()
    {
        // A-1（Windows）：先清连接池再删（同 SqliteUsageStoreTests）。
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

    [Fact]
    public async Task LocalUpsert_StampsLocalDevice()
    {
        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 10 },
        });
        await _store.UpsertSessionFactsAsync(new List<SessionFact>
        {
            new() { SessionId = "s1", FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow },
        });

        (await _store.GetUsageFactsAsync()).Single().Device.Should().Be(_deviceId);
        (await _store.GetSessionFactsAsync()).Single().Device.Should().Be(_deviceId);
    }

    [Fact]
    public async Task UpsertSameContent_KeepsUpdatedAt()
    {
        // 以远端路径落一行"很旧"的 updated_at（内容与后续本地 upsert 完全一致）：
        // 若值变化检测（A14）失效，本地 upsert 会把时间戳刷成 Now，断言即证伪。
        var stale = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await _store.UpsertRemoteUsageFactsAsync(new List<UsageFact>
        {
            new() { Device = _deviceId, Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 10, UpdatedAt = stale },
        });

        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 10 },
        });

        var stored = (await _store.GetUsageFactsAsync()).Single();
        stored.TotalTokens.Should().Be(10);
        stored.UpdatedAt.Should().Be(stale, "内容与现值完全一致时不得刷新 updated_at（A14，增量推送的前提）");

        // 值变化 → 盖新戳
        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 11 },
        });
        var changed = (await _store.GetUsageFactsAsync()).Single();
        changed.TotalTokens.Should().Be(11);
        changed.UpdatedAt.Should().BeAfter(stale, "值变化必须盖新戳");
    }

    [Fact]
    public async Task SessionUpsertSameContent_KeepsUpdatedAt()
    {
        var stale = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var seen = new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero);
        await _store.UpsertRemoteSessionFactsAsync(new List<SessionFact>
        {
            new() { Device = _deviceId, SessionId = "s1", TotalTokens = 10, FirstSeenAt = seen, LastSeenAt = seen, UpdatedAt = stale },
        });

        await _store.UpsertSessionFactsAsync(new List<SessionFact>
        {
            new() { SessionId = "s1", TotalTokens = 10, FirstSeenAt = seen, LastSeenAt = seen },
        });

        var stored = (await _store.GetSessionFactsAsync()).Single();
        stored.TotalTokens.Should().Be(10);
        stored.UpdatedAt.Should().Be(stale, "会话行同样受值变化检测约束");
    }

    [Fact]
    public async Task ReplaceByDay_OnlyTouchesLocalRows()
    {
        var day = "2026-06-19";
        var remoteAt = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            new() { Day = day, Grain = UsageFactGrain.Day, TotalTokens = 100 },
        });
        await _store.UpsertRemoteUsageFactsAsync(new List<UsageFact>
        {
            new() { Device = "other", Day = day, Grain = UsageFactGrain.Day, TotalTokens = 200, UpdatedAt = remoteAt },
        });

        // 本机按日重建：新一批只含 model 粒度行——远端行不得被误删（§4.3 地雷 2）
        await _store.ReplaceUsageFactsByDayAsync(new List<UsageFact>
        {
            new() { Day = day, Grain = UsageFactGrain.Model, Model = "m", TotalTokens = 150 },
        });

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().NotContain(f => f.Device == _deviceId && f.Grain == UsageFactGrain.Day, "本机分解行被整日替换");
        var remote = stored.Single(f => f.Device == "other");
        remote.TotalTokens.Should().Be(200, "远端行原样保留");
        remote.UpdatedAt.Should().Be(remoteAt);
    }

    [Fact]
    public async Task DeleteDecomposition_OnlyLocal()
    {
        var day = "2026-06-19";
        await _store.UpsertUsageFactsAsync(new List<UsageFact>
        {
            new() { Day = day, Grain = UsageFactGrain.Day, TotalTokens = 100 },
        });
        await _store.UpsertRemoteUsageFactsAsync(new List<UsageFact>
        {
            new() { Device = "other", Day = day, Grain = UsageFactGrain.Day, TotalTokens = 200, UpdatedAt = DateTimeOffset.UtcNow },
        });

        await _store.DeleteDecompositionFactsAsync();

        var stored = await _store.GetUsageFactsAsync();
        stored.Should().ContainSingle("本机分解行清掉，远端行不受影响");
        stored[0].Device.Should().Be("other");
    }

    [Fact]
    public async Task RemoteUpsert_LwwSkipsOlder()
    {
        var t1 = new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 6, 19, 11, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        UsageFact Row(long tokens, DateTimeOffset at) => new()
        {
            Device = "X", Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = tokens, UpdatedAt = at,
        };

        await _store.UpsertRemoteUsageFactsAsync(new List<UsageFact> { Row(222, t2) });
        await _store.UpsertRemoteUsageFactsAsync(new List<UsageFact> { Row(111, t1) });

        (await _store.GetUsageFactsAsync()).Single(f => f.Device == "X").TotalTokens.Should().Be(222, "旧批次不得覆盖新批次（LWW 防御）");

        await _store.UpsertRemoteUsageFactsAsync(new List<UsageFact> { Row(333, t3) });
        var stored = (await _store.GetUsageFactsAsync()).Single(f => f.Device == "X");
        stored.TotalTokens.Should().Be(333);
        stored.UpdatedAt.Should().Be(t3);
    }

    [Fact]
    public async Task LocalSince_FiltersByDeviceAndTime()
    {
        var t1 = new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 6, 19, 11, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        await _store.UpsertRemoteUsageFactsAsync(new List<UsageFact>
        {
            new() { Device = _deviceId, Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 1, UpdatedAt = t1 },
            new() { Device = _deviceId, Day = "2026-06-21", Grain = UsageFactGrain.Full, TotalTokens = 3, UpdatedAt = t3 },
            new() { Device = "other", Day = "2026-06-20", Grain = UsageFactGrain.Full, TotalTokens = 2, UpdatedAt = t2 },
        });

        var rows = await _store.GetLocalUsageFactsSinceAsync(t1);

        rows.Should().ContainSingle("since 严格大于，且只含本机行");
        rows[0].TotalTokens.Should().Be(3);
        rows[0].UpdatedAt.Should().Be(t3);
    }

    [Fact]
    public async Task RemoteDeviceData_DeleteRemovesRowsAndRegistry()
    {
        var at = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        await _store.UpsertRemoteUsageFactsAsync(new List<UsageFact>
        {
            new() { Device = "X", Day = "2026-06-19", Grain = UsageFactGrain.Full, TotalTokens = 7, UpdatedAt = at },
        });
        await _store.UpsertRemoteSessionFactsAsync(new List<SessionFact>
        {
            new() { Device = "X", SessionId = "s-x", TotalTokens = 7, FirstSeenAt = at, LastSeenAt = at, UpdatedAt = at },
        });
        await _store.UpsertRemoteDevicesAsync(new List<SyncDeviceInfo> { new("X", "Device X", at) });

        await _store.DeleteRemoteDeviceDataAsync("X");

        (await _store.GetUsageFactsAsync()).Should().NotContain(f => f.Device == "X");
        (await _store.GetSessionFactsAsync()).Should().NotContain(f => f.Device == "X");
        (await _store.GetRemoteDevicesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SyncCursor_RoundTrips()
    {
        var initial = await _store.GetSyncCursorAsync();
        initial.LastPushedAt.Should().BeNull();
        initial.LastPullSeq.Should().Be(0);

        var at = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        await _store.SetSyncCursorAsync(at, 42);

        var cursor = await _store.GetSyncCursorAsync();
        cursor.LastPushedAt.Should().Be(at);
        cursor.LastPullSeq.Should().Be(42);

        // 双列独立清空（只 pull 不 push 的轮次）
        await _store.SetSyncCursorAsync(null, null);
        var cleared = await _store.GetSyncCursorAsync();
        cleared.LastPushedAt.Should().BeNull();
        cleared.LastPullSeq.Should().Be(0);
    }

    [Fact]
    public async Task LocalDeviceName_RoundTrips()
    {
        (await _store.GetLocalDeviceNameAsync()).Should().Be(Environment.MachineName, "迁移时抓一次机器名，之后不随 hostname 漂移（A7）");

        await _store.SetLocalDeviceNameAsync("我的笔记本");
        (await _store.GetLocalDeviceNameAsync()).Should().Be("我的笔记本");
    }
}
