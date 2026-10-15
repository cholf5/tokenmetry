using Tokenmetry.Core.Models;
using Tokenmetry.Reconciliation.Tests.CCUsageCli;
using Tokenmetry.Reconciliation.Tests.Comparison;
using Tokenmetry.Reconciliation.Tests.Support;

namespace Tokenmetry.Reconciliation.Tests;

/// <summary>
/// TC-001（Hermes 侧，I-016）：仓库内 Hermes fixture（合成 state.db）上，自研链路
/// （HermesSqliteAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 hermes daily --json --offline，HERMES_HOME 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。hermes daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：五桶 tokens 映射（cache_write/read → cacheCreation/cacheRead）、REAL 秒（含小数截断）
/// 与 REAL 毫秒两种 started_at 刻度、billing_provider 缺失按模型前缀推断（claude-sonnet-4-5 → anthropic，
/// 仅影响 CLI 计价候选；claude-sonnet-4-5 按原名两侧同价）、actual_cost_usd 直采（session-b 0.05）、
/// 全零会话行跳过、model 空白行 SQL 排除。
/// 注意：CLI 20.0.29 实测只认 actual_cost_usd（蓝本 HEAD 为 actual.or(estimated) 回退），fixture 不含
/// estimated-only 成本行保证对账逐位一致；该回退行为在自研侧按蓝本实现并由单测覆盖（版本偏差见卡片 Dev Log）。
/// </summary>
public class HermesFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadHermes(FixturePaths.HermesGoldenPath);
        var ours = await UsageChainAggregator.AggregateHermesAsync(FixturePaths.HermesRoot);

        // 链路健康检查：全零会话行按 skipped 计数；无 invalid / malformed；schema 完整无问题。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.Diagnostics.LinesSkipped.Should().Be(1, "session-d 全零（无 tokens 无 cost）按 skipped 计");
        ours.RecordCount.Should().Be(3, "session-a/f/b 保留，session-d 全零行与 session-e 空白 model 行剔除");
        ours.UnpricedModels.Should().BeEmpty("fixture 覆盖的模型都应被内置快照命中");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_scale_detection_and_cost_paths()
    {
        var ours = await UsageChainAggregator.AggregateHermesAsync(FixturePaths.HermesRoot);

        // 09-14：session-a（claude-sonnet-4-5 1200/300/20/50，REAL 秒含小数 → 毫秒截断）+
        // session-f（同模型 30/3，provider 缺失前缀推断），cost = 0.00819 + 0.000135 = 0.008325。
        var day14 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-14");
        day14.InputTokens.Should().Be(1230);
        day14.OutputTokens.Should().Be(303);
        day14.CacheCreationTokens.Should().Be(20);
        day14.CacheReadTokens.Should().Be(50);
        day14.TotalTokens.Should().Be(1603);
        day14.CostUsd.Should().Be(0.008325m);

        // 09-15：session-b（gpt-5.1 400/40，REAL 毫秒，actual cost 0.05 直采）。
        var day15 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-15");
        day15.InputTokens.Should().Be(400);
        day15.OutputTokens.Should().Be(40);
        day15.TotalTokens.Should().Be(440);
        day15.CostUsd.Should().Be(0.05m);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.HermesGoldenPath)})";

    private static string OursLabel() => "自研链路(HermesSqliteAdapter+PricingEngine)";
}
