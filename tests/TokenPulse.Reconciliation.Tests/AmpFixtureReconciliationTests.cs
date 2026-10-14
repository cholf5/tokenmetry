using TokenPulse.Core.Models;
using TokenPulse.Reconciliation.Tests.CCUsageCli;
using TokenPulse.Reconciliation.Tests.Comparison;
using TokenPulse.Reconciliation.Tests.Support;

namespace TokenPulse.Reconciliation.Tests;

/// <summary>
/// TC-001（Amp 侧，I-014）：仓库内 Amp fixture（合成 threads/T-*.json）上，自研链路
/// （AmpAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 amp daily --json --offline，AMP_DATA_DIR 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。amp daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：usageLedger.events 优先（含 toMessageId 缓存四桶 attach、total 兜底补 output、
/// 缺 model 事件跳过）、messages 回退路径（usage 级 timestamp/model 缺省回退消息级、totalTokens 兜底）、
/// user 行跳过。fixture 不含 reasoning/extra 行（CLI outputTokens 不含 extra，见卡片偏差②），
/// 该语义由单测覆盖。
/// </summary>
public class AmpFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadAmp(FixturePaths.AmpGoldenPath);
        var ours = await UsageChainAggregator.AggregateAmpAsync(FixturePaths.AmpRoot);

        // 链路健康检查：文件与行均合法；跳过行 = user 消息 + 缺 model 的账本事件。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.Diagnostics.LinesSkipped.Should().Be(2, "T-thread-a 的 user 行 + 缺 model 的 event-d");
        ours.RecordCount.Should().Be(6, "账本 3 条（event-d 跳过）+ messages 3 条");
        ours.UnpricedModels.Should().BeEmpty("gpt-5 / claude-haiku-4-5-20251001 均能被内置快照覆盖");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_ledger_precedence_and_total_fallback()
    {
        var ours = await UsageChainAggregator.AggregateAmpAsync(FixturePaths.AmpRoot);

        // 01-19：event-a（gpt-5 1000/200）+ event-b（gpt-5 800/100，toMessageId=11 attach 986/11372）+
        // T-thread-b 两条 haiku（10/178/986/11372 与 5/42/0/12000）。
        var day = ours.TotalEntries.Single(entry => entry.Date == "2026-01-19");
        day.InputTokens.Should().Be(1815);
        day.OutputTokens.Should().Be(520);
        day.CacheCreationTokens.Should().Be(1972);
        day.CacheReadTokens.Should().Be(34_744);
        day.TotalTokens.Should().Be(39_051);

        // 01-20：event-c（total 345 → output 兜底）+ thread-b 第三条（totalTokens 500 → output 兜底）。
        var later = ours.TotalEntries.Single(entry => entry.Date == "2026-01-20");
        later.InputTokens.Should().Be(0);
        later.OutputTokens.Should().Be(845);
        later.TotalTokens.Should().Be(845);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.AmpGoldenPath)})";

    private static string OursLabel() => "自研链路(AmpAdapter+PricingEngine)";
}
