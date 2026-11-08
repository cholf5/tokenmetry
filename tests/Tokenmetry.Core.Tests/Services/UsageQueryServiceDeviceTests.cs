namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

/// <summary>
/// 查询面设备维度（relay 计划 Task 3 / 设计 §4.3 地雷 1、§6 不变式）：
/// grain 兜底按 (device, day) 收窄——扫描设备的 full 行不吞并 CLI 设备同日的分解行；
/// Device 过滤贯通会话与三维聚合；两设备合并 = 各自之和（成本 decimal 精确）。
/// </summary>
public sealed class UsageQueryServiceDeviceTests
{
    [Fact]
    public async Task UnionDay_FullFromOneDevice_DecompositionFromOther_NoDoubleCount_NoLoss()
    {
        var store = new FakeStore();
        store.Facts.Add(new UsageFact
        {
            Device = "devA", Day = "2026-06-19", Grain = UsageFactGrain.Full,
            Agent = "cc", Model = "m", Project = "/p",
            TotalTokens = 100, CostUsd = 1.00m,
        });
        store.Facts.Add(new UsageFact
        {
            Device = "devB", Day = "2026-06-19", Grain = UsageFactGrain.Day,
            TotalTokens = 50, CostUsd = 0.50m,
        });
        var query = new UsageQueryService(store);

        var daily = await query.GetDailySeriesAsync(new UsageQueryFilter());
        var point = daily.Single(row => row.Day == "2026-06-19");
        point.TotalTokens.Should().Be(150, "各自粒度兜底互不吞并：full 行与分解行都计入");
        point.Cost.KnownCostUsd.Should().Be(1.50m);

        var kpi = await query.GetKpiAsync(new UsageQueryFilter());
        kpi.TotalTokens.Should().Be(150);
        kpi.Cost.KnownCostUsd.Should().Be(1.50m);

        var kpiA = await query.GetKpiAsync(new UsageQueryFilter { Device = "devA" });
        kpiA.TotalTokens.Should().Be(100);
        kpiA.Cost.KnownCostUsd.Should().Be(1.00m);
    }

    [Fact]
    public async Task DeviceFilter_AppliesToSessionsAndDimensions()
    {
        var store = new FakeStore();
        store.Facts.Add(new UsageFact
        {
            Device = "devA", Day = "2026-06-19", Grain = UsageFactGrain.Agent,
            Agent = "Claude Code", TotalTokens = 100, CostUsd = 1.00m,
        });
        store.Facts.Add(new UsageFact
        {
            Device = "devB", Day = "2026-06-19", Grain = UsageFactGrain.Agent,
            Agent = "Codex", TotalTokens = 200, CostUsd = 2.00m,
        });
        var seen = new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);
        store.Sessions.Add(new SessionFact
        {
            Device = "devA", SessionId = "s-a", Agent = "Claude Code",
            FirstSeenAt = seen, LastSeenAt = seen, TotalTokens = 100,
        });
        store.Sessions.Add(new SessionFact
        {
            Device = "devB", SessionId = "s-b", Agent = "Codex",
            FirstSeenAt = seen, LastSeenAt = seen, TotalTokens = 200,
        });
        var query = new UsageQueryService(store);
        var filterB = new UsageQueryFilter { Device = "devB" };

        var sessions = await query.GetSessionsAsync(new SessionListQuery { Filter = filterB });
        sessions.Rows.Select(row => row.SessionId).Should().Equal("s-b");

        var agents = await query.GetAgentBreakdownAsync(filterB);
        agents.Select(row => row.Key).Should().Equal("Codex");
        agents.Single().TotalTokens.Should().Be(200);
    }

    [Fact]
    public async Task TwoDeviceUnion_SumEqualsPerDeviceSums()
    {
        var store = new FakeStore();
        store.Facts.Add(new UsageFact
        {
            Device = "devA", Day = "2026-06-19", Grain = UsageFactGrain.Full,
            Agent = "cc", Model = "m1", Project = "/p1",
            InputTokens = 70, OutputTokens = 30, CacheReadTokens = 20, TotalTokens = 120, CostUsd = 1.2345m,
        });
        store.Facts.Add(new UsageFact
        {
            Device = "devA", Day = "2026-06-20", Grain = UsageFactGrain.Day,
            TotalTokens = 80, CostUsd = 0.1111m,
        });
        store.Facts.Add(new UsageFact
        {
            Device = "devB", Day = "2026-06-19", Grain = UsageFactGrain.Day,
            TotalTokens = 200, CostUsd = 2.5000m,
        });
        store.Facts.Add(new UsageFact
        {
            Device = "devB", Day = "2026-06-22", Grain = UsageFactGrain.Full,
            Model = "m2", Project = "/p2",
            TotalTokens = 40, CostUsd = 0.6666m,
        });
        var query = new UsageQueryService(store);

        var union = await query.GetKpiAsync(new UsageQueryFilter());
        var a = await query.GetKpiAsync(new UsageQueryFilter { Device = "devA" });
        var b = await query.GetKpiAsync(new UsageQueryFilter { Device = "devB" });

        union.TotalTokens.Should().Be(a.TotalTokens + b.TotalTokens, "对账不变式（设计 §6）：tokens 精确");
        union.Cost.KnownCostUsd.Should().Be(a.Cost.KnownCostUsd + b.Cost.KnownCostUsd, "成本 decimal 精确");
        union.Cost.KnownCostUsd.Should().Be(4.5122m);
    }

    /// <summary>查询面内存桩：只实现 GetUsageFactsAsync / GetSessionFactsAsync（查询服务只读两表）。</summary>
    private sealed class FakeStore : IUsageStore
    {
        public List<UsageFact> Facts { get; } = new();

        public List<SessionFact> Sessions { get; } = new();

        public Task<string> GetLocalDeviceIdAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UsageFact>>(Facts.ToList());

        public Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionFact>>(Sessions.ToList());

        public Task DeleteDecompositionFactsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> GetLocalDeviceNameAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task SetLocalDeviceNameAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertRemoteUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertRemoteSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<UsageFact>> GetLocalUsageFactsSinceAsync(DateTimeOffset since, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionFact>> GetLocalSessionFactsSinceAsync(DateTimeOffset since, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<(DateTimeOffset? LastPushedAt, long LastPullSeq)> GetSyncCursorAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetSyncCursorAsync(DateTimeOffset? lastPushedAt, long? lastPullSeq, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<Tokenmetry.Core.Sync.SyncDeviceInfo>> GetRemoteDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertRemoteDevicesAsync(IReadOnlyList<Tokenmetry.Core.Sync.SyncDeviceInfo> devices, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteRemoteDeviceDataAsync(string deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
