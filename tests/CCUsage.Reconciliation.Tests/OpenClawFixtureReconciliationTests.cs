using CCUsage.Core.Models;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（OpenClaw 侧，I-020）：仓库内 OpenClaw fixture（合成 JSONL 会话 + 合成
/// agents/*/agent/openclaw-agent.sqlite）上，自研链路（OpenClawAdapter → PricingEngine 内置快照 →
/// 按 UTC 日聚合）与 golden（ccusage 20.0.29 openclaw daily --json --offline，OPENCLAW_DIR 钉住
/// fixture，录制命令见 fixtures/golden/README.md）在容差内一致。openclaw daily 输出形状与 claude
/// 相同（modelBreakdowns 逐模型带 cost，未定价模型 cost 0 且 missingPricing=true），golden 复用
/// claude DTO。fixture 语义覆盖：model_change（根级）与 model-snapshot（data 块）状态跟踪、
/// usage.cost.total 内建成本 LogCost 直采、totalTokens 兜底（output 补齐分支）、cacheWrite →
/// cache_creation 映射、归档会话文件名（.jsonl.deleted.* / .jsonl.reset.*）与会话 id 推导、
/// 文件内重复行内容键去重、非 message / 非 assistant / 全零行跳过、坏行（损坏 message 块与坏
/// JSON 载荷行）跳过、<b>同一事件的迁移 SQLite 副本优先于 JSONL 遗留副本</b>（成本 0.50 → 0.51
/// 逐位替换）、SQLite-only 会话、agents/empty/agent/not-a-database.sqlite 不发现。
/// fixture 规避：无内建成本的可计价模型行（I-024 后桌面经 OC-1 两级候选链现算裸名价，与 CLI
/// 口径一致——fixture 仍以带内建成本行覆盖之，未定价模型行 mystery-model-x 双侧 0 成本覆盖之）、
/// total 缺口走 extra 分支的行（CLI output 列不含 extra——I-013 偏差②同款）。
/// </summary>
public class OpenClawFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadOpenClaw(FixturePaths.OpenClawGoldenPath);
        var ours = await UsageChainAggregator.AggregateOpenClawAsync(FixturePaths.OpenClawRoot);

        // 链路健康检查：双格式发现、行级归因、文件内去重与迁移副本替换计数。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial, "坏行计 Malformed/Invalid 显式降级");
        ours.Diagnostics.FilesDiscovered.Should().Be(6, "JSONL 4（含 .deleted/.reset 归档）+ agent 库 2（not-a-database.sqlite 不发现）");
        ours.Diagnostics.FilesRead.Should().Be(6);
        ours.Diagnostics.LinesSkipped.Should().Be(4,
            "user 行 + 非 message 型行（JSONL 2）+ SQLite 汇总行与 user 行（2）");
        ours.Diagnostics.MalformedLines.Should().Be(1, "worker 库 not json 载荷行");
        ours.Diagnostics.InvalidRecords.Should().Be(1, "session.jsonl 损坏 message 块行");
        ours.Diagnostics.DuplicateRecords.Should().Be(2, "abc.jsonl 重复行折叠 + 迁移副本替换（SQLite 胜出）");
        ours.RecordCount.Should().Be(9, "01-30 两行 + 02-02 四行（迁移副本 1:1 替换后）+ 03-20 三行");
        ours.UnpricedModels.Select(info => info.Model).Should().Equal("[openclaw] mystery-model-x");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_sqlite_priority_and_token_semantics()
    {
        var ours = await UsageChainAggregator.AggregateOpenClawAsync(FixturePaths.OpenClawRoot);

        // 01-30：model_change 状态模型 + 重复行折叠 + total 兜底（output 补齐 50）。
        var first = ours.TotalEntries.Single(entry => entry.Date == "2026-01-30");
        first.InputTokens.Should().Be(1760);
        first.OutputTokens.Should().Be(105, "55（gpt-5.2）+ 50（total 缺口补 output）");
        first.CacheReadTokens.Should().Be(108928);
        first.CostUsd.Should().Be(0.03m, "内建成本 LogCost 直采（0.02 + 0.01）");

        // 02-02：迁移副本被 SQLite 行替换（0.50 → 0.51）+ data 块 model-snapshot 状态 + SQLite-only 会话。
        var second = ours.TotalEntries.Single(entry => entry.Date == "2026-02-02");
        second.InputTokens.Should().Be(1000517);
        second.OutputTokens.Should().Be(273);
        second.CacheReadTokens.Should().Be(100);
        second.CostUsd.Should().Be(2.75m, "0.51（SQLite 胜出）+ 0.44 + 1.80 + 0（mystery 未定价）");

        // 03-20：归档会话文件（.deleted/.reset）+ cacheWrite → cache_creation + 第二个 agent 库。
        var third = ours.TotalEntries.Single(entry => entry.Date == "2026-03-20");
        third.InputTokens.Should().Be(54);
        third.CacheCreationTokens.Should().Be(5);
        third.CostUsd.Should().Be(0.90m);

        // 全期总计与 golden totals 逐位一致。
        var grand = ours.TotalEntries.Single(entry => entry.Date == "(total)");
        grand.InputTokens.Should().Be(1002331);
        grand.OutputTokens.Should().Be(457);
        grand.CacheCreationTokens.Should().Be(5);
        grand.CacheReadTokens.Should().Be(109028);
        grand.TotalTokens.Should().Be(1111821);

        ours.ModelEntries.Where(entry => entry.Model == "[openclaw] gpt-5.2")
            .Sum(entry => entry.CostUsd).Should().Be(0.83m, "0.02 + 0.51（SQLite 行）+ 0.10 + 0.20");
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.OpenClawGoldenPath)})";

    private static string OursLabel() => "自研链路(OpenClawAdapter+PricingEngine)";
}
