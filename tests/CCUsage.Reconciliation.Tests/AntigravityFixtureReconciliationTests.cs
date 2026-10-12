using CCUsage.Core.Models;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（Antigravity 侧，I-021）：仓库内 Antigravity fixture（全部合成 gen_metadata / steps /
/// trajectory_metadata_blob protobuf blob 的会话 .db）上，自研链路（AntigravityAdapter →
/// PricingEngine 内置快照 → 按 UTC 日聚合）与 golden（ccusage 20.0.29 antigravity daily --json
/// --offline，ANTIGRAVITY_DATA_DIR 钉住 fixture，录制命令见 fixtures/golden/README.md）在容差内
/// 一致。antigravity daily 输出形状与 claude 相同（modelBreakdowns 逐模型带 cost，未定价模型
/// cost 0 且 missingPricing=true），golden 复用 claude DTO。fixture 语义覆盖：gen_metadata 代序
/// 续行（ORDER BY idx）、"Gemini 2.5 Pro" 文本归一化、model_id 312 → gemini-2.5-flash 映射、
/// steps 与 gen 同身份副本合并（同 rank 更早时间戳胜出）、provider 24（google）行、无时间戳 gen 行
/// 的 trajectory blob 回退（首个有效候选）、非 token-bearing 零用量行跳过、0 字节未初始化库跳过、
/// claude-4-sonnet 的 cache_creation 计价（两侧显式同价 3.75/M）、未定价模型行（missingPricing
/// 双侧 0 成本）。
/// fixture 规避（I-021 登记；I-024 接线后桌面已带 AG-1 候选链与 total_output 计价口径，fixture
/// 延续原规避以保持 golden 逐位稳定）：reasoning &gt; 0 的行（CLI daily totalTokens 含 extra 分量、
/// 计价 output 取 total_output——桌面统一记录无 extra 字段、token 展示为 visible 口径）、
/// gemini 行 cache_creation &gt; 0（CLI cache_write 缺失按 input×1.25 推导，与桌面快照 0.375 不同）。
/// </summary>
public class AntigravityFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadAntigravity(FixturePaths.AntigravityGoldenPath);
        var ours = await UsageChainAggregator.AggregateAntigravityAsync(FixturePaths.AntigravityRoot);

        // 链路健康检查：库发现、非 token-bearing 跳过、无坏库（fixture 全为合法库）。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        ours.Diagnostics.FilesDiscovered.Should().Be(4, "session.db + second.db + unpriced.db + empty.db");
        ours.Diagnostics.FilesRead.Should().Be(4, "空快照库计入已检查（蓝本 debug 跳过口径）");
        ours.Diagnostics.LinesSkipped.Should().Be(1, "零用量 gen 行（非 token-bearing）");
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.Diagnostics.DuplicateRecords.Should().Be(1, "steps 副本与 gen 行共享身份键合并");
        ours.RecordCount.Should().Be(7, "8 事件（r-1 步骤副本 + r-1/r-2/r-3/r-4/r-5 + c-r-1 + x-r-1）− 1 合并");
        ours.UnpricedModels.Select(info => info.Model).Should().Equal("mystery-model-x");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_merge_and_fallback_semantics()
    {
        var ours = await UsageChainAggregator.AggregateAntigravityAsync(FixturePaths.AntigravityRoot);

        // 2026-05-05：gen 续行 + steps 副本合并（同 rank 更早时间戳）+ trajectory 回退 + model_id 映射。
        var first = ours.TotalEntries.Single(entry => entry.Date == "2026-05-05");
        first.InputTokens.Should().Be(1350, "400（r-1）+ 300（r-2）+ 50（r-3）+ 500（r-4）+ 100（r-5）");
        first.OutputTokens.Should().Be(615, "150 + 75 + 30 + 300 + 60（visible 口径，reasoning 不落记录）");
        first.CacheCreationTokens.Should().Be(0);
        first.CacheReadTokens.Should().Be(1500, "500 + 1000");
        first.TotalTokens.Should().Be(3465, "四桶和（fixture reasoning=0，extra 无差异）");
        first.CostUsd.Should().Be(0.00466m, "gemini-2.5-pro 0.00355 + gemini-2.5-flash 0.00111（快照同价）");

        // 2026-06-09：claude-4-sonnet cache_creation 两侧显式同价。
        var second = ours.TotalEntries.Single(entry => entry.Date == "2026-06-09");
        second.CacheCreationTokens.Should().Be(40);
        second.CostUsd.Should().Be(0.0033m);

        // 2026-06-15：未定价模型双侧 0 成本。
        var third = ours.TotalEntries.Single(entry => entry.Date == "2026-06-15");
        third.CostUsd.Should().Be(0m);
        third.InputTokens.Should().Be(1000);

        // 全期总计与 golden totals 逐位一致。
        var grand = ours.TotalEntries.Single(entry => entry.Date == "(total)");
        grand.InputTokens.Should().Be(2550);
        grand.OutputTokens.Should().Be(1165);
        grand.CacheCreationTokens.Should().Be(40);
        grand.CacheReadTokens.Should().Be(2500);
        grand.TotalTokens.Should().Be(6255);
        grand.CostUsd.Should().Be(0.00796m);

        // 合并后的 r-1 事件时间戳取更早的 steps 副本（同 rank 3 更早者胜）。
        ours.ModelEntries.Where(entry => entry.Model == "gemini-2.5-pro")
            .Sum(entry => entry.InputTokens).Should().Be(750);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.AntigravityGoldenPath)})";

    private static string OursLabel() => "自研链路(AntigravityAdapter+PricingEngine)";
}
