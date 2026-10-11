using CCUsage.Infrastructure.CCUsageCli;
using CCUsage.Infrastructure.DshSession;
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

    /// <summary>
    /// I-006：本机 ZCode SQLite 账本对账。CLI 侧对自定义 provider 的模型（本机 GLM/Ark 行）
    /// 报 missingPricing / cost 0；自研侧同口径（CostUsd=0），成本两侧同为 0，token 精确一致。
    /// </summary>
    [SkippableFact]
    public async Task Real_zcode_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasZcodeData(), "本机没有 ZCode 账本（~/.zcode/cli/db/db.sqlite 或 ZCODE_HOME）");

        var goldenJson = RunCli("zcode");
        var golden = CcusageGoldenFiles.LoadZcodeText(goldenJson);

        // 适配器不带 configuredHome：与 CLI 进程读同一套 ZCODE_HOME / ~/.zcode 探测逻辑。
        var ours = await UsageChainAggregator.AggregateZcodeAsync();

        ReconcileAndAssert(golden, ours, agent: "ZCode");
    }

    /// <summary>
    /// I-008：本机 Copilot CLI session-state 数据对账。CLI 口径 vs 自研聚合（AggregateCopilotAsync），
    /// tokens 精确一致。按数据存在性门控：本机 ~/.copilot 实测无 session-state/otel 用量数据
    /// （2026-10-11 CLI golden daily=[]、totals 全 0），本机 Skip；有数据的机器上全链路对账。
    /// 两侧读同一套 COPILOT_HOME / ~/.copilot 探测逻辑。
    /// </summary>
    [SkippableFact]
    public async Task Real_copilot_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasCopilotData(),
            "本机没有 Copilot CLI session-state 数据（~/.copilot/session-state/*/events.jsonl 或 COPILOT_HOME）");

        var goldenJson = RunCli("copilot");
        var golden = CcusageGoldenFiles.LoadCopilotText(goldenJson);

        // 适配器不带 configuredHome：与 CLI 进程读同一套 COPILOT_HOME / ~/.copilot 探测逻辑。
        var ours = await UsageChainAggregator.AggregateCopilotAsync();

        ReconcileAndAssert(golden, ours, agent: "Copilot CLI");
    }

    /// <summary>
    /// I-007：本机 DSH 会话日志对账。golden 由 dsh-usage-stats 蓝本库直读原始 zstd 字节计算
    /// （Support/DshBlueprintGolden.mjs，foldUsage / estimateTokenCost 均为蓝本库代码），
    /// 自研侧走 DshSessionAdapter 全链路。两侧按 本地日历日（蓝本 dayKey 口径）分桶：
    /// 日期 × 模型、每日总计、全期总计、全局 per-model 四个粒度 tokens 精确一致；
    /// 成本按 fail-closed 口径比对：蓝本可计价样本成本合计 ≈ 自研 CostUsd 合计（deepseek 目录价，
    /// 不可计价样本两侧都不贡献），且自研 0 成本记录数 = 蓝本 incompleteSamples 数。
    /// </summary>
    [SkippableFact]
    public async Task Real_dsh_data_reconciles_with_blueprint()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasDshData(), "本机没有 DSH 会话日志（~/.dsh/sessions 或 DSH_HOME）");
        var blueprint = RealReconciliationEnvironment.FindBlueprintDirectory();
        Skip.IfNot(blueprint is not null, $"未找到 dsh-usage-stats 蓝本（设 {RealReconciliationEnvironment.BlueprintVariable} 指向检出目录）");

        var goldenJson = RunNodeGolden(blueprint!);
        var golden = DshBlueprintGolden.Load(goldenJson);
        output.WriteLine($"[DSH] 蓝本 golden：{golden.Summarize()}");
        golden.Providers.Should().NotBeEmpty();

        // 适配器不带 configuredHome：与蓝本脚本读同一套 DSH_HOME / ~/.dsh 探测逻辑。
        var ours = await UsageChainAggregator.AggregateDshAsync();
        output.WriteLine($"[DSH] 自研链路诊断：status={ours.Diagnostics.Status} "
            + $"files={ours.Diagnostics.FilesDiscovered} records={ours.RecordCount} "
            + $"malformed={ours.Diagnostics.MalformedLines} invalid={ours.Diagnostics.InvalidRecords} "
            + $"linesSkipped={ours.Diagnostics.LinesSkipped}");
        output.WriteLine($"[DSH] 自研链路全期汇总：{ours.Summarize()}");

        // 自研侧按蓝本同样的四个粒度聚合（本地日历日；DSH 记录模型恒非空）。
        var oursDayModel = ours.ModelEntries;
        var oursGlobalModels = ours.ModelEntries
            .GroupBy(entry => entry.Model!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ReconciliationEntry(
                "(all-days)", group.Key,
                group.Sum(entry => entry.InputTokens),
                group.Sum(entry => entry.OutputTokens),
                group.Sum(entry => entry.CacheCreationTokens),
                group.Sum(entry => entry.CacheReadTokens),
                group.Sum(entry => entry.TotalTokens),
                group.Sum(entry => entry.CostUsd ?? 0m)))
            .ToList();

        var dayModelReport = ReconciliationComparator.Compare(golden.DayModelEntries, oursDayModel,
            goldenLabel: "dsh-usage-stats 蓝本", oursLabel: "自研链路");
        var dayTotalReport = ReconciliationComparator.Compare(DshBlueprintGolden.BuildTotalEntries(golden), ours.TotalEntries,
            goldenLabel: "dsh-usage-stats 蓝本", oursLabel: "自研链路");
        var globalModelReport = ReconciliationComparator.Compare(golden.ModelEntries, oursGlobalModels,
            goldenLabel: "dsh-usage-stats 蓝本", oursLabel: "自研链路");
        output.WriteLine($"[DSH] 日期×模型 对账：{dayModelReport.Render()}");
        output.WriteLine($"[DSH] 每日/全期总计对账：{dayTotalReport.Render()}");
        output.WriteLine($"[DSH] 全局 per-model 对账：{globalModelReport.Render()}");

        dayModelReport.HasDifferences.Should().BeFalse(dayModelReport.Render());
        dayTotalReport.HasDifferences.Should().BeFalse(dayTotalReport.Render());
        globalModelReport.HasDifferences.Should().BeFalse(globalModelReport.Render());

        // 全期四桶直接断言（tokens 必须精确一致——I-007 验收口径）。
        var oursGrand = ours.TotalEntries.Single(entry => entry.Date == GoldenReport.GrandTotalDate);
        oursGrand.InputTokens.Should().Be(golden.GrandTotal.InputTokens);
        oursGrand.OutputTokens.Should().Be(golden.GrandTotal.OutputTokens);
        oursGrand.CacheCreationTokens.Should().Be(golden.GrandTotal.CacheCreationTokens);
        oursGrand.CacheReadTokens.Should().Be(golden.GrandTotal.CacheReadTokens);
        oursGrand.TotalTokens.Should().Be(golden.GrandTotal.TotalTokens);

        // 成本口径（fail-closed）：蓝本可计价样本合计 ≈ 自研合计；自研 0 成本记录数 = 蓝本不可计价样本数。
        var oursCost = ours.TotalEntries.Sum(entry => entry.CostUsd ?? 0m);
        var costAbsolute = Math.Abs(oursCost - golden.PricedCost);
        var costWithin = costAbsolute < ReconciliationTolerance.Default.CostAbsoluteToleranceUsd
                         || (golden.PricedCost != 0m && costAbsolute / Math.Abs(golden.PricedCost) <= ReconciliationTolerance.Default.CostRelativeTolerance);
        costWithin.Should().BeTrue($"自研成本 {oursCost} vs 蓝本可计价合计 {golden.PricedCost}");
        ours.ZeroCostRecordCount.Should().Be(golden.IncompleteSamples,
            "自研记 0 的记录（目录外路由 / 目录未覆盖模型）应与蓝本 fail-closed 样本一一对应");
        output.WriteLine($"[DSH] 成本口径：自研合计={oursCost} 蓝本可计价合计={golden.PricedCost} "
            + $"（自研 0 成本记录 {ours.ZeroCostRecordCount} = 蓝本 incompleteSamples {golden.IncompleteSamples}）");
    }

    /// <summary>运行 Node 蓝本 golden 脚本（直读原始 zstd 字节），返回其 stdout JSON。</summary>
    private string RunNodeGolden(string blueprintDirectory)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Support", "DshBlueprintGolden.mjs");
        Skip.IfNot(File.Exists(scriptPath), $"golden 脚本未随测试输出：{scriptPath}");
        var dshHome = new DshLogLocator().ResolveHome(configuredHome: null);
        Skip.IfNot(dshHome is not null, "无法解析 DSH home（无 DSH_HOME 且无用户目录）");

        var arguments = $"\"{scriptPath}\" \"{dshHome}\" \"{blueprintDirectory}\"";
        var runner = new CCUsageCliRunner("node", arguments);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var json = runner.GetOutputAsync(timeout.Token).GetAwaiter().GetResult();
            output.WriteLine($"golden 命令：node {arguments}");
            return json;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or OperationCanceledException
            or System.ComponentModel.Win32Exception)
        {
            throw new SkipException($"Node / 蓝本 golden 不可用（node 缺失或脚本失败）：{exception.Message}");
        }
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
