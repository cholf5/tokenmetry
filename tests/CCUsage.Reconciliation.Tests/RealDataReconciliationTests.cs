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
