using Tokenmetry.Infrastructure.CCUsageCli;
using Tokenmetry.Infrastructure.DshSession;
using Tokenmetry.Reconciliation.Tests.CCUsageCli;
using Tokenmetry.Reconciliation.Tests.Comparison;
using Tokenmetry.Reconciliation.Tests.Support;
using Xunit.Abstractions;

namespace Tokenmetry.Reconciliation.Tests;

/// <summary>
/// TC-002 / 本机真实数据对账（集成测试，环境门控）：
/// 仅当 TOKENMETRY_RECONCILE_REAL=1 且（ccusage CLI 可用 + 真实数据存在）时运行；
/// 任何条件不满足时 Skip 而非 Fail（任务卡验收口径）。
/// 与 fixture 测试不同，本类会真的调用 `npx ccusage@&lt;version&gt; --json --offline`（首次需要网络下载包），
/// 对本机真实数据（默认 ~/.codex、CLAUDE_CONFIG_DIR / ~/.claude）跑全链路对账。
/// CLI 使用与 golden 相同的版本与参数（--timezone UTC、--offline），保证与 fixture 对账口径一致；
/// 唯一例外是 Claude 真实数据（见 <see cref="RealReconciliationEnvironment.ClaudeRealDataCcusageVersion"/>）。
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

        var goldenJson = RunCli("claude", RealReconciliationEnvironment.ClaudeRealDataCcusageVersion);
        var golden = CcusageGoldenFiles.LoadClaudeText(goldenJson);

        // 适配器不带 configuredRoot：与 CLI 进程读同一套 CLAUDE_CONFIG_DIR / ~/.claude 探测逻辑。
        var ours = await UsageChainAggregator.AggregateClaudeRootsAsync();

        ReconcileAndAssert(golden, ours, agent: "Claude Code");
    }

    /// <summary>
    /// I-006：本机 ZCode SQLite 账本对账。tokens 全量精确一致（日期×模型 + 每日/全期总计）；
    /// 成本只对 CLI 有价行（cost&gt;0）做容差比较：自定义 provider / 第三方渠道行 CLI 报
    /// missingPricing / cost 0，自研按 2026-10-18 定案走三级计价（CostUsd=null → 引擎），
    /// 属 §6-4/§6-5 有意分歧，CLI 记 0 的行不参与成本断言。
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

        ReconcileTokensExactAndCliPricedCost(
            golden,
            ours,
            agent: "ZCode",
            divergenceNote: "§6-4/§6-5 有意分歧：CLI 对自定义 provider / 第三方渠道行计 0，自研按官方价三级计价");
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
    /// I-009：本机 OpenCode SQLite 数据对账。CLI 口径 vs 自研聚合（AggregateOpenCodeAsync），
    /// tokens 精确一致。按数据存在性门控（OPENCODE_DATA_DIR → XDG 默认根下有库文件），本机无数据
    /// Skip；有数据的机器上全链路对账。两侧读同一套目录探测逻辑。
    /// 已知口径（见 I-009 卡 Dev Log）：CLI 的 outputTokens 不含 reasoning（extra 单列），自研把
    /// reasoning 并入 output——含 reasoning 的真实数据在 output 分量上会以差异形式响亮失败（fail-loud）。
    /// </summary>
    [SkippableFact]
    public async Task Real_opencode_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasOpenCodeData(),
            "本机没有 OpenCode 数据（OPENCODE_DATA_DIR 或 XDG 默认根下无 opencode*.db）");

        var goldenJson = RunCli("opencode");
        var golden = CcusageGoldenFiles.LoadOpenCodeText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 OPENCODE_DATA_DIR / XDG 探测逻辑。
        var ours = await UsageChainAggregator.AggregateOpenCodeAsync();

        ReconcileAndAssert(golden, ours, agent: "OpenCode");
    }

    /// <summary>
    /// I-012：本机 Goose 会话库对账。CLI 口径 vs 自研聚合（AggregateGooseAsync），tokens 精确一致。
    /// 按数据存在性门控（GOOSE_PATH_ROOT / 双默认根下有 sessions.db），本机无数据 Skip；有数据的机器上
    /// 全链路对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-012 卡 Dev Log）：CLI 的 outputTokens 不含 reasoning（extra 单列），自研把 reasoning
    /// 并入 output——含 reasoning（accumulated_total > input+output）的真实数据在 output 分量上会以差异
    /// 形式响亮失败（fail-loud）。
    /// </summary>
    [SkippableFact]
    public async Task Real_goose_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasGooseData(),
            "本机没有 Goose 数据（GOOSE_PATH_ROOT 或 ~/.local/share/goose、~/Library/Application Support/goose、~/.local/share/Block/goose 下无 sessions.db）");

        var goldenJson = RunCli("goose");
        var golden = CcusageGoldenFiles.LoadGooseText(goldenJson);

        // 适配器不带 configuredRoot：与 CLI 进程读同一套 GOOSE_PATH_ROOT / 默认根探测逻辑。
        var ours = await UsageChainAggregator.AggregateGooseAsync();

        ReconcileAndAssert(golden, ours, agent: "Goose");
    }

    /// <summary>
    /// I-016：本机 Hermes state 库对账。CLI 口径 vs 自研聚合（AggregateHermesAsync），tokens 精确一致。
    /// 按数据存在性门控（HERMES_HOME / ~/.hermes 下有 state.db），本机无数据 Skip；有数据的机器上全链路
    /// 对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-016 卡 Dev Log）：reasoning 并入 output 的显示差异 fail-loud；CLI 20.0.29 只认
    /// actual_cost_usd（蓝本 HEAD 另回退 estimated），含 estimated-only 成本的真实数据在 cost 上 fail-loud。
    /// </summary>
    [SkippableFact]
    public async Task Real_hermes_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasHermesData(),
            "本机没有 Hermes 数据（HERMES_HOME 或 ~/.hermes/state.db）");

        var goldenJson = RunCli("hermes");
        var golden = CcusageGoldenFiles.LoadHermesText(goldenJson);

        // 适配器不带 configuredHome：与 CLI 进程读同一套 HERMES_HOME / ~/.hermes 探测逻辑。
        var ours = await UsageChainAggregator.AggregateHermesAsync();

        ReconcileAndAssert(golden, ours, agent: "Hermes");
    }

    /// <summary>
    /// I-017：本机 Kilo 数据对账。CLI 口径 vs 自研聚合（AggregateKiloAsync），tokens 精确一致。
    /// 按数据存在性门控（KILO_DATA_DIR / 默认根下有 kilo.db），本机无数据 Skip；有数据的机器上全链路
    /// 对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-017 卡 Dev Log）：reasoning/total 余量并入 output 的显示差异 fail-loud。
    /// </summary>
    [SkippableFact]
    public async Task Real_kilo_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasKiloData(),
            "本机没有 Kilo 数据（KILO_DATA_DIR 或 ~/.local/share/kilo/kilo.db）");

        var goldenJson = RunCli("kilo");
        var golden = CcusageGoldenFiles.LoadKiloText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 KILO_DATA_DIR / 默认根探测逻辑。
        var ours = await UsageChainAggregator.AggregateKiloAsync();

        ReconcileAndAssert(golden, ours, agent: "Kilo");
    }

    /// <summary>
    /// I-010：本机 Gemini CLI chats 数据对账。CLI 口径 vs 自研聚合（AggregateGeminiAsync），
    /// tokens 精确一致。按数据存在性门控（GEMINI_DATA_DIR / ~/.gemini/tmp 下有 .json/.jsonl 日志），
    /// 本机无数据 Skip；有数据的机器上全链路对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-010 卡 Dev Log）：CLI 的 outputTokens 不含 thoughts/extra（单列），自研把 extra
    /// 并入 output——含 thoughts 或 stats total 双计 cached 的真实数据在 output 分量上会以差异形式
    /// 响亮失败（fail-loud）。
    /// </summary>
    [SkippableFact]
    public async Task Real_gemini_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasGeminiData(),
            "本机没有 Gemini CLI 数据（GEMINI_DATA_DIR 或 ~/.gemini/tmp 下无 .json/.jsonl）");

        var goldenJson = RunCli("gemini");
        var golden = CcusageGoldenFiles.LoadGeminiText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 GEMINI_DATA_DIR / ~/.gemini/tmp 探测逻辑。
        var ours = await UsageChainAggregator.AggregateGeminiAsync();

        ReconcileAndAssert(golden, ours, agent: "Gemini CLI");
    }

    /// <summary>
    /// I-014：本机 Amp threads 数据对账。CLI 口径 vs 自研聚合（AggregateAmpAsync），tokens 精确一致。
    /// 按数据存在性门控（AMP_DATA_DIR / ~/.local/share/amp 下有 threads/*.json），本机无数据 Skip；
    /// 有数据的机器上全链路对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-014 卡 Dev Log）：CLI 的 outputTokens 不含 extra（单列），自研把 extra 并入
    /// output——含 total 缺口（output 已知时）的真实数据在 output 分量上会以差异形式响亮失败（fail-loud）。
    /// </summary>
    [SkippableFact]
    public async Task Real_amp_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasAmpData(),
            "本机没有 Amp 数据（AMP_DATA_DIR 或 ~/.local/share/amp/threads 下无 .json）");

        var goldenJson = RunCli("amp");
        var golden = CcusageGoldenFiles.LoadAmpText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 AMP_DATA_DIR / ~/.local/share/amp 探测逻辑。
        var ours = await UsageChainAggregator.AggregateAmpAsync();

        ReconcileAndAssert(golden, ours, agent: "Amp");
    }

    /// <summary>
    /// I-015：本机 Droid (Factory) 会话快照对账。CLI 口径 vs 自研聚合（AggregateDroidAsync），
    /// tokens 精确一致。按数据存在性门控（DROID_SESSIONS_DIR / ~/.factory/sessions 下有
    /// *.settings.json），本机无数据 Skip；有数据的机器上全链路对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-015 卡 Dev Log）：CLI 的 outputTokens 不含 thinkingTokens（extra 单列），自研把
    /// extra 并入 output——含 thinkingTokens 的真实数据在 output 分量上会以差异形式响亮失败（fail-loud）。
    /// </summary>
    [SkippableFact]
    public async Task Real_droid_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasDroidData(),
            "本机没有 Droid 数据（DROID_SESSIONS_DIR 或 ~/.factory/sessions 下无 *.settings.json）");

        var goldenJson = RunCli("droid");
        var golden = CcusageGoldenFiles.LoadDroidText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 DROID_SESSIONS_DIR / ~/.factory/sessions 探测逻辑。
        var ours = await UsageChainAggregator.AggregateDroidAsync();

        ReconcileAndAssert(golden, ours, agent: "Droid");
    }

    /// <summary>
    /// I-011：本机 Qwen Code chats 数据对账。CLI 口径 vs 自研聚合（AggregateQwenAsync），
    /// tokens 精确一致。按数据存在性门控（QWEN_DATA_DIR / ~/.qwen 的 projects/ 下有聊天 .jsonl），
    /// 本机无数据 Skip；有数据的机器上全链路对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-011 卡 Dev Log）：CLI 的 outputTokens 不含 thoughts/total 缺口（extra 单列），
    /// 自研把 extra 并入 output——含 thoughts 或 total 缺口（output 已知时）的真实数据在 output 分量上
    /// 会以差异形式响亮失败（fail-loud）。
    /// </summary>
    [SkippableFact]
    public async Task Real_qwen_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasQwenData(),
            "本机没有 Qwen Code 数据（QWEN_DATA_DIR 或 ~/.qwen/projects 下无 chats/*.jsonl）");

        var goldenJson = RunCli("qwen");
        var golden = CcusageGoldenFiles.LoadQwenText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 QWEN_DATA_DIR / ~/.qwen 探测逻辑。
        var ours = await UsageChainAggregator.AggregateQwenAsync();

        ReconcileAndAssert(golden, ours, agent: "Qwen Code");
    }

    /// <summary>
    /// I-013：本机 Kimi Code wire 数据对账。CLI 口径 vs 自研聚合（AggregateKimiAsync），
    /// tokens 精确一致。按数据存在性门控（KIMI_DATA_DIR / ~/.kimi 与 ~/.kimi-code 的 sessions/ 下有
    /// wire.jsonl），本机无数据 Skip；有数据的机器上全链路对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-013 卡 Dev Log）：CLI 的 outputTokens 不含 extra（单列），自研把 extra 并入
    /// output——含 total 缺口（output 已知时）的真实数据在 output 分量上会以差异形式响亮失败
    /// （fail-loud）。
    /// </summary>
    [SkippableFact]
    public async Task Real_kimi_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasKimiData(),
            "本机没有 Kimi 数据（KIMI_DATA_DIR 或 ~/.kimi、~/.kimi-code 下无 sessions/**/wire.jsonl）");

        var goldenJson = RunCli("kimi");
        var golden = CcusageGoldenFiles.LoadKimiText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 KIMI_DATA_DIR / 双默认目录探测逻辑。
        var ours = await UsageChainAggregator.AggregateKimiAsync();

        ReconcileAndAssert(golden, ours, agent: "Kimi");
    }

    /// <summary>
    /// I-018：本机 Codebuff 会话数据对账。CLI 口径 vs 自研聚合（AggregateCodebuffAsync），
    /// tokens 精确一致。按数据存在性门控（CODEBUFF_DATA_DIR / ~/.config/&lt;channel&gt; 的 projects/ 下有
    /// chat-messages.json），本机无数据 Skip；有数据的机器上全链路对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-018 卡 Dev Log）：CLI 的 outputTokens 不含 extra（单列），自研把 extra 并入
    /// output——含 total 缺口（output 已知时）的真实数据在 output 分量上会以差异形式响亮失败
    /// （fail-loud）；缺省模型 codebuff-unknown 与 provider 前缀候选计价属 I-024 欠账，成本 fail-loud。
    /// </summary>
    [SkippableFact]
    public async Task Real_codebuff_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasCodebuffData(),
            "本机没有 Codebuff 数据（CODEBUFF_DATA_DIR 或 ~/.config/manicode* 下无 projects/**/chat-messages.json）");

        var goldenJson = RunCli("codebuff");
        var golden = CcusageGoldenFiles.LoadCodebuffText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 CODEBUFF_DATA_DIR / ~/.config 探测逻辑。
        var ours = await UsageChainAggregator.AggregateCodebuffAsync();

        ReconcileAndAssert(golden, ours, agent: "Codebuff");
    }

    /// <summary>
    /// I-019：本机 pi-agent 会话数据对账。CLI 口径 vs 自研聚合（AggregatePiAsync），
    /// tokens 精确一致。按数据存在性门控（PI_AGENT_DIR / ~/.pi/agent/sessions 下有 .jsonl），
    /// 本机无数据 Skip；有数据的机器上全链路对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-019 卡 Dev Log）：CLI 的 outputTokens 不含 extra（单列）、且蓝本 pi 计价只按
    /// 兜底后四桶（extra 不参与），自研把 extra 并入 output 交引擎——含 total 缺口（output 已知时）
    /// 的真实数据在 output 与成本两个分量上都会以差异形式响亮失败（fail-loud）；重放签名的现算成本
    /// 用内置快照（蓝本用 CLI 全量定价表）。
    /// </summary>
    [SkippableFact]
    public async Task Real_pi_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasPiData(),
            "本机没有 pi-agent 数据（PI_AGENT_DIR 或 ~/.pi/agent/sessions 下无 .jsonl）");

        var goldenJson = RunCli("pi");
        var golden = CcusageGoldenFiles.LoadPiText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 PI_AGENT_DIR / ~/.pi 探测逻辑。
        var ours = await UsageChainAggregator.AggregatePiAsync();

        ReconcileAndAssert(golden, ours, agent: "pi-agent");
    }

    /// <summary>
    /// I-022：本机 Grok Build CLI 会话数据对账。CLI 口径 vs 自研聚合（AggregateGrokAsync），
    /// tokens 精确一致。按数据存在性门控（GROK_HOME / ~/.grok 的 sessions/ 下有 updates.jsonl），
    /// 本机无数据 Skip；有数据的机器上全链路对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-022 卡 Dev Log）：日志 costUsdTicks 走 LogCost 直采（display/auto 同口径），
    /// 无 ticks 的可计价模型行在 CLI 候选链现算（含长上下文分档）与桌面无分档快照间会以成本差异
    /// fail-loud；reasoning 已含在 output（两侧同丢弃）。
    /// </summary>
    [SkippableFact]
    public async Task Real_grok_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasGrokData(),
            "本机没有 Grok 数据（GROK_HOME 或 ~/.grok/sessions 下无 updates.jsonl）");

        var goldenJson = RunCli("grok");
        var golden = CcusageGoldenFiles.LoadGrokText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 GROK_HOME / ~/.grok 探测逻辑。
        var ours = await UsageChainAggregator.AggregateGrokAsync();

        ReconcileAndAssert(golden, ours, agent: "Grok");
    }

    /// <summary>
    /// I-020：本机 OpenClaw 双格式数据对账。JSONL 与 agents/*/agent/openclaw-agent.sqlite 均参与，
    /// 同一事件的迁移 SQLite 副本优先于 JSONL 遗留副本（双侧同语义）。按数据存在性门控
    /// （OPENCLAW_DIR / ~/.openclaw、~/.clawdbot、~/.moltbot、~/.moldbot 下有会话 JSONL 或 agent 库），
    /// 本机无数据 Skip；有数据的机器上全链路对账。两侧读同一套探测逻辑。
    /// 已知口径（见 I-020 卡 Dev Log）：usage.cost.total 内建成本 LogCost 直采；无内建成本的可计价
    /// 模型行 CLI 按裸模型名候选现算，桌面按装饰名不命中记 0（I-024 欠账）——成本分量 fail-loud。
    /// </summary>
    [SkippableFact]
    public async Task Real_openclaw_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasOpenClawData(),
            "本机没有 OpenClaw 数据（OPENCLAW_DIR 或 ~/.openclaw / ~/.clawdbot / ~/.moltbot / ~/.moldbot）");

        var goldenJson = RunCli("openclaw");
        var golden = CcusageGoldenFiles.LoadOpenClawText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 OPENCLAW_DIR / 四默认根探测逻辑。
        var ours = await UsageChainAggregator.AggregateOpenClawAsync();

        ReconcileAndAssert(golden, ours, agent: "OpenClaw");
    }

    /// <summary>
    /// I-021：本机 Antigravity 会话库对账。gen_metadata / steps / trajectory_metadata_blob 解析
    /// 双侧同语义。按数据存在性门控（ANTIGRAVITY_DATA_DIR / ~/.gemini/antigravity* 五根与
    /// ~/.config/antigravity 下有 .db），本机无数据 Skip；有数据的机器上全链路对账。
    /// 两侧读同一套探测逻辑。已知口径（见 I-021 卡 Dev Log）：CLI 计价 output 分量取
    /// total_output（含 reasoning），桌面按 visible output 计价——含 reasoning 的真实数据成本
    /// fail-loud（I-024 AG-1 候选链欠账同段登记）。
    /// </summary>
    [SkippableFact]
    public async Task Real_antigravity_data_reconciles_with_cli()
    {
        Skip.IfNot(RealReconciliationEnvironment.IsGateEnabled(),
            $"环境门控：设 {RealReconciliationEnvironment.GateVariable}=1 启用本机真实数据对账");
        Skip.IfNot(RealReconciliationEnvironment.HasAntigravityData(),
            "本机没有 Antigravity 数据（ANTIGRAVITY_DATA_DIR 或 ~/.gemini/antigravity* / ~/.config/antigravity）");

        var goldenJson = RunCli("antigravity");
        var golden = CcusageGoldenFiles.LoadAntigravityText(goldenJson);

        // 适配器不带 configuredDataDir：与 CLI 进程读同一套 ANTIGRAVITY_DATA_DIR / 六默认根探测逻辑。
        var ours = await UsageChainAggregator.AggregateAntigravityAsync();

        ReconcileAndAssert(golden, ours, agent: "Antigravity");
    }

    /// <summary>
    /// I-007：本机 DSH 会话日志对账。golden 由 dsh-usage-stats 蓝本库直读原始 zstd 字节计算
    /// （Support/DshBlueprintGolden.mjs，foldUsage / estimateTokenCost 均为蓝本库代码），
    /// 自研侧走 DshSessionAdapter 全链路。两侧按 本地日历日（蓝本 dayKey 口径）分桶：
    /// 日期 × 模型、每日总计、全期总计、全局 per-model 四个粒度 tokens 精确一致。
    /// 成本口径（A-0b + 蓝本 fail-closed）：蓝本只能给其内置 DeepSeek 目录覆盖的样本定价；
    /// 目录外样本自研记 CostUsd=null 交定价引擎计价（§6-10 有意分歧）。故蓝本全部样本可计价时
    /// 两侧成本必须一致，存在目录外样本时只要求自研合计 ≥ 蓝本可计价合计。
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

        // 成本口径（A-0b + 蓝本 fail-closed）：蓝本只给它内置 DeepSeek 目录覆盖的样本定价，
        // 目录外样本（ark 路由等）按 fail-closed 记 0；自研把目录外样本记 CostUsd=null，由定价引擎
        // 按快照/models.dev/用户覆盖计价（§6-10 有意分歧）。因此：
        // - 蓝本全部样本可计价 → 两侧成本必须一致（没有可解释超出的样本）；
        // - 蓝本存在未计价样本 → 自研合计只要求 ≥ 蓝本可计价合计，超出部分只能来自这些样本。
        var oursCost = oursGrand.CostUsd ?? 0m; // 不可 Sum(TotalEntries)：日总计与全期总计会重复累加
        var tolerance = ReconciliationTolerance.Default;
        if (golden.IncompleteSamples == 0)
        {
            var costAbsolute = Math.Abs(oursCost - golden.PricedCost);
            var costWithin = costAbsolute < tolerance.CostAbsoluteToleranceUsd
                             || (golden.PricedCost != 0m
                                 && costAbsolute / Math.Abs(golden.PricedCost) <= tolerance.CostRelativeTolerance);
            costWithin.Should().BeTrue(
                $"蓝本全部样本可计价（incompleteSamples=0）：自研成本 {oursCost} 必须与蓝本合计 {golden.PricedCost} 一致");
        }
        else
        {
            (oursCost + tolerance.CostAbsoluteToleranceUsd).Should().BeGreaterThanOrEqualTo(
                golden.PricedCost,
                $"自研成本 {oursCost} 应至少覆盖蓝本可计价合计 {golden.PricedCost}；"
                + $"超出部分只允许来自蓝本未计价的 {golden.IncompleteSamples} 个样本（A-0b 引擎计价）");
        }

        ours.ZeroCostRecordCount.Should().BeLessThanOrEqualTo(golden.IncompleteSamples,
            "自研 0 成本记录只能是蓝本未计价样本的子集：目录外样本自研会尝试引擎计价（A-0b）");
        output.WriteLine($"[DSH] 成本口径：自研合计={oursCost} 蓝本可计价合计={golden.PricedCost} "
            + $"（蓝本未计价样本 {golden.IncompleteSamples} 由自研引擎计价，A-0b 有意分歧；"
            + $"自研 0 成本记录 {ours.ZeroCostRecordCount}）");
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
        WriteChainDiagnostics(golden, ours, agent);

        var modelReport = ReconciliationComparator.Compare(golden.ModelEntries, ours.ModelEntries);
        var totalReport = ReconciliationComparator.Compare(golden.TotalEntries, ours.TotalEntries);
        output.WriteLine($"[{agent}] per-model 对账：{modelReport.Render()}");
        output.WriteLine($"[{agent}] 每日/全期总计对账：{totalReport.Render()}");

        // 差异按 日期 × 模型 定位：token 差异 → 解析原因；cost 差异 → 定价原因（差异报告已标注）。
        modelReport.HasDifferences.Should().BeFalse(modelReport.Render());
        totalReport.HasDifferences.Should().BeFalse(totalReport.Render());
    }

    /// <summary>诊断与两侧汇总（与成本口径无关的公共输出）。</summary>
    private void WriteChainDiagnostics(GoldenReport golden, ChainAggregation ours, string agent)
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
    }

    /// <summary>
    /// tokens 全量精确对账，成本只比较 CLI 有价行（cost &gt; 0）：CLI 记 0 的行按已文档化
    /// 有意分歧（§6-4/§6-5：自定义 provider / 第三方渠道）不参与成本断言，只在报告里汇总。
    /// </summary>
    private void ReconcileTokensExactAndCliPricedCost(
        GoldenReport golden,
        ChainAggregation ours,
        string agent,
        string divergenceNote)
    {
        WriteChainDiagnostics(golden, ours, agent);

        // 成本置 null 后比较器只比 tokens：五分量精确一致仍走同一套差异报告/定位。
        var modelTokenReport = ReconciliationComparator.Compare(
            golden.ModelEntries.Select(entry => entry with { CostUsd = null }).ToList(),
            ours.ModelEntries.Select(entry => entry with { CostUsd = null }).ToList());
        var totalTokenReport = ReconciliationComparator.Compare(
            golden.TotalEntries.Select(entry => entry with { CostUsd = null }).ToList(),
            ours.TotalEntries.Select(entry => entry with { CostUsd = null }).ToList());
        output.WriteLine($"[{agent}] per-model tokens 对账：{modelTokenReport.Render()}");
        output.WriteLine($"[{agent}] 每日/全期 tokens 对账：{totalTokenReport.Render()}");
        modelTokenReport.HasDifferences.Should().BeFalse(modelTokenReport.Render());
        totalTokenReport.HasDifferences.Should().BeFalse(totalTokenReport.Render());

        var tolerance = ReconciliationTolerance.Default;
        var oursByKey = ours.ModelEntries.ToDictionary(entry => (entry.Date, entry.ModelKey));
        var pricedRows = 0;
        var zeroCostRows = 0;
        var zeroCostOursTotal = 0m;
        var costFailures = new List<string>();
        foreach (var goldenEntry in golden.ModelEntries)
        {
            // 缺失条目已由 tokens 比较报告；这里只对双方都存在的行做成本判定。
            if (!oursByKey.TryGetValue((goldenEntry.Date, goldenEntry.ModelKey), out var oursEntry))
            {
                continue;
            }

            if (goldenEntry.CostUsd is not { } goldenCost || goldenCost == 0m)
            {
                zeroCostRows++;
                zeroCostOursTotal += oursEntry.CostUsd ?? 0m;
                continue;
            }

            pricedRows++;
            var oursCost = oursEntry.CostUsd ?? 0m;
            var absolute = Math.Abs(oursCost - goldenCost);
            var relative = absolute / Math.Abs(goldenCost);
            if (absolute >= tolerance.CostAbsoluteToleranceUsd && relative > tolerance.CostRelativeTolerance)
            {
                costFailures.Add($"{goldenEntry.DisplayName}：CLI={goldenCost} 自研={oursCost}");
            }
        }

        costFailures.Should().BeEmpty(
            $"[{agent}] CLI 有价行的成本必须在容差内：\n" + string.Join("\n", costFailures));
        output.WriteLine($"[{agent}] 成本口径：CLI 有价行 {pricedRows} 组全部在容差内；"
            + $"CLI 记 0 行 {zeroCostRows} 组由自研计价（合计 ${zeroCostOursTotal}）——{divergenceNote}");
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
    private string RunCli(string agent, string? version = null)
    {
        var arguments = $"--yes ccusage@{version ?? RealReconciliationEnvironment.CcusageVersion} {agent} daily --json --offline --timezone UTC";
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
