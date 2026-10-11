using CCUsage.Core.Models;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（Copilot 侧，I-008）：仓库内 Copilot CLI fixture 上，自研链路
/// （CopilotSessionAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 copilot daily --json --offline，COPILOT_HOME 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。copilot daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：shutdown 累计快照差分（resume 跨日不重复出账、总量守恒）、同 id 重复
/// shutdown 去重、uncached input 拆分（inputTokens 含缓存读写）、usage_checkpoint 行跳过、
/// 全零模型条目跳过、-1m 内部后缀剥离归并（gpt-5.1-1m → gpt-5.1）、嵌套会话目录不发现。
/// </summary>
public class CopilotFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadCopilot(FixturePaths.CopilotGoldenPath);
        var ours = await UsageChainAggregator.AggregateCopilotAsync(FixturePaths.CopilotRoot);

        // 链路健康检查：坏 JSON 行（含 shutdown 标记，过预过滤后解析失败）计 malformed 不中断；
        // session.started 行、checkpoint 行按 skipped 口径计数；其余行全部按预期消费。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial, "fixture 含 1 行坏 JSON（仓库口径计 malformed）");
        ours.Diagnostics.MalformedLines.Should().Be(1);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.RecordCount.Should().Be(5);
        ours.UnpricedModels.Should().BeEmpty("fixture 覆盖的模型（含 -1m 剥离后的归并名）都应被内置快照命中");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_resume_interval_differential_and_totals_conservation()
    {
        // session-alpha：resume-1（09-14 累计快照）与 resume-2（09-15 累计快照，含全部历史）。
        // 差分后 09-14 出账首条、09-15 只出增量；同 id 重复 shutdown 去重，不出双份。
        var golden = CcusageGoldenFiles.LoadCopilot(FixturePaths.CopilotGoldenPath);
        var ours = await UsageChainAggregator.AggregateCopilotAsync(FixturePaths.CopilotRoot);

        golden.ModelEntries.Select(entry => entry.Model).Distinct()
            .Should().BeEquivalentTo(new[] { "claude-sonnet-4", "gpt-5.1", "gpt-5-mini" });
        // -1m 后缀剥离：CLI 与自研都把 gpt-5.1-1m 归并进 gpt-5.1，模型清单里没有 gpt-5.1-1m。

        var day14 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-14");
        day14.InputTokens.Should().Be(10000, "16000 input − 4000 cacheRead − 2000 cacheWrite（uncached 拆分）");
        day14.OutputTokens.Should().Be(1000);
        day14.CacheCreationTokens.Should().Be(2000);
        day14.CacheReadTokens.Should().Be(4000);

        var day15 = ours.TotalEntries.Single(entry => entry.Date == "2026-09-15");
        // 差分：claude-sonnet-4 uncached 20500−10000=10500、out 1200、cw 500、cr 3000；
        // 加 gpt-5.1（5000/800）、gpt-5-mini（1200/300）、gpt-5.1-1m 归并条目（1500/100/cr500）。
        day15.InputTokens.Should().Be(18200);
        day15.OutputTokens.Should().Be(2400);
        day15.CacheCreationTokens.Should().Be(500);
        day15.CacheReadTokens.Should().Be(3500);

        // 总量守恒：两次快照的 uncached 累计之和 == 各区间之和（resume 不重复出账）。
        var grand = ours.TotalEntries.Single(entry => entry.Date == GoldenReport.GrandTotalDate);
        grand.InputTokens.Should().Be(28200);
        grand.OutputTokens.Should().Be(3400);
        grand.CacheCreationTokens.Should().Be(2500);
        grand.CacheReadTokens.Should().Be(7500);
        grand.TotalTokens.Should().Be(41600);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.CopilotGoldenPath)})";

    private static string OursLabel() => "自研链路(CopilotSessionAdapter+PricingEngine)";
}
