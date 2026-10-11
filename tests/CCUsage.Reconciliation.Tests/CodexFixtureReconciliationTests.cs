using CCUsage.Core.Models;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（Codex 侧）：仓库内 Codex fixture 上，自研链路
/// （CodexJsonlAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与
/// golden（ccusage 20.0.29 codex daily --json --offline，录制命令见 fixtures/golden/README.md）
/// 在容差内一致。CLI 的 codex per-model 输出无成本，成本只在每日/全期总计口径比较（比较器跳过 null 成本）。
/// </summary>
public class CodexFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadCodex(FixturePaths.CodexGoldenPath);
        var ours = await UsageChainAggregator.AggregateCodexAsync(FixturePaths.CodexRoot);

        // 链路健康检查：fixture 全部行合法；重复的 cumulative 快照被守卫跳过（不产生记录）。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.RecordCount.Should().Be(5);
        ours.UnpricedModels.Should().BeEmpty("fixture 覆盖的模型都应被内置快照命中");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_cumulative_snapshot_guard_and_multi_model_days()
    {
        // 2026-09-14 会话连发两条相同 cumulative 快照：只产出一条记录，
        // CLI（golden input=16000）与自研链路一致跳过重复快照。
        var golden = CcusageGoldenFiles.LoadCodex(FixturePaths.CodexGoldenPath);
        var ours = await UsageChainAggregator.AggregateCodexAsync(FixturePaths.CodexRoot);

        golden.ModelEntries.Select(entry => entry.Model)
            .Should().BeEquivalentTo("gpt-5.3-codex", "gpt-5.1-codex-max", "gpt-5-codex");
        golden.ModelEntries.Should().OnlyContain(entry => entry.CostUsd == null, "CLI codex per-model 输出无成本");

        var day = ours.TotalEntries.Single(entry => entry.Date == "2026-09-14");
        day.InputTokens.Should().Be(16000);
        day.CacheReadTokens.Should().Be(4000);
        day.OutputTokens.Should().Be(1000);
        day.CostUsd.Should().BeApproximately(0.0427m, 0.0000001m);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.CodexGoldenPath)})";

    private static string OursLabel() => "自研链路(CodexJsonlAdapter+PricingEngine)";
}
