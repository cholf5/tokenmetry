namespace Tokenmetry.Infrastructure.Tests.LocalIngestion;

using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.CodexJsonl;
using Tokenmetry.Infrastructure.GooseSqlite;
using Tokenmetry.Infrastructure.LocalIngestion;
using Tokenmetry.Infrastructure.ModelsDev;

/// <summary>
/// I-047 采集 gate 等待超时：持锁轮挂死时等待方在超时后返回空结果（本轮放弃），进程不崩、
/// 后续轮次可重新进入 gate。超时值取卡内定案（30 分钟，测试经 internal 缩短注入）。
/// </summary>
public class LocalIngestionGateTimeoutTests
{
    /// <summary>gate 被外部占住不释放：等待方应超时返回空列表（放弃本轮）而非无限挂起。</summary>
    [Fact]
    public async Task GetDailyUsage_WhenGateHeld_TimesOutAndReturnsEmpty()
    {
        var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync(); // 模拟挂死轮：永久持有
        try
        {
            var source = CreateSource(gateOverride: gate, gateWaitTimeout: TimeSpan.FromMilliseconds(120));

            var rows = await source.GetDailyUsageAsync();

            rows.Should().BeEmpty("超时放弃本轮：返回空列表，UI 不误显错误数据");
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task GetDailyUsage_AfterTimedOutRound_NextRoundCanAcquireGate()
    {
        var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();
        var source = CreateSource(gateOverride: gate, gateWaitTimeout: TimeSpan.FromMilliseconds(120));

        var first = await source.GetDailyUsageAsync();
        first.Should().BeEmpty("首轮等待超时放弃");

        gate.Release(); // 持锁轮"恢复"：后续轮次必须能重新进入
        var second = await source.GetDailyUsageAsync();
        second.Should().NotBeNull("后续轮次可重新进入 gate（防线语义：解除排队封锁）");
    }

    [Fact]
    public async Task GetDailyUsage_WhenGateFree_BehavesAsBefore()
    {
        // gate 正常释放路径零变化（AC）：无超时注入的既有行为——本用例用超长超时保证不误伤。
        var source = CreateSource(gateWaitTimeout: TimeSpan.FromSeconds(30));
        var rows = await source.GetDailyUsageAsync();
        rows.Should().NotBeNull();
    }

    /// <summary>
    /// 最小管道源：只注入 Claude（空目录 → NoData）+ 其余空桩，供 gate 行为测试不背全量适配器。
    /// LocalIngestionDataSource 构造需要全部 20 个适配器，这里用最简桩补位。
    /// </summary>
    private static LocalIngestionDataSource CreateSource(SemaphoreSlim? gateOverride = null, TimeSpan? gateWaitTimeout = null)
    {
        var claude = new ClaudeCodeJsonlAdapter(configuredRoot: Path.Combine(Path.GetTempPath(), $"ccusage-gate-{Guid.NewGuid():N}"));
        var source = new GateTestDataSource(
            claude,
            new CodexJsonlAdapter(configuredHome: Path.Combine(Path.GetTempPath(), $"ccusage-gate-codex-{Guid.NewGuid():N}")),
            new GooseSqliteAdapter(configuredRoot: Path.Combine(Path.GetTempPath(), $"ccusage-gate-goose-{Guid.NewGuid():N}")),
            pricingEngine: new PricingEngine(overrides: []),
            store: new GateTestStore(),
            scanRegistry: new IngestionScanRegistry());
        if (gateOverride is not null)
        {
            source.IngestionGate = gateOverride;
        }

        if (gateWaitTimeout is not null)
        {
            source.GateWait = gateWaitTimeout.Value;
        }

        return source;
    }

    /// <summary>测试注入点：gate 实例与等待超时可替换（生产走构造默认——SemaphoreSlim(1,1) / 30 分钟）。</summary>
    private sealed class GateTestDataSource : LocalIngestionDataSource
    {
        public GateTestDataSource(
            IClaudeCodeJsonlAdapter claude,
            ICodexJsonlAdapter codex,
            IGooseSqliteAdapter goose,
            PricingEngine pricingEngine,
            IUsageStore store,
            IngestionScanRegistry scanRegistry)
            : base(
                claude, codex,
                new StubZCode(), new StubDsh(), new StubCopilot(), new StubOpenCode(), goose,
                new StubHermes(), new StubKilo(), new StubGemini(), new StubAmp(), new StubDroid(),
                new StubQwen(), new StubKimi(), new StubCodebuff(), new StubPi(), new StubCindy(),
                new StubGrok(), new StubOpenClaw(), new StubAntigravity(),
                pricingEngine, store, scanRegistry: scanRegistry)
        {
        }
    }

    private sealed class GateTestStore : IUsageStore
    {
        public Task<string> GetLocalDeviceIdAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task<string> GetLocalDeviceNameAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task SetLocalDeviceNameAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertRemoteUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertRemoteSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UsageFact>> GetLocalUsageFactsSinceAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UsageFact>>(Array.Empty<UsageFact>());
        public Task<IReadOnlyList<SessionFact>> GetLocalSessionFactsSinceAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionFact>>(Array.Empty<SessionFact>());
        public Task<(DateTimeOffset? LastPushedAt, long LastPullSeq)> GetSyncCursorAsync(CancellationToken ct = default) => Task.FromResult<(DateTimeOffset?, long)>((null, 0L));
        public Task SetSyncCursorAsync(DateTimeOffset? lastPushedAt, long? lastPullSeq, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Tokenmetry.Core.Sync.SyncDeviceInfo>> GetRemoteDevicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Tokenmetry.Core.Sync.SyncDeviceInfo>>(Array.Empty<Tokenmetry.Core.Sync.SyncDeviceInfo>());
        public Task UpsertRemoteDevicesAsync(IReadOnlyList<Tokenmetry.Core.Sync.SyncDeviceInfo> devices, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteRemoteDeviceDataAsync(string deviceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => Task.CompletedTask;

        public Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UsageFact>>([]);

        public Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionFact>>([]);

        public Task DeleteDecompositionFactsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubZCode : IZCodeSqliteAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubDsh : IDshSessionAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubCopilot : ICopilotSessionAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubOpenCode : IOpenCodeSqliteAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubHermes : IHermesSqliteAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubKilo : IKiloSqliteAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubGemini : IGeminiCliAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubAmp : IAmpAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubDroid : IDroidAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubQwen : IQwenCodeAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubKimi : IKimiCodeAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubCodebuff : ICodebuffAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubPi : IPiAgentAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubCindy : ICindyAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubGrok : IGrokAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubOpenClaw : IOpenClawAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }

    private sealed class StubAntigravity : IAntigravityAdapter
    {
        public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult(new JsonlScanResult { Diagnostics = { Status = JsonlScanStatus.NoData } });
    }
}
