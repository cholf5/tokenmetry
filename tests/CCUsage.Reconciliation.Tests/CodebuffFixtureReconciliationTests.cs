using CCUsage.Core.Models;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-001（Codebuff 侧，I-018）：仓库内 Codebuff fixture（合成 projects/**/chat-messages.json）上，
/// 自研链路（CodebuffAdapter → PricingEngine 内置快照 → 按 UTC 日聚合）与 golden
/// （ccusage 20.0.29 codebuff daily --json --offline，CODEBUFF_DATA_DIR 钉住 fixture，录制命令见
/// fixtures/golden/README.md）在容差内一致。codebuff daily 输出形状与 claude 相同
/// （modelBreakdowns 逐模型带 cost，另有 credits / unpricedModels 信息字段，DTO 忽略），golden 复用
/// claude DTO。fixture 语义覆盖：metadata.usage 四桶 + 消息级 credits、同 id 同文件重复 last-wins 去重
/// （蓝本 HashMap insert，后到覆盖）、metadata.codebuff.usage 零值字段兜底、runState.messageHistory
/// 倒序回退（prompt_tokens/completion_tokens/prompt_tokens_details.cached_tokens 别名）、total 兜底
/// （output 为 0 补 output）、无消息时间戳回退 chat 目录名时间戳、缺省模型 codebuff-unknown
/// （CLI missingPricing 双侧 0 成本）、坏 JSON 与顶层级对象不发现（Malformed 显式计数）。
/// fixture 不含 provider 前缀候选计价场景——蓝本 calculate_codebuff_cost 的裸名→provider/ 前缀重试
/// 属 I-024 端到端欠账（卡片偏差①），裸 anthropic 日期后缀名由自研家族模式兜底命中同价（TC-001
/// 02-10/02-12 实证）；也不含 extra&gt;0 行（CLI outputTokens 单列，偏差②），由单测覆盖。
/// </summary>
public class CodebuffFixtureReconciliationTests
{
    [Fact]
    public async Task Fixture_chain_matches_cli_golden_within_tolerance()
    {
        var golden = CcusageGoldenFiles.LoadCodebuff(FixturePaths.CodebuffGoldenPath);
        var ours = await UsageChainAggregator.AggregateCodebuffAsync(FixturePaths.CodebuffRoot);

        // 链路健康检查：发现过滤、坏文件显式计数、同 id last-wins 去重。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial, "存在坏 JSON 与顶层级对象文件");
        ours.Diagnostics.FilesDiscovered.Should().Be(7, "仅文件名恰为 chat-messages.json 者（other 类不发现）");
        ours.Diagnostics.MalformedLines.Should().Be(2, "坏 JSON 文件 + 顶层级对象文件");
        ours.Diagnostics.InvalidRecords.Should().Be(0, "Codebuff 无结构性拒绝场景（时间戳有 mtime/纪元兜底）");
        ours.Diagnostics.DuplicateRecords.Should().Be(1, "chat A 重复的 msg-a1 折叠（last-wins，取后者）");
        ours.RecordCount.Should().Be(6, "chat A 2（msg-a1 last-wins 折叠 + msg-a2）+ chat B/C/D/E 各 1");
        ours.UnpricedModels.Should().ContainSingle().Which.Model.Should().Be("codebuff-unknown",
            "缺省模型不参与定价（I-024 欠账口径），CLI missingPricing 同报");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    [Fact]
    public async Task Fixture_exercises_runstate_fallback_and_total_fallback_semantics()
    {
        var ours = await UsageChainAggregator.AggregateCodebuffAsync(FixturePaths.CodebuffRoot);

        // 02-10：msg-a1 重复 last-wins（1500/400/200/2500）+ msg-a2（800/150/0/0）。
        var first = ours.TotalEntries.Single(entry => entry.Date == "2026-02-10");
        first.InputTokens.Should().Be(2300);
        first.OutputTokens.Should().Be(550);
        first.CacheCreationTokens.Should().Be(200);
        first.CacheReadTokens.Should().Be(2500);
        first.TotalTokens.Should().Be(5550);

        // 02-11：runState 回退（prompt_tokens 3000 / completion_tokens 450 / cached_tokens 1200），
        // 无消息时间戳 → chat 目录名 2026-02-11T01-02-03.000Z 归日。
        var second = ours.TotalEntries.Single(entry => entry.Date == "2026-02-11");
        second.InputTokens.Should().Be(3000);
        second.OutputTokens.Should().Be(450);
        second.CacheReadTokens.Should().Be(1200);
        second.TotalTokens.Should().Be(4650);

        // 02-13：total 2000 缺口补 output（500/1500）+ 缺省模型行（100/50）。
        var fourth = ours.TotalEntries.Single(entry => entry.Date == "2026-02-13");
        fourth.InputTokens.Should().Be(600);
        fourth.OutputTokens.Should().Be(1550);
        fourth.TotalTokens.Should().Be(2150);

        ours.ModelEntries.Where(entry => entry.Model == "claude-sonnet-4-20250514")
            .Sum(entry => entry.TotalTokens).Should().Be(8750);
    }

    private static string GoldenLabel() => $"ccusage@{RealReconciliationEnvironment.CcusageVersion} golden({Path.GetFileName(FixturePaths.CodebuffGoldenPath)})";

    private static string OursLabel() => "自研链路(CodebuffAdapter+PricingEngine)";
}
