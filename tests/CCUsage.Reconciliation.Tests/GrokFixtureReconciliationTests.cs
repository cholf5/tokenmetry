using CCUsage.Core.Models;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（Grok 侧，I-022）：仓库内 Grok fixture（合成 sessions/**&#47;updates.jsonl）上，自研链路
/// （GrokAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden（ccusage 20.0.29 grok daily
/// --json --offline，GROK_HOME 钉住 fixture，录制命令见 fixtures/golden/README.md）在容差内一致。
/// grok daily 输出形状与 claude 相同（modelBreakdowns 逐模型带 cost，未定价模型 cost 0 且
/// missingPricing=true），golden 复用 claude DTO。fixture 语义覆盖：turn_completed 过滤（tool_call 与
/// 缺 usage 行跳过）、modelUsage 逐模型拆行（Ordinal 排序）与顶层 usage+summary 缺省模型回退、
/// inputTokens 缓存扣除与 cache_creation 子集拆分、costUsdTicks 发票成本 LogCost 直采（1 tick =
/// 1e-10 USD）、agentTimestampMs 毫秒优先与 envelope 秒回退、无 eventId 内容键文件内去重、同 eventId
/// 跨会话全局去重、summary 缺失时会话目录名 id 与 URL 解码项目路径、坏行预过滤跳过。
/// fixture 不含无 ticks 的可计价模型行（I-024 后桌面经 GK-1 候选链 + 200K 分档现算，与 CLI 口径
/// 一致——fixture 仍以带 ticks 发票成本行覆盖之），unpriced 模型行（CLI missingPricing 双侧 0 成本）
/// 覆盖之。
/// </summary>
public class GrokFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadGrok(FixturePaths.GrokGoldenPath);
        var ours = await UsageChainAggregator.AggregateGrokAsync(FixturePaths.GrokRoot);

        // 链路健康检查：turn_completed 过滤、文件内内容键去重、跨文件 eventId 全局去重、坏行计数。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok, "坏行无预过滤标记（计 LinesSkipped），无 Malformed/Invalid");
        ours.Diagnostics.FilesDiscovered.Should().Be(3, "仅文件名恰为 updates.jsonl 者（events.jsonl 与 unified.jsonl 不发现）");
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.LinesSkipped.Should().Be(5,
            "tool_call 行 + 缺 usage 行 + 文件内同内容键折叠行 + 损坏文件 2 坏行");
        ours.Diagnostics.DuplicateRecords.Should().Be(1, "evt-alpha 跨会话全局去重（eventId|模型 first-wins）");
        ours.RecordCount.Should().Be(7, "file1 5（alpha + multi×2 + cc + evt-dup 折叠后）+ file2 2（agent-ts + unknown）");
        ours.UnpricedModels.Select(info => info.Model).Should().Equal(
            "grokbuild-alpha-x1", "grokbuild-beta-x2", "unknown");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_token_split_and_ticks_cost_semantics()
    {
        var ours = await UsageChainAggregator.AggregateGrokAsync(FixturePaths.GrokRoot);

        // 06-15：evt-alpha 缓存扣除（18444 → 7180 uncached）+ evt-cc cache_creation 子集拆分
        // （100 → 35 uncached + 40 cache_read + 25 cache_creation）+ evt-dup（50/10，无 eventId 折叠）。
        var first = ours.TotalEntries.Single(entry => entry.Date == "2025-06-15");
        first.InputTokens.Should().Be(7645);
        first.OutputTokens.Should().Be(253);
        first.CacheCreationTokens.Should().Be(25);
        first.CacheReadTokens.Should().Be(11454);
        first.TotalTokens.Should().Be(19377);
        first.CostUsd.Should().Be(0.0236792m, "ticks 发票成本 LogCost 直采（0.0185192 + 0.005 + 0.00016）");

        // 07-29：agentTimestampMs 毫秒优先于 envelope 秒（1750000000 → 2025-06-15）。
        var second = ours.TotalEntries.Single(entry => entry.Date == "2026-07-29");
        second.InputTokens.Should().Be(10);
        second.OutputTokens.Should().Be(1);
        second.CostUsd.Should().Be(0.001m);

        ours.ModelEntries.Where(entry => entry.Model == "grok-4.5-build")
            .Sum(entry => entry.TotalTokens).Should().Be(18765, "18574（alpha）+ 120（cc）+ 60（dup）+ 11（agent-ts）");
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.GrokGoldenPath)})";

    private static string OursLabel() => "自研链路(GrokAdapter+PricingEngine)";
}
