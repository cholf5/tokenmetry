using CCUsage.Infrastructure.CCUsageCli;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;
using CCUsage.Reconciliation.Tests.Support;
using Xunit.Abstractions;

namespace CCUsage.Reconciliation.Tests;

/// <summary>
/// TC-002 / 本机真实数据对账（集成测试，环境门控）：
/// 仅当 CCUSAGE_RECONCILE_REAL=1 且（ccusage CLI 可用 + 真实数据存在）时运行；
/// 任何条件不满足时 Skip 而非 Fail（任务卡验收口径）。
/// 与 fixture 测试不同，本类会真的调用 `npx ccusage@&lt;version&gt; --json --offline`（首次需要网络下载包），
/// 对本机真实数据（默认 ~/.codex、CLAUDE_CONFIG_DIR / ~/.claude）跑全链路对账。
/// CLI 使用与 golden 相同的版本与参数（--timezone UTC、--offline），保证与 fixture 对账口径一致。
/// </summary>
public class RealDataReconciliationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(5);

    [SkippableFact]
    public async Task Real_codex_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasCodexData(), "本机没有 Codex 数据（~/.codex 或 CODEX_HOME）");

        var goldenJson = RunCli("codex");
        var golden = CcusageGoldenFiles.LoadCodexText(goldenJson);

        // 适配器不带 configuredHome：与 CLI 进程读同一套 CODEX_HOME / ~/.codex 探测逻辑。
        var ours = await UsageChainAggregator.AggregateCodexAsync();

        ReconcileAndAssert(golden, ours, agent: "Codex");
    }

    [SkippableFact]
    public async Task Real_claude_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasClaudeData(), "本机没有 Claude Code 日志（~/.claude/projects 或 CLAUDE_CONFIG_DIR）");

        var goldenJson = RunCli("claude");
        var golden = CcusageGoldenFiles.LoadClaudeText(goldenJson);

        // 适配器不带 configuredRoot：与 CLI 进程读同一套 CLAUDE_CONFIG_DIR / ~/.claude 探测逻辑。
        var ours = await UsageChainAggregator.AggregateClaudeRootsAsync();

        ReconcileAndAssert(golden, ours, agent: "Claude Code");
    }

    private void ReconcileAndAssert(GoldenReport golden, ChainAggregation ours, string agent)
    {
        output.WriteLine($"[{agent}] 自研链路诊断：status={ours.Diagnostics.Status} "
            + $"files={ours.Diagnostics.FilesDiscovered} records={ours.RecordCount} "
            + $"malformed={ours.Diagnostics.MalformedLines} invalid={ours.Diagnostics.InvalidRecords} "
            + $"duplicates={ours.Diagnostics.DuplicateRecords}");
        output.WriteLine($"[{agent}] 自研链路全期汇总：{ours.Summarize()}");
        output.WriteLine($"[{agent}] CLI 全期汇总：{SummarizeGolden(golden)}");
        if (ours.UnpricedModels.Count > 0)
        {
            output.WriteLine($"[{agent}] 自研定价未命中模型："
                + string.Join(", ", ours.UnpricedModels.Select(info => $"{info.Model}({info.RecordCount} 条 / {info.TotalTokens} tokens)")));
        }

        var modelReport = ReconciliationComparator.Compare(golden.ModelEntries, ours.ModelEntries);
        var totalReport = ReconciliationComparator.Compare(golden.TotalEntries, ours.TotalEntries);
        output.WriteLine($"[{agent}] per-model 对账：{modelReport.Render()}");
        output.WriteLine($"[{agent}] 每日/全期总计对账：{totalReport.Render()}");

        // 差异按 日期 × 模型 定位：token 差异 → 解析原因；cost 差异 → 定价原因（差异报告已标注）。
        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    private static string SummarizeGolden(GoldenReport golden)
    {
        var grand = golden.TotalEntries.Last();
        return $"input={grand.InputTokens} output={grand.OutputTokens} "
            + $"cacheCreation={grand.CacheCreationTokens} cacheRead={grand.CacheReadTokens} "
            + $"total={grand.TotalTokens} cost={grand.CostUsd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}";
    }

    /// <summary>
    /// 运行 `npx --yes ccusage@&lt;version&gt; &lt;agent&gt; daily --json --offline --timezone UTC` 并返回 stdout。
    /// 进程失败 / npx 不存在 / 超时都视为"环境不具备"→ Skip（TC-002：无 CLI 环境跳过）。
    /// </summary>
    private string RunCli(string agent)
    {
        var arguments = $"--yes ccusage@{RealReconciliationEnvironment.CcusageVersion} {agent} daily --json --offline --timezone UTC";
        var runner = new CCUsageCliRunner("npx", arguments);
        try
        {
            using var timeout = new CancellationTokenSource(CliTimeout);
            var json = runner.GetOutputAsync(timeout.Token).GetAwaiter().GetResult();
            output.WriteLine($"CLI 命令：npx {arguments}");
            return json;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
            or OperationCanceledException
            or System.ComponentModel.Win32Exception)
        {
            // xunit.skippablefact：运行期抛 SkipException 让测试以 skipped 结束（TC-002）。
            // Win32Exception：npx 二进制不存在（Process.Start 失败）也属"环境不具备"。
            throw new SkipException($"ccusage CLI 不可用（npx 失败、缺失或超时）：{ex.Message}");
        }
    }
}
