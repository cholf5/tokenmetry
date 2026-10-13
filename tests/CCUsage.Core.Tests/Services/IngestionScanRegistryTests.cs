using System;
using System.Linq;
using System.Threading.Tasks;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

namespace CCUsage.Core.Tests.Services;

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
}
