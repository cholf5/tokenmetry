using Tokenmetry.Core.Models;
using Tokenmetry.Reconciliation.Tests.CCUsageCli;
using Tokenmetry.Reconciliation.Tests.Comparison;
using Tokenmetry.Reconciliation.Tests.Support;

namespace Tokenmetry.Reconciliation.Tests;

/// <summary>
/// TC-001（Gemini 侧，I-010）：仓库内 Gemini fixture（合成 chats 日志）上，自研链路
/// （GeminiCliAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 gemini daily --json --offline，GEMINI_DATA_DIR 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。gemini daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost），golden 复用 claude DTO。
/// fixture 语义覆盖：.json/.jsonl 双格式、messages 数组路径（user 行跳过、cached 拆分含 tool 并入 input）、
/// JSONL sessionId 滚动与同 id 事件就地替换、非 gemini 行的 stats（models 明细 overlap 扣减）、
/// 无 models 的整块 stats 回退（模型缺省 unknown，CLI missingPricing 双侧同为 0 成本）。
/// fixture 不含 thoughts&gt;0 与"stats total 双计 cached"行（CLI outputTokens 不含 extra，见卡片偏差②），
/// 该语义由单测覆盖。
/// </summary>
public class GeminiFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadGemini(FixturePaths.GeminiGoldenPath);
        var ours = await UsageChainAggregator.AggregateGeminiAsync(FixturePaths.GeminiRoot);

        // 链路健康检查：文件与行均合法（fixture 无坏 JSON）；跳过行 = meta/note 等无 usage 行。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.Diagnostics.DuplicateRecords.Should().Be(1, "session-b 的 msg-b1 同 id 事件就地替换");
        ours.RecordCount.Should().Be(7, "a1/a2/b1(替换后)/b-stats/c-pro/c-flash/d-unknown");
        ours.UnpricedModels.Should().ContainSingle().Which.Model.Should().Be("unknown",
            "stats 兜底模型名两侧都不计价（CLI missingPricing，成本双 0——聚合 Missing 记 0，比较器逐位同）");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_dual_format_parsing_and_cached_split()
    {
        var ours = await UsageChainAggregator.AggregateGeminiAsync(FixturePaths.GeminiRoot);

        // 05-17：a1（pro 505/20/0/500——total 自洽性判别 cached 拆分 + tool 并入 input）+
        // a2（flash 150/10）+ b1（pro 902/60/0/300，同 id 替换后）。
        var day = ours.TotalEntries.Single(entry => entry.Date == "2026-05-17");
        day.InputTokens.Should().Be(1557);
        day.OutputTokens.Should().Be(90);
        day.CacheCreationTokens.Should().Be(0);
        day.CacheReadTokens.Should().Be(800);
        day.TotalTokens.Should().Be(2447);

        // 05-18：b-stats（flash 300/25/0/100）+ c（pro 1600/100/0/400、flash 500/30）+ d（unknown 200/40/0/100，
        // 整块 stats 回退）。unknown 不入 per-model 口径由 CLI 同样不给计价——两粒度均只比较给定模型。
        var pro = ours.ModelEntries.Single(entry => entry.Date == "2026-05-18" && entry.Model == "gemini-2.5-pro");
        pro.InputTokens.Should().Be(1_600, "stats 路径恒 subtract_cached_overlap：2000−400");
        pro.CacheReadTokens.Should().Be(400);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.GeminiGoldenPath)})";

    private static string OursLabel() => "自研链路(GeminiCliAdapter+PricingEngine)";
}
