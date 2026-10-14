using TokenPulse.Core.Models;
using TokenPulse.Reconciliation.Tests.CCUsageCli;
using TokenPulse.Reconciliation.Tests.Comparison;
using TokenPulse.Reconciliation.Tests.Support;

namespace TokenPulse.Reconciliation.Tests;

/// <summary>
/// TC-001（Kilo 侧，I-017）：仓库内 Kilo fixture（合成 kilo.db）上，自研链路
/// （KiloSqliteAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 kilo daily --json --offline，KILO_DATA_DIR 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。kilo daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：assistant 行过滤、五桶 tokens 映射、载荷 cost 直采（含 0——蓝本 Auto 对
/// Some(0.0) 记 0 不再计价，claude-sonnet-4-5 行 cost=0）、stored cost 0.005 LogCost、非 assistant 行
/// 跳过、坏 JSON 载荷跳过。fixture 不含 reasoning/total 余量行（CLI outputTokens 不含 extra，见卡片
/// 偏差②），该语义由单测覆盖。
/// </summary>
public class KiloFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadKilo(FixturePaths.KiloGoldenPath);
        var ours = await UsageChainAggregator.AggregateKiloAsync(FixturePaths.KiloRoot);

        // 链路健康检查：坏 JSON 载荷行计 malformed 不中断；user 行按 skipped 口径计数。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial, "fixture 含 1 行坏 JSON 载荷（仓库口径计 malformed）");
        ours.Diagnostics.MalformedLines.Should().Be(1);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.Diagnostics.LinesSkipped.Should().Be(1, "user 行按 skipped 计");
        ours.RecordCount.Should().Be(3, "msg-a/msg-b/msg-c 保留，user 行与坏 JSON 行剔除");
        ours.UnpricedModels.Should().BeEmpty("模型名均能被内置快照或 LogCost 短路覆盖");
        ours.ZeroCostRecordCount.Should().Be(2, "msg-a/msg-c 的 stored cost 0 按蓝本 Auto 记 0 不计价");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_auto_cost_semantics_and_filters()
    {
        var ours = await UsageChainAggregator.AggregateKiloAsync(FixturePaths.KiloRoot);

        // 09-14：msg-a（claude-sonnet-4-5 1000/200/100/300，stored cost 0 → 0）+ msg-c（同模型 100/10，
        // stored cost 0.0 → 0）+ msg-b（gpt-5.1 500/50，stored 0.005 直采）。
        var day = ours.TotalEntries.Single(entry => entry.Date == "2026-09-14");
        day.InputTokens.Should().Be(1600);
        day.OutputTokens.Should().Be(260);
        day.CacheCreationTokens.Should().Be(100);
        day.CacheReadTokens.Should().Be(300);
        day.TotalTokens.Should().Be(2260);
        day.CostUsd.Should().Be(0.005m, "stored cost 0 按蓝本 Auto 记 0 不计价（与 OpenCode 的 >0 过滤不同）");

        var claude = ours.ModelEntries.Single(entry => entry.Model == "claude-sonnet-4-5");
        claude.CostUsd.Should().Be(0m);
        claude.TotalTokens.Should().Be(1710);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.KiloGoldenPath)})";

    private static string OursLabel() => "自研链路(KiloSqliteAdapter+PricingEngine)";
}
