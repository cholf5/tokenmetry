using Tokenmetry.Infrastructure.Antigravity;
using Tokenmetry.Infrastructure.Amp;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.CodexJsonl;
using Tokenmetry.Infrastructure.CopilotSession;
using Tokenmetry.Infrastructure.DshSession;
using Tokenmetry.Infrastructure.Droid;
using Tokenmetry.Infrastructure.GeminiCli;
using Tokenmetry.Infrastructure.Grok;
using Tokenmetry.Infrastructure.GooseSqlite;
using Tokenmetry.Infrastructure.HermesSqlite;
using Tokenmetry.Infrastructure.KiloSqlite;
using Tokenmetry.Infrastructure.OpenClaw;
using Tokenmetry.Infrastructure.OpenCodeSqlite;
using Tokenmetry.Infrastructure.KimiCode;
using Tokenmetry.Infrastructure.Codebuff;
using Tokenmetry.Infrastructure.PiAgent;
using Tokenmetry.Infrastructure.JsonSource;
using Tokenmetry.Infrastructure.QwenCode;
using Tokenmetry.Infrastructure.ZCodeSqlite;

namespace Tokenmetry.Reconciliation.Tests.Support;

/// <summary>
/// 本机真实数据对账（集成测试）的环境探测：
/// - 门控开关：环境变量 TOKENMETRY_RECONCILE_REAL=1 显式开启；
/// - 数据存在性：复用产品定位器（与适配器同一套目录发现逻辑）；
/// - CLI 可用性：npx 运行 pinned 版本 ccusage，失败即视为不具备；
/// - DSH 对账（I-007）：golden 来自 ../dsh-usage-stats 蓝本库（Node 运行 Support/DshBlueprintGolden.mjs，
///   直读原始 zstd 字节 + 蓝本 fold），需 Node 可执行文件与蓝本目录（可用 TOKENMETRY_DSH_BLUEPRINT 指定，
///   缺省从测试输出目录逐级向上找 dsh-usage-stats）。
/// 任何条件不满足时测试 Skip 而非 Fail（任务卡 TC-002）。
/// </summary>
public static class RealReconciliationEnvironment
{
    public const string GateVariable = "TOKENMETRY_RECONCILE_REAL";

    /// <summary>DSH 蓝本目录覆盖环境变量（指向含 lib/usage.js 的 dsh-usage-stats 检出）。</summary>
    public const string BlueprintVariable = "TOKENMETRY_DSH_BLUEPRINT";

    /// <summary>golden 录制与真实对账共用的 ccusage 版本（npx 标签，保证两侧行为一致）。</summary>
    public const string CcusageVersion = "20.0.29";

    public static bool IsGateEnabled()
        => Environment.GetEnvironmentVariable(GateVariable) == "1";

    public static bool HasCodexData()
        => new CodexLogLocator().ResolveSources(configuredHome: null).Count > 0;

    public static bool HasClaudeData()
        => new ClaudeCodeLogLocator().ResolveRoots(configuredRoot: null).Count > 0;

    public static bool HasZcodeData()
        => new ZCodeLogLocator().ResolveDatabases(configuredHome: null).Databases.Count > 0;

    public static bool HasDshData()
        => new DshLogLocator().CollectSessionArtifacts(configuredHome: null).Count > 0;

    /// <summary>
    /// Copilot CLI 数据存在性（I-008，I-023 扩展）：session-state 会话 events.jsonl 或 otel
    /// jsonl / COPILOT_OTEL_FILE_EXPORTER_PATH 任一存在（COPILOT_HOME → ~/.copilot，与 CLI 子进程
    /// 同一套探测；otel 合并后仅 session-state 数据不足以覆盖 CLI 口径）。
    /// </summary>
    public static bool HasCopilotData()
        => new CopilotLogLocator().CollectSourceFiles(configuredHome: null).Count > 0;

    /// <summary>
    /// OpenCode 数据存在性（I-009）：至少一个数据目录下选出了库文件
    /// （OPENCODE_DATA_DIR → XDG 默认根，与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasOpenCodeData()
    {
        var directories = new OpenCodeDataLocator().ResolveDataDirectories(configuredDataDir: null);
        return directories.Any(directory => OpenCodeSqliteAdapter.SelectDatabase(directory) is not null);
    }

    /// <summary>
    /// Goose 数据存在性（I-012）：GOOSE_PATH_ROOT / 双默认根下至少一个库文件
    /// （与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasGooseData()
        => new GooseLogLocator().ResolveDatabases(configuredRoot: null).Count > 0;

    /// <summary>Hermes 数据存在性（I-016）：HERMES_HOME / ~/.hermes 下有 state.db（与 CLI 子进程同一套探测）。</summary>
    public static bool HasHermesData()
        => new HermesLogLocator().ResolveDatabases(configuredHome: null).Count > 0;

