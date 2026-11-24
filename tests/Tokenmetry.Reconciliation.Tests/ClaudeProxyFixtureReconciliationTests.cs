using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Reconciliation.Tests.CCUsageCli;
using Tokenmetry.Reconciliation.Tests.Comparison;
using Tokenmetry.Reconciliation.Tests.Support;

namespace Tokenmetry.Reconciliation.Tests;

/// <summary>
/// ccusage 20.1.0 #2341（代理网关多 block 响应按转录响应链折叠）的离线 golden 对账。
/// fixture 形态：一次响应按 content block 写成多行（共享 message.id 与 usage，uuid/时间戳不同），
/// 以及被普通用户消息隔开的「同占位 id 第二轮」——前者折叠一次、后者必须各计一次。
/// golden 录制（2026-11-24）：
/// <code>
/// CLAUDE_CONFIG_DIR="$PWD/$FX/claude-proxy" \
///   npx --yes ccusage@20.1.1 claude daily --json --offline --timezone UTC \
///   > $FX/golden/claude-proxy-fixture-daily-v20.1.1.json
/// </code>
/// 测试不调用 CLI，离线确定性。
/// </summary>
public class ClaudeProxyFixtureReconciliationTests
{
    [Fact]
    public async Task Proxied_multi_block_fixture_matches_cli_golden()
    {
        var golden = CcusageGoldenFiles.LoadClaude(FixturePaths.ClaudeProxyGoldenPath);
        var ours = await UsageChainAggregator.AggregateClaudeAsync(FixturePaths.ClaudeProxyRoot);

        // 7 条 usage 行 → 3 次响应：第一次响应 3 个 block 折叠，第二轮复用同 id 但被用户消息断开。
        ours.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        ours.Diagnostics.MalformedLines.Should().Be(0);
        ours.Diagnostics.InvalidRecords.Should().Be(0);
        ours.Diagnostics.DuplicateRecords.Should().Be(4);
        ours.RecordCount.Should().Be(3);
        ours.UnpricedModels.Should().BeEmpty("fixture 模型应被内置快照命中");

        var modelReport = ReconciliationComparator.Compare(
            golden.ModelEntries, ours.ModelEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());
        var totalReport = ReconciliationComparator.Compare(
            golden.TotalEntries, ours.TotalEntries, goldenLabel: GoldenLabel(), oursLabel: OursLabel());

        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());

        golden.TotalEntries.Single(entry => entry.Date == GoldenReport.GrandTotalDate)
            .TotalTokens.Should().Be(44_400);
    }

    private static string GoldenLabel() => $"ccusage@20.1.1 golden({Path.GetFileName(FixturePaths.ClaudeProxyGoldenPath)})";

    private static string OursLabel() => "自研链路(ClaudeCodeJsonlAdapter+PricingEngine)";
}
