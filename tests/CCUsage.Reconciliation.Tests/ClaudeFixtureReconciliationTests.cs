using CCUsage.Core.Models;
using CCUsage.Infrastructure.ClaudeCodeJsonl;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（Claude 侧）：仓库内 Claude fixture 上，自研链路
/// （ClaudeCodeJsonlAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与
/// golden（ccusage 20.0.29 claude daily --json --offline，录制命令见 fixtures/golden/README.md）
/// 在容差内一致。测试不调用 CLI，离线确定性。
/// </summary>
public class ClaudeFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadClaude(FixturePaths.ClaudeGoldenPath);
        var ours = await UsageChainAggregator.AggregateClaudeAsync(FixturePaths.ClaudeRoot);

        // 链路健康检查：fixture 全部行合法、重复行被消息级去重折叠。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.Diagnostics.DuplicateRecords.Should().Be(1);
        ours.RecordCount.Should().Be(9);
        ours.UnpricedModels.Should().BeEmpty("fixture 覆盖的模型都应被内置快照命中");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        // 失败时报告全文进断言消息：差异按 日期 × 模型 × 字段 列出并标注解析/定价原因。
        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Golden_fixture_covers_both_cost_paths_and_multi_model_days()
    {
        // golden 结构防回归：若 CLI 输出形状或 fixture 漂移，先在这里显式失败而不是静默通过。
        var golden = CcusageGoldenFiles.LoadClaude(FixturePaths.ClaudeGoldenPath);

        golden.ModelEntries.Select(entry => entry.Date).Distinct()
            .Should().BeEquivalentTo(["2026-09-14", "2026-09-15"]);
        golden.ModelEntries.Select(entry => entry.Model)
            .Should().BeEquivalentTo(
                "claude-sonnet-4-5-20250929",
                "claude-opus-4-20250514",
                "claude-haiku-4-5",
                "claude-sonnet-4-5-20250929-fast",
                "claude-sonnet-4-5-20250929");
        // 2026-09-14：sonnet/opus 走日志内建 costUSD，haiku 无 costUSD 走 CLI 计算价。
        golden.ModelEntries.Single(entry => entry.Model == "claude-haiku-4-5").CostUsd.Should().Be(0.011m);
        golden.TotalEntries.Single(entry => entry.Date == GoldenReport.GrandTotalDate).TotalTokens
            .Should().Be(229743);
    }

    [Fact]
    public async Task Our_chain_counts_synthetic_records_only_in_daily_totals()
    {
        // &lt;synthetic&gt; 记录（model 归一为 null）：CLI 计入每日总计但不给 per-model breakdown；
        // 自研链路口径相同（无模型记录不入 per-model 条目），两总计口径由此对上。
        var ours = await UsageChainAggregator.AggregateClaudeAsync(FixturePaths.ClaudeRoot);

        ours.ModelEntries.Select(entry => entry.Model).Should().OnlyContain(model => model != null);
        ours.Diagnostics.UnknownModelRecords.Should().Be(1);
        ours.ModelEntries.Should().HaveCount(5); // 09-14: sonnet/opus/haiku；09-15: sonnet-fast/sonnet
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.ClaudeGoldenPath)})";

    private static string OursLabel() => "自研链路(ClaudeCodeJsonlAdapter+PricingEngine)";
}
