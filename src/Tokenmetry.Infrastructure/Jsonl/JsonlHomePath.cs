namespace Tokenmetry.Infrastructure.Jsonl;

/// <summary>
/// 各 agent 日志定位器共享的 home 路径工具（claude.rs / codex.rs 同规则）：
/// home_dir 取 USERPROFILE 优先于 HOME；仅展开 "~" 与 "~/前缀" 两种形式（调用方负责先去首尾空白）。
/// </summary>
public static class JsonlHomePath
{
    public static string? GetHomeDir(Func<string, string?> getEnvironmentVariable)
        => getEnvironmentVariable("USERPROFILE") ?? getEnvironmentVariable("HOME");

    public static string ExpandHomePath(string raw, Func<string, string?> getEnvironmentVariable)
    {
        if (raw == "~" && GetHomeDir(getEnvironmentVariable) is { } homeOnly)
        {
            return homeOnly;
        }

        if (raw.StartsWith("~/", StringComparison.Ordinal) && GetHomeDir(getEnvironmentVariable) is { } home)
        {
            // Windows 的 Path.Combine 不归一化分隔符，remainder 保留正斜杠会产出 "C:\Users\x\a/b"
            // 混合路径并被调用方原样返回；Unix 上分隔符本就是 '/'，替换为空操作。
            return Path.Combine(home, raw[2..].Replace('/', Path.DirectorySeparatorChar));
        }

        return raw;
    }
}
