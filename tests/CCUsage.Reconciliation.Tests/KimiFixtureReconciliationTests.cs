using CCUsage.Core.Models;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（Kimi 侧，I-013）：仓库内 Kimi fixture（合成 sessions/**/wire.jsonl + config.json）上，
/// 自研链路（KimiCodeAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 kimi daily --json --offline，KIMI_DATA_DIR 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。kimi daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：旧格式 StatusUpdate token_usage 五桶（cache_creation 参与计价，cache_write
/// 按 input×1.25 推导与 CLI 同式）、total 兜底 output 补齐、同 message_id 同刻跨行 first-wins 去重、
/// config.json 模型显示名、新格式 usage.record turn 级采集（session 级与全零拒绝、kimi-code/ 前缀
/// 剥离）、3/5 段双布局发现（4 段与 non-wire 不发现）、秒级时间戳毫秒截断。
/// fixture 不含缺省模型（kimi-for-coding）行——蓝本按时间戳选定价档的候选计价属 I-024 欠账
/// （卡片偏差①），该语义由单测覆盖；也不含 extra&gt;0 行（CLI outputTokens 单列，偏差②），由单测覆盖。
/// </summary>
public class KimiFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadKimi(FixturePaths.KimiGoldenPath);
        var ours = await UsageChainAggregator.AggregateKimiAsync(FixturePaths.KimiRoot);

        // 链路健康检查：双布局发现、坏行/拒绝显式计数、同 message_id 去重。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial, "存在坏 JSON 行与被拒记录");
        ours.Diagnostics.FilesDiscovered.Should().Be(2, "旧 3 段 + 新 5 段布局（4 段与 other.jsonl 不发现）");
        ours.Diagnostics.MalformedLines.Should().Be(1, "session-b 的坏 JSON 行");
        ours.Diagnostics.InvalidRecords.Should().Be(2, "session 级累计记录 + 全零 usage.record");
        ours.Diagnostics.LinesSkipped.Should().Be(2, "metadata 与 TurnBegin 行无预过滤标记");
        ours.Diagnostics.DuplicateRecords.Should().Be(1, "session-a 重复的 msg-1 StatusUpdate 折叠");
        ours.RecordCount.Should().Be(3, "session-a 2（msg-1 + total 兜底 msg-2）+ session-b 1");
        ours.UnpricedModels.Should().BeEmpty("kimi-k2.5 由新增 moonshot/kimi-k2.5 快照条目覆盖");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_dual_format_and_total_fallback_semantics()
    {
        var ours = await UsageChainAggregator.AggregateKimiAsync(FixturePaths.KimiRoot);

        // 02-13（旧格式，config 模型 kimi-k2.5）：msg-1（100/50/20/10）+ msg-2（total 432、
        // output 为 0 → 缺口补 output）- 重复 msg-1 折叠。
        var first = ours.TotalEntries.Single(entry => entry.Date == "2026-02-13");
        first.InputTokens.Should().Be(100);
        first.OutputTokens.Should().Be(482, "50 + total 缺口补入 432");
        first.CacheCreationTokens.Should().Be(20);
        first.CacheReadTokens.Should().Be(10);
        first.TotalTokens.Should().Be(612);

        // 06-22（新格式，kimi-code/kimi-k2.5 剥前缀 → kimi-k2.5）：turn 级 3064/76/0/14848。
        var second = ours.TotalEntries.Single(entry => entry.Date == "2026-06-22");
        second.InputTokens.Should().Be(3064);
        second.OutputTokens.Should().Be(76);
        second.CacheReadTokens.Should().Be(14848);
        second.CacheCreationTokens.Should().Be(0);
        second.TotalTokens.Should().Be(17988);

        ours.ModelEntries.Where(entry => entry.Model == "kimi-k2.5")
            .Sum(entry => entry.TotalTokens).Should().Be(18600);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.KimiGoldenPath)})";

    private static string OursLabel() => "自研链路(KimiCodeAdapter+PricingEngine)";
}
