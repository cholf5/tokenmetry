using TokenPulse.Infrastructure.Jsonl;

namespace TokenPulse.Infrastructure.ClaudeCodeJsonl;

/// <summary>
/// Claude Code 日志目录发现与 .jsonl 文件枚举。
/// 解析规则与 ccusage-tauri claude.rs 的 resolve_roots / find_claude_config_dirs / usage_files 对齐：
/// - 显式配置的根目录优先（须包含 projects 子目录，或本身即 projects 目录）；
/// - 否则按 CLAUDE_CONFIG_DIR（逗号分隔，支持 ~ 前缀）→ XDG_CONFIG_HOME/claude → ~/.config/claude → ~/.claude 顺序探测；
/// - 每个根目录下的 projects/ 子树递归收集 .jsonl（projects 下每个子目录是一个项目根）。
/// 递归收集与 home 路径展开与 Codex 定位器共用（<see cref="JsonlFileCollector"/> / <see cref="JsonlHomePath"/>），
/// Claude 特有的候选归一化与候选顺序保留在本类。
/// 环境变量通过可注入的访问器读取，测试不依赖真实用户目录。
/// </summary>
public class ClaudeCodeLogLocator
{
    private readonly Func<string, string?> _getEnvironmentVariable;

    public ClaudeCodeLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _getEnvironmentVariable = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析日志根目录：显式配置的根目录优先，否则走自动探测（与 claude.rs resolve_roots 一致）。
    /// </summary>
    public IReadOnlyList<string> ResolveRoots(string? configuredRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return NormalizeConfigCandidate(configuredRoot) is { } root
                ? new[] { root }
                : Array.Empty<string>();
        }

        return FindClaudeConfigDirs();
    }

    /// <summary>自动探测 Claude 配置目录（与 claude.rs find_claude_config_dirs 一致）。</summary>
    public IReadOnlyList<string> FindClaudeConfigDirs()
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // CLAUDE_CONFIG_DIR 存在时只使用它（支持逗号分隔的多 profile），不再回退默认路径。
        if (_getEnvironmentVariable("CLAUDE_CONFIG_DIR") is { } configDirs)
        {
            foreach (var rawPath in configDirs.Split(','))
            {
                var trimmed = rawPath.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (NormalizeConfigCandidate(ExpandHomePath(trimmed)) is { } path && seen.Add(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        if (_getEnvironmentVariable("XDG_CONFIG_HOME") is { } xdgConfigHome)
        {
            PushRootCandidate(paths, seen, Path.Combine(xdgConfigHome, "claude"));
        }
        else if (GetHomeDir() is { } configHome)
        {
            PushRootCandidate(paths, seen, Path.Combine(configHome, ".config", "claude"));
        }

        if (GetHomeDir() is { } home)
        {
            PushRootCandidate(paths, seen, Path.Combine(home, ".claude"));
        }

        return paths;
    }

    /// <summary>
    /// 归一化候选根目录：须包含 projects 子目录，或本身即名为 projects 的目录（返回其父目录）。
    /// </summary>
    public static string? NormalizeConfigCandidate(string candidate)
    {
        if (Directory.Exists(Path.Combine(candidate, "projects")))
        {
            return candidate;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(candidate);
        if (Path.GetFileName(trimmed) == "projects" && Directory.Exists(trimmed))
        {
            var parent = Path.GetDirectoryName(trimmed);
            return string.IsNullOrEmpty(parent) ? null : parent;
        }

        return null;
    }

    /// <summary>枚举所有根目录 projects/ 子树下的 .jsonl 文件，按完整路径排序。</summary>
    public static List<string> EnumerateUsageFiles(IReadOnlyList<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            JsonlFileCollector.Collect(Path.Combine(root, "projects"), files);
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private void PushRootCandidate(List<string> paths, HashSet<string> seen, string candidate)
    {
        if (NormalizeConfigCandidate(candidate) is { } path && seen.Add(path))
        {
            paths.Add(path);
        }
    }

    private string ExpandHomePath(string raw) => JsonlHomePath.ExpandHomePath(raw, _getEnvironmentVariable);

    private string? GetHomeDir() => JsonlHomePath.GetHomeDir(_getEnvironmentVariable);
}
