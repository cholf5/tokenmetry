using CCUsage.Core.Models;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（pi 侧，I-019）：仓库内 pi fixture（合成 sessions/**&#47;*.jsonl）上，自研链路
/// （PiAgentAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden（ccusage 20.0.29 pi daily
/// --json --offline，PI_AGENT_DIR 钉住 fixture，录制命令见 fixtures/golden/README.md）在容差内一致。
/// pi daily 输出形状与 claude 相同（modelBreakdowns 逐模型带 cost，模型名带 "[pi] " 前缀），golden
/// 复用 claude DTO。fixture 语义覆盖：重放抑制（子会话相对 parentSession 指向同批父文件、前缀签名
/// 逐位匹配后跳过）、subagent-artifacts 派生转录剔除与 run-* 新上下文子会话保留、会话内建 cost.total
/// 直采（LogCost）与负成本重算（CLI auto 口径，引擎按 "[pi] gpt-5" 剥前缀命中 openai/gpt-5）、
/// 无内建成本按快照现算（含 cacheWrite 的 claude-sonnet 行两侧同价）、坏行 Malformed 显式计数。
/// fixture 不含命名存储（pi.stores[]）——CLI 侧命名存储只在统一 daily 命令出账（会扫本机全部内置
/// agent 默认目录，golden 不可复现），该语义由单测覆盖（卡片偏差④）；也不含 extra&gt;0 行（CLI
/// outputTokens 单列，偏差②），由单测覆盖。
/// </summary>
public class PiFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadPi(FixturePaths.PiGoldenPath);
        var ours = await UsageChainAggregator.AggregatePiAsync(FixturePaths.PiRoot);

        // 链路健康检查：subagent-artifacts 不发现、坏行显式计数、重放前缀抑制。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok,
            "坏 JSON 行不含预过滤标记（计 LinesSkipped），无 Malformed/Invalid 场景");
        ours.Diagnostics.FilesDiscovered.Should().Be(6, "subagent-artifacts 派生转录不发现（含其余 6 个会话文件）");
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.LinesSkipped.Should().Be(5, "4 个 session 头行 + agent_bad 的坏行（无 usage+message 标记）");
        ours.Diagnostics.DuplicateRecords.Should().Be(0);
        ours.RecordCount.Should().Be(8, "root 2 + child 1（重放前缀 1 抑制）+ display 2 + priced 1 + bad 1 + run-*/ 1");
        ours.UnpricedModels.Should().BeEmpty("[pi] 前缀由引擎候选剥离命中快照");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_replay_suppression_and_cost_semantics()
    {
        var ours = await UsageChainAggregator.AggregatePiAsync(FixturePaths.PiRoot);

        // 01-12：child 会话重放前缀（root 的 01-10 行）被抑制，仅保留 01-12 新行。
        var twelfth = ours.TotalEntries.Single(entry => entry.Date == "2026-01-12");
        twelfth.InputTokens.Should().Be(500);
        twelfth.OutputTokens.Should().Be(100);
        twelfth.TotalTokens.Should().Be(600);

        // 01-13：cost.total 0.05 直采 + 负成本 -1.25 重算（1000×1.25/M + 2000×10/M = 0.02125）。
        var thirteenth = ours.TotalEntries.Single(entry => entry.Date == "2026-01-13");
        thirteenth.InputTokens.Should().Be(3000);
        thirteenth.OutputTokens.Should().Be(2400);
        thirteenth.CostUsd.Should().Be(0.07125m);

        // 01-14：无内建成本按快照现算（含 cacheWrite 200×$3.75/M——蓝本 anthropic/claude-sonnet-4
        // 与桌面家族条目同价）。
        var fourteenth = ours.TotalEntries.Single(entry => entry.Date == "2026-01-14");
        fourteenth.CacheCreationTokens.Should().Be(200);
        fourteenth.CostUsd.Should().Be(0.01911m);

        ours.ModelEntries.Where(entry => entry.Model == "[pi] claude-sonnet-4-20250514")
            .Sum(entry => entry.TotalTokens).Should().Be(8090);
        ours.ModelEntries.Where(entry => entry.Model == "[pi] gpt-5")
            .Sum(entry => entry.TotalTokens).Should().Be(6492);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.PiGoldenPath)})";

    private static string OursLabel() => "自研链路(PiAgentAdapter+PricingEngine)";
}
