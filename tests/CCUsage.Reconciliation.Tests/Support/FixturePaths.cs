namespace CCUsage.Reconciliation.Tests.Support;

/// <summary>对账测试资源路径：fixtures（Claude/Codex/Copilot 合成日志）与 golden（CLI 录制输出）。</summary>
public static class FixturePaths
{
    private const string ClaudeGoldenFileName = "claude-fixture-daily-v20.0.29.json";

    private const string CodexGoldenFileName = "codex-fixture-daily-v20.0.29.json";

    private const string CopilotGoldenFileName = "copilot-fixture-daily-v20.0.29.json";

    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>Claude fixture 配置目录（内含 projects/），供 CLAUDE_CONFIG_DIR 等价使用。</summary>
    public static string ClaudeRoot => Path.Combine(Root, "claude");

    /// <summary>Codex fixture home（内含 sessions/），供 CODEX_HOME 等价使用。</summary>
    public static string CodexRoot => Path.Combine(Root, "codex");

    /// <summary>Copilot fixture home（内含 session-state/），供 COPILOT_HOME 等价使用（I-008）。</summary>
    public static string CopilotRoot => Path.Combine(Root, "copilot");

    public static string GoldenDir => Path.Combine(Root, "golden");

    public static string ClaudeGoldenPath => Path.Combine(GoldenDir, ClaudeGoldenFileName);

    public static string CodexGoldenPath => Path.Combine(GoldenDir, CodexGoldenFileName);

    public static string CopilotGoldenPath => Path.Combine(GoldenDir, CopilotGoldenFileName);
}
