using CCUsage.Core.Models;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（Goose 侧，I-012）：仓库内 Goose fixture（合成 sessions.db）上，自研链路
/// （GooseSqliteAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 goose daily --json --offline，GOOSE_PATH_ROOT 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。goose daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：accumulated_* 列优先（plain 列为干扰值）、整数秒时间戳形态、19 字符空格分隔时间、
/// provider 缺失按模型前缀归一（gpt-5.1 → openai，仅影响 CLI 计价候选；gpt-5.1 按原名两侧同价）、
/// tokens 全零会话行跳过、model_config 非法 JSON 跳过、created_at 不可解析跳过。
/// </summary>
public class GooseFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadGoose(FixturePaths.GooseGoldenPath);
        var ours = await UsageChainAggregator.AggregateGooseAsync(FixturePaths.GooseRoot);

        // 链路健康检查：坏 config / 坏时间戳行计 invalid 不中断（蓝本静默跳过的行显式计数）；
        // tokens 全零会话行按 skipped 口径计数。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial, "fixture 含坏 config 与坏时间戳行各 1（仓库口径计 invalid）");
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.InvalidRecords.Should().Be(2);
        ours.Diagnostics.LinesSkipped.Should().Be(1);
        ours.RecordCount.Should().Be(2, "session-a/session-b 保留，session-c/d/e 剔除");
        ours.UnpricedModels.Should().BeEmpty("fixture 覆盖的模型都应被内置快照命中");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_accumulated_token_preference_and_pricing()
    {
        var ours = await UsageChainAggregator.AggregateGooseAsync(FixturePaths.GooseRoot);

        // 09-14：session-a（claude-sonnet-4-5 1000/200/0/0，accumulated 列生效，plain 5/6/7 为干扰值），
        // cost = 1000×$3/M + 200×$15/M = 0.006（内置快照价与 CLI --offline 一致）。
        var day14 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-14");
        day14.InputTokens.Should().Be(1000);
        day14.OutputTokens.Should().Be(200);
        day14.CacheCreationTokens.Should().Be(0);
        day14.CacheReadTokens.Should().Be(0);
        day14.TotalTokens.Should().Be(1200);
        day14.CostUsd.Should().Be(0.006m);

        // 09-15：session-b（gpt-5.1 300/30/0/0，整数秒时间戳），cost = 300×$1.25/M + 30×$10/M = 0.000675。
        var day15 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-15");
        day15.InputTokens.Should().Be(300);
        day15.OutputTokens.Should().Be(30);
        day15.TotalTokens.Should().Be(330);
        day15.CostUsd.Should().Be(0.000675m);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.GooseGoldenPath)})";

    private static string OursLabel() => "自研链路(GooseSqliteAdapter+PricingEngine)";
}
