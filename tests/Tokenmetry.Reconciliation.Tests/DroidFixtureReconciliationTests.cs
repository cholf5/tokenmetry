using Tokenmetry.Core.Models;
using Tokenmetry.Reconciliation.Tests.CCUsageCli;
using Tokenmetry.Reconciliation.Tests.Comparison;
using Tokenmetry.Reconciliation.Tests.Support;

namespace Tokenmetry.Reconciliation.Tests;

/// <summary>
/// TC-001（Droid 侧，I-015）：仓库内 Droid fixture（合成 *.settings.json）上，自研链路
/// （DroidAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 droid daily --json --offline，DROID_SESSIONS_DIR 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。droid daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：settings.model 归一化（剥 [..] 段）、侧车 .jsonl "Model:" 提取回退、
/// archive 旧快照 latest-wins 去重、全零 tokenUsage 跳过、非 settings 的 .json 不发现。
/// fixture 不含 thinkingTokens&gt;0 行（CLI outputTokens 不含 extra，见卡片偏差②），该语义由单测覆盖。
/// </summary>
public class DroidFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadDroid(FixturePaths.DroidGoldenPath);
        var ours = await UsageChainAggregator.AggregateDroidAsync(FixturePaths.DroidRoot);

        // 链路健康检查：快照文件与字段均合法；跳过行 = 全零 tokenUsage 快照。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.Diagnostics.LinesSkipped.Should().Be(1, "zero.settings.json 五桶全零跳过");
        ours.Diagnostics.DuplicateRecords.Should().Be(1, "archive/session-c 旧快照被 latest-wins 去重");
        ours.RecordCount.Should().Be(3, "session-a（sonnet-4）+ session-b（侧车 opus-4-5-thinking）+ session-c（latest gpt-5）");
        ours.UnpricedModels.Should().BeEmpty("claude-sonnet-4 / gpt-5 / claude-opus-4-5-thinking 均被内置快照覆盖");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_model_chain_and_dedup()
    {
        var ours = await UsageChainAggregator.AggregateDroidAsync(FixturePaths.DroidRoot);

        // 05-01：session-a（claude-sonnet-4 100/50/20/10，[Anthropic] 段剥离归一化）；
        // archive 旧 session-c（05-01，10/20）被去重不计。
        var first = ours.TotalEntries.Single(entry => entry.Date == "2026-05-01");
        first.InputTokens.Should().Be(100);
        first.OutputTokens.Should().Be(50);
        first.CacheCreationTokens.Should().Be(20);
        first.CacheReadTokens.Should().Be(10);
        first.TotalTokens.Should().Be(180);

        // 05-02：session-b（无 model → 侧车 Model: 提取 → claude-opus-4-5-thinking 10/20）+
        // session-c latest（gpt-5 100/200）。
        var second = ours.TotalEntries.Single(entry => entry.Date == "2026-05-02");
        second.InputTokens.Should().Be(110);
        second.OutputTokens.Should().Be(220);
        second.TotalTokens.Should().Be(330);
        ours.ModelEntries.Single(entry => entry.Model == "claude-opus-4-5-thinking").InputTokens.Should().Be(10);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.DroidGoldenPath)})";

    private static string OursLabel() => "自研链路(DroidAdapter+PricingEngine)";
}
