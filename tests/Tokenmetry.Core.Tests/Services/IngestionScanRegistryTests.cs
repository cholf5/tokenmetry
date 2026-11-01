using System;
using System.Linq;
using System.Threading.Tasks;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.Core.Tests.Services;

/// <summary>
/// S-002 采集扫描状态注册表：注册序保持、快照最新生效、未扫描返回 null（UI 诚实降级"尚未扫描"）。
/// </summary>
public class IngestionScanRegistryTests
{
    [Fact]
    public void RegisterSource_PreservesRegistrationOrderAndIsIdempotent()
    {
        var registry = new IngestionScanRegistry();

        registry.RegisterSource("Claude Code");
        registry.RegisterSource("Codex");
        registry.RegisterSource("Claude Code"); // 重复注册幂等，不改变注册序

        registry.RegisteredSources.Should().Equal("Claude Code", "Codex");
    }

    [Fact]
    public void GetLastScan_ReturnsNullBeforeFirstScan()
    {
        var registry = new IngestionScanRegistry();
        registry.RegisterSource("Codex");

        registry.GetLastScan("Codex").Should().BeNull();
        registry.GetLastScan("Unknown").Should().BeNull();
    }

    [Fact]
    public void ReportScan_StoresLatestSnapshotPerSource()
    {
        var registry = new IngestionScanRegistry();
        var first = new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 10, 13, 11, 0, 0, TimeSpan.Zero);

        registry.RegisterSource("Claude Code");
        registry.ReportScan("Claude Code", JsonlScanStatus.Ok, 100, first);
        registry.ReportScan("Codex", JsonlScanStatus.Partial, 5, first);
        registry.ReportScan("Claude Code", JsonlScanStatus.Ok, 120, second); // 最新扫描覆盖

        registry.GetLastScan("Claude Code").Should().Be(
            new IngestionSourceScanSnapshot("Claude Code", JsonlScanStatus.Ok, 120, second));
        registry.GetLastScan("Codex").Should().Be(
            new IngestionSourceScanSnapshot("Codex", JsonlScanStatus.Partial, 5, first));
    }

    [Fact]
    public async Task RegisteredSources_IsSafeUnderConcurrentReadWrite()
    {
        // 写入只发生在采集管道内（串行），读取来自 UI 线程——并发往返不抛异常且注册序稳定
        var registry = new IngestionScanRegistry();
        var names = Enumerable.Range(0, 32).Select(i => $"source-{i}").ToArray();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = w; i < names.Length; i += 8)
            {
                registry.RegisterSource(names[i]);
                registry.ReportScan(names[i], JsonlScanStatus.Ok, i, DateTimeOffset.Now);
            }
        })));

        registry.RegisteredSources.Should().HaveCount(names.Length);
        foreach (var name in names)
        {
            registry.GetLastScan(name).Should().NotBeNull();
        }
    }

    // ── I-046 源内进行中快照 ──

    [Fact]
    public void BeginScan_ClearsLastResultAndEntersInProgressState()
    {
        var registry = new IngestionScanRegistry();
        registry.RegisterSource("Claude Code");
        registry.ReportScan("Claude Code", JsonlScanStatus.Ok, 120, DateTimeOffset.Now);

        registry.BeginScan("Claude Code");

        // 进行中：上一轮结果快照不再顶替本轮（UI 显示进行中文案而非旧结果）
        registry.GetLastScan("Claude Code").Should().BeNull();
        registry.GetScanProgress("Claude Code").Should().NotBeNull();
        registry.GetScanProgress("Claude Code")!.FilesProcessed.Should().Be(0);
        registry.GetScanProgress("Claude Code")!.FilesDiscovered.Should().Be(0);
    }

    [Fact]
    public void ReportScanProgress_UpdatesProcessedCount_KeepingStartedAt()
    {
        var registry = new IngestionScanRegistry();
        registry.BeginScan("Codex");
        var startedAt = registry.GetScanProgress("Codex")!.StartedAt;

        registry.ReportScanProgress("Codex", 3200, 16403);

        var progress = registry.GetScanProgress("Codex")!;
        progress.FilesProcessed.Should().Be(3200);
        progress.FilesDiscovered.Should().Be(16403);
        progress.StartedAt.Should().Be(startedAt, "开始时刻在源扫描期间保持不变");
    }

    [Fact]
    public void ReportScan_OverwritesInProgressState()
    {
        // 时序边界（卡内开放问题）：一轮结束必须覆盖掉最后的中间态，不残留进行中快照
        var registry = new IngestionScanRegistry();
        registry.BeginScan("Codex");
        registry.ReportScanProgress("Codex", 500, 1000);

        registry.ReportScan("Codex", JsonlScanStatus.Ok, 42, DateTimeOffset.Now);

        registry.GetScanProgress("Codex").Should().BeNull("完成登记覆盖进行中态");
        registry.GetLastScan("Codex")!.RecordCount.Should().Be(42);
    }

    [Fact]
    public void ReportScanProgress_BeforeBeginScan_IsIgnored()
    {
        // 防御乱序：未 BeginScan 的源不凭空创建进行中态
        var registry = new IngestionScanRegistry();
        registry.RegisterSource("ZCode");

        registry.ReportScanProgress("ZCode", 1, 2);

        registry.GetScanProgress("ZCode").Should().BeNull();
    }

    [Fact]
    public void GetScanProgress_ReturnsNullForUnknownOrIdleSource()
    {
        var registry = new IngestionScanRegistry();

        registry.GetScanProgress("Unknown").Should().BeNull();
        registry.RegisterSource("Idle");
        registry.GetScanProgress("Idle").Should().BeNull();
    }
}
