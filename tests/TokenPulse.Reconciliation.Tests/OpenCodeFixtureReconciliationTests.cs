using TokenPulse.Core.Models;
using TokenPulse.Reconciliation.Tests.CCUsageCli;
using TokenPulse.Reconciliation.Tests.Comparison;
using TokenPulse.Reconciliation.Tests.Support;

namespace TokenPulse.Reconciliation.Tests;

/// <summary>
/// TC-001（OpenCode 侧，I-009）：仓库内 OpenCode fixture（合成 opencode.db）上，自研链路
/// （OpenCodeSqliteAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 opencode daily --json --offline，OPENCODE_DATA_DIR 钉住 fixture，
/// 录制命令见 fixtures/golden/README.md）在容差内一致。opencode daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：v1 message 表与 v2 session_message 表双轮扫描、同 message id 跨表去重（v1 先扫）、
/// fork 拷贝行剔除（session_v2 through 边界）、非 assistant 行与无 tokens 行跳过、坏 JSON 载荷跳过、
/// stored cost 直采（msg-b 0.005）与 stored cost 为 0 走定价（claude-sonnet-4-5 / gpt-5.1 命中内置快照）。
/// </summary>
public class OpenCodeFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadOpenCode(FixturePaths.OpenCodeGoldenPath);
        var ours = await UsageChainAggregator.AggregateOpenCodeAsync(FixturePaths.OpenCodeRoot);

        // 链路健康检查：坏 JSON 载荷行计 malformed 不中断；user 行（v1 无 tokens / v2 非 assistant）
        // 与 fork 拷贝行按 skipped 口径计数；同 id 重复行（v2 的 msg-a）计 dedup。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial, "fixture 含 1 行坏 JSON 载荷（仓库口径计 malformed）");
        ours.Diagnostics.MalformedLines.Should().Be(1);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.Diagnostics.DuplicateRecords.Should().Be(1, "v2 的 msg-a 与 v1 message 表同 id，v1 先扫保留");
        ours.RecordCount.Should().Be(6, "msg-a/msg-b（v1）+ v2-gpt/parent×2/fork-own（v2，拷贝行与 user 行剔除）");
        ours.UnpricedModels.Should().BeEmpty("fixture 覆盖的模型都应被内置快照命中");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_fork_dedup_and_cost_paths()
    {
        var ours = await UsageChainAggregator.AggregateOpenCodeAsync(FixturePaths.OpenCodeRoot);

        // 09-14：v1 msg-a（claude-sonnet-4-5 1000/200/100/300，cost 0 → 引擎计价 0.006465）
        // + msg-b（gpt-5.1 500/50，stored cost 0.005 直采）。
        var day14 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-14");
        day14.InputTokens.Should().Be(1500);
        day14.OutputTokens.Should().Be(250);
        day14.CacheCreationTokens.Should().Be(100);
        day14.CacheReadTokens.Should().Be(300);
        day14.TotalTokens.Should().Be(2150);
        day14.CostUsd.Should().Be(0.011465m);

        // 09-15：v2-gpt（gpt-5.1 300/30/20/40）+ parent 两行（claude 111+222/11+22，stored 0.01+0.02）
        // + fork-own（claude 100/10，cost 0 → 0.00045）；fork 拷贝行（fork-copy-1/2）剔除不出账。
        var day15 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-15");
        day15.InputTokens.Should().Be(733);
        day15.OutputTokens.Should().Be(73);
        day15.CacheCreationTokens.Should().Be(20);
        day15.CacheReadTokens.Should().Be(40);
        day15.TotalTokens.Should().Be(866);
        day15.CostUsd.Should().Be(0.03116125m);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.OpenCodeGoldenPath)})";

    private static string OursLabel() => "自研链路(OpenCodeSqliteAdapter+PricingEngine)";
}
