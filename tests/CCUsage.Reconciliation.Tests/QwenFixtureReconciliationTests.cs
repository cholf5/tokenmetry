using CCUsage.Core.Models;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（Qwen 侧，I-011）：仓库内 Qwen fixture（合成 projects/*/chats/*.jsonl）上，自研链路
/// （QwenCodeAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 qwen daily --json --offline，QWEN_DATA_DIR 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。qwen daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：projects/&lt;p&gt;/chats/&lt;f&gt;.jsonl 三段布局过滤（transcripts/ 与 chats/deep/
/// 不发现）、user 行与无标记行预过滤、坏 JSON 行、usageMetadata null/非对象（"oops"）/全零拒绝、
/// totalTokenCount 恰等四桶和（no-op 兜底边界）、output 为 0 时 total 缺口补 output、
/// 同内容键跨文件 first-wins 去重、双项目双日。
/// fixture 规避 thoughts&gt;0 与 total&gt;known 且 output 已知的行（CLI outputTokens 不含 extra，
/// 见卡片偏差②），该语义由 Infrastructure 单测覆盖。
/// </summary>
public class QwenFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadQwen(FixturePaths.QwenGoldenPath);
        var ours = await UsageChainAggregator.AggregateQwenAsync(FixturePaths.QwenRoot);

        // 链路健康检查：布局过滤与预过滤生效、坏行显式计数、去重折叠一条。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial, "存在坏 JSON 行与被拒记录");
        ours.Diagnostics.FilesDiscovered.Should().Be(4, "仅 projects/*/chats/*.jsonl 三段布局（transcripts/ 与 chats/deep/ 不发现）");
        ours.Diagnostics.MalformedLines.Should().Be(1, "chat-b 的坏 JSON 行");
        ours.Diagnostics.InvalidRecords.Should().Be(4, "全零 usage + usageMetadata null + usageMetadata 非对象 + user 行带 usageMetadata");
        ours.Diagnostics.DuplicateRecords.Should().Be(1, "dupe-chat 与 chat-a 的同内容键记录 first-wins 折叠");
        ours.Diagnostics.LinesSkipped.Should().Be(1, "chat-a 的 user 行无 usageMetadata 标记");
        ours.RecordCount.Should().Be(4, "chat-a 1 + chat-b 1 + chat-c 2（dupe-chat 折叠）");
        ours.UnpricedModels.Should().BeEmpty("qwen3-coder-plus 由新增 qwen 快照段覆盖");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_layout_fallback_and_dedup_semantics()
    {
        var ours = await UsageChainAggregator.AggregateQwenAsync(FixturePaths.QwenRoot);

        // 02-23：chat-a（100/50/0/5，extra 0）+ chat-b（200/100/0/20，total 恰等四桶和 no-op）
        // + dupe-chat（同内容键折叠）。
        var first = ours.TotalEntries.Single(entry => entry.Date == "2026-02-23");
        first.InputTokens.Should().Be(300);
        first.OutputTokens.Should().Be(150);
        first.CacheCreationTokens.Should().Be(0);
        first.CacheReadTokens.Should().Be(25);
        first.TotalTokens.Should().Be(475);

        // 02-24：chat-c 记录一（total 40 > known 10、output 为 0 → 缺口补 output 30）
        // + 记录二（300/150/0/100）。
        var second = ours.TotalEntries.Single(entry => entry.Date == "2026-02-24");
        second.InputTokens.Should().Be(310);
        second.OutputTokens.Should().Be(180, "30（total 缺口补入）+ 150");
        second.CacheReadTokens.Should().Be(100);
        second.TotalTokens.Should().Be(590);

        // 记录内容：模型口径跨日合计（ModelEntries 为 日期×模型 粒度）。
        ours.ModelEntries.Where(entry => entry.Model == "qwen3-coder-plus")
            .Sum(entry => entry.TotalTokens).Should().Be(1065);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.QwenGoldenPath)})";

    private static string OursLabel() => "自研链路(QwenCodeAdapter+PricingEngine)";
}
