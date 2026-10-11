namespace CCUsage.Reconciliation.Tests.Support;

/// <summary>对账测试资源路径：fixtures（Claude/Codex/Copilot/OpenCode 合成日志）与 golden（CLI 录制输出）。</summary>
public static class FixturePaths
{
    private const string ClaudeGoldenFileName = "claude-fixture-daily-v20.0.29.json";

    private const string CodexGoldenFileName = "codex-fixture-daily-v20.0.29.json";

    private const string CopilotGoldenFileName = "copilot-fixture-daily-v20.0.29.json";

    private const string OpenCodeGoldenFileName = "opencode-fixture-daily-v20.0.29.json";

    private const string GooseGoldenFileName = "goose-fixture-daily-v20.0.29.json";

    private const string HermesGoldenFileName = "hermes-fixture-daily-v20.0.29.json";

    private const string KiloGoldenFileName = "kilo-fixture-daily-v20.0.29.json";

    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>Claude fixture 配置目录（内含 projects/），供 CLAUDE_CONFIG_DIR 等价使用。</summary>
    public static string ClaudeRoot => Path.Combine(Root, "claude");

    /// <summary>Codex fixture home（内含 sessions/），供 CODEX_HOME 等价使用。</summary>
    public static string CodexRoot => Path.Combine(Root, "codex");

    /// <summary>Copilot fixture home（内含 session-state/），供 COPILOT_HOME 等价使用（I-008）。</summary>
    public static string CopilotRoot => Path.Combine(Root, "copilot");

    /// <summary>OpenCode fixture 数据目录（内含 opencode.db），供 OPENCODE_DATA_DIR 等价使用（I-009）。</summary>
    public static string OpenCodeRoot => Path.Combine(Root, "opencode");

    /// <summary>Goose fixture 根目录（内含 data/sessions/sessions.db），供 GOOSE_PATH_ROOT 等价使用（I-012）。</summary>
    public static string GooseRoot => Path.Combine(Root, "goose");

    /// <summary>Hermes fixture home（内含 state.db），供 HERMES_HOME 等价使用（I-016）。</summary>
    public static string HermesRoot => Path.Combine(Root, "hermes");

    /// <summary>Kilo fixture 数据目录（内含 kilo.db），供 KILO_DATA_DIR 等价使用（I-017）。</summary>
    public static string KiloRoot => Path.Combine(Root, "kilo");

    public static string GoldenDir => Path.Combine(Root, "golden");

    public static string ClaudeGoldenPath => Path.Combine(GoldenDir, ClaudeGoldenFileName);

    public static string CodexGoldenPath => Path.Combine(GoldenDir, CodexGoldenFileName);

    public static string CopilotGoldenPath => Path.Combine(GoldenDir, CopilotGoldenFileName);

    public static string OpenCodeGoldenPath => Path.Combine(GoldenDir, OpenCodeGoldenFileName);

    public static string GooseGoldenPath => Path.Combine(GoldenDir, GooseGoldenFileName);

    public static string HermesGoldenPath => Path.Combine(GoldenDir, HermesGoldenFileName);

    public static string KiloGoldenPath => Path.Combine(GoldenDir, KiloGoldenFileName);
}