    /// <summary>
    /// Kilo 数据存在性（I-017）：KILO_DATA_DIR / 默认根下至少一个目录有 kilo.db
    /// （与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasKiloData()
    {
        var directories = new KiloDataLocator().ResolveDataDirectories(configuredDataDir: null);
        return directories.Any(directory => File.Exists(Path.Combine(directory, KiloDataLocator.DatabaseFileName)));
    }

    /// <summary>
    /// Gemini CLI 数据存在性（I-010）：GEMINI_DATA_DIR / ~/.gemini/tmp 下发现 .json/.jsonl 日志
    /// （与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasGeminiData()
        => new GeminiLogLocator().CollectLogFiles(configuredDataDir: null).Count > 0;

    /// <summary>
    /// Amp 数据存在性（I-014）：AMP_DATA_DIR / ~/.local/share/amp 的 threads/ 下有 .json 线程文件
    /// （与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasAmpData()
        => new AmpLogLocator().CollectThreadFiles(configuredDataDir: null).Count > 0;

    /// <summary>
    /// Droid 数据存在性（I-015）：DROID_SESSIONS_DIR / ~/.factory/sessions 下有 *.settings.json
    /// （与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasDroidData()
        => new DroidLogLocator().CollectSettingsFiles(configuredDataDir: null).Count > 0;

    /// <summary>
    /// Qwen Code 数据存在性（I-011）：QWEN_DATA_DIR / ~/.qwen 的 projects/ 下发现聊天 .jsonl
    /// （与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasQwenData()
        => new QwenLogLocator().CollectChatFiles(new QwenLogLocator().ResolveDataDirectories(configuredDataDir: null)).Count > 0;

    /// <summary>
    /// Kimi Code 数据存在性（I-013）：KIMI_DATA_DIR / ~/.kimi 与 ~/.kimi-code 的 sessions/ 下发现
    /// wire.jsonl（与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasKimiData()
        => new KimiLogLocator().CollectWireFiles(new KimiLogLocator().ResolveDataDirectories(configuredDataDir: null)).Count > 0;

    /// <summary>
    /// Codebuff 数据存在性（I-018）：CODEBUFF_DATA_DIR / ~/.config/&lt;channel&gt; 的 projects/ 下发现
    /// chat-messages.json（与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasCodebuffData()
        => new CodebuffLogLocator().CollectChatFiles(new CodebuffLogLocator().ResolveProjectRoots(null)).Count > 0;

    /// <summary>
    /// pi-agent 数据存在性（I-019）：PI_AGENT_DIR / ~/.pi/agent/sessions 下发现 .jsonl
    /// （与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasPiData()
    {
        var locator = new PiLogLocator();
        return locator.ResolveSessionDirectories(null)
            .SelectMany(directory => CollectJsonlFiles(directory))
            .Any(file => !PiAgentAdapter.IsSubagentArtifactTranscript(file));
    }

    private static List<string> CollectJsonlFiles(string directory)
    {
        var files = new List<string>();
        SourceFileCollector.Collect(directory, "jsonl", files);
        return files;
    }

    /// <summary>
    /// Grok 数据存在性（I-022）：GROK_HOME / ~/.grok 的 sessions/ 下发现 updates.jsonl
    /// （与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasGrokData()
        => new GrokLogLocator().CollectSessionFiles(null).Count > 0;

    /// <summary>
    /// OpenClaw 数据存在性（I-020）：OPENCLAW_DIR / 主目录四默认根下发现会话 JSONL 或
    /// agents/*/agent/openclaw-agent.sqlite（与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasOpenClawData()
    {
        var locator = new OpenClawLogLocator();
        var roots = locator.ResolveDataDirectories(null);
        return roots.Any(root => locator.CollectSessionFiles(root).Count > 0
            || OpenClawSqliteReader.CollectAgentDatabases(root).Count > 0);
    }

    /// <summary>
    /// Antigravity 数据存在性（I-021）：ANTIGRAVITY_DATA_DIR / 六默认根下发现会话 .db
    /// （与 CLI 子进程同一套探测逻辑）。
    /// </summary>
    public static bool HasAntigravityData()
        => new AntigravityLogLocator().CollectConversationDatabasePaths(null).Count > 0;

    /// <summary>
    /// dsh-usage-stats 蓝本目录（含 lib/usage.js）：TOKENMETRY_DSH_BLUEPRINT 优先，
    /// 否则从测试运行目录逐级向上（含兄弟目录）查找；找不到返回 null（测试 Skip）。
    /// </summary>
    public static string? FindBlueprintDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(BlueprintVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "lib", "usage.js")))
        {
            return configured;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && current is not null; depth++, current = current.Parent)
        {
            var sibling = Path.Combine(current.FullName, "dsh-usage-stats", "lib", "usage.js");
            if (File.Exists(sibling))
            {
                return Path.Combine(current.FullName, "dsh-usage-stats");
            }
        }

        return null;
    }
}
