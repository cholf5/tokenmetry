using TokenPulse.Infrastructure.Jsonl;
using TokenPulse.Infrastructure.JsonSource;

namespace TokenPulse.Infrastructure.QwenCode;

/// <summary>
/// Qwen Code 数据目录定位（蓝本 ../ccusage/rust/adapters/qwen/src/paths.rs 逐条移植，I-011；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 根目录：configuredDataDir（测试/调用方钉定，仓库共有增强，非蓝本）→ QWEN_DATA_DIR 环境变量
///   （<b>已设置即只用配置</b>，含空串——Rust env::var 对空串返回 Ok，空列表生效、不回退默认；
///   蓝本 is_dir 过滤与去重对默认目录同样生效，语义与共享 <see cref="EnvDataDirResolver"/> 一致）→
///   用户主目录下 .qwen（目录必须实际存在，不存在即无数据）；
/// - 聊天文件（蓝本 discover_chat_files）：每个根下必须有 projects/ 子目录，递归收集 .jsonl 后
///   仅保留 <c>projects/&lt;project&gt;/chats/&lt;file&gt;.jsonl</c>（相对段恰为 3 段、第 2 段恰为
///   "chats"）的聊天文件，按完整路径排序（蓝本 files.sort() 同口径）。
/// </summary>
public class QwenLogLocator
{
    /// <summary>Qwen 数据目录环境变量（蓝本 paths.rs QWEN_DATA_DIR_ENV）。</summary>
    public const string QwenDataDirEnvVariable = "QWEN_DATA_DIR";

    private readonly Func<string, string?> _environmentAccessor;

    public QwenLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析数据根目录列表：configuredDataDir 非空即生效（目录可以不存在，与 Gemini configuredDataDir
    /// 同款仓库增强）；否则 QWEN_DATA_DIR 已设置即只用配置（可为空列表、不回退）；否则默认
    /// ~/.qwen（不存在即空）。
    /// </summary>
    public IReadOnlyList<string> ResolveDataDirectories(string? configuredDataDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return [configuredDataDir];
        }

        var fromEnvironment = EnvDataDirResolver.Resolve(_environmentAccessor, QwenDataDirEnvVariable);
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        if (home is null)
        {
            return [];
        }

        var defaultDirectory = Path.Combine(home, ".qwen");
        return Directory.Exists(defaultDirectory) ? [defaultDirectory] : [];
    }

    /// <summary>
    /// 收集根目录列表下的全部聊天 JSONL 文件（蓝本 discover_chat_files）：仅
    /// projects/&lt;project&gt;/chats/&lt;file&gt;.jsonl 形态，按完整路径排序。
    /// </summary>
    public IReadOnlyList<string> CollectChatFiles(IReadOnlyList<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            var projectsDirectory = Path.Combine(root, "projects");
            if (!Directory.Exists(projectsDirectory))
            {
                continue;
            }

            var candidates = new List<string>();
            SourceFileCollector.Collect(projectsDirectory, "jsonl", candidates);
            files.AddRange(candidates.Where(file => IsChatFile(projectsDirectory, file)));
        }

        // 蓝本 files.sort()：字节序全路径排序，不做去重（根目录列表已按蓝本去重，文件不可能重复）。
        return files
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>聊天文件判定（蓝本 is_chat_file）：相对 projects/ 的路径段恰为
    /// [project, "chats", file.jsonl] 三段才有效（更深或更浅的 .jsonl 不发现）。</summary>
    public static bool IsChatFile(string projectsDirectory, string file)
    {
        var relative = GetRelativeParts(projectsDirectory, file);
        if (relative.Length != 3)
        {
            return false;
        }

        return relative[0].Length > 0
               && string.Equals(relative[1], "chats", StringComparison.Ordinal)
               && relative[2].EndsWith(".jsonl", StringComparison.Ordinal);
    }

    /// <summary>
    /// 从聊天文件路径推导项目名（蓝本 project_from_file）：路径组件取 4 段滑动窗口（自后向前），
    /// 命中 [projects, project, chats, file] 即返回 project；未命中返回 null（调用方回退 "unknown"）。
    /// </summary>
    public static string? ProjectFromPath(string file)
    {
        var parts = file.Split(
            new[] { '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return null;
        }

        for (var index = parts.Length - 4; index >= 0; index--)
        {
            if (string.Equals(parts[index], "projects", StringComparison.Ordinal)
                && string.Equals(parts[index + 2], "chats", StringComparison.Ordinal))
            {
                return parts[index + 1];
            }
        }

        return null;
    }

    private static string[] GetRelativeParts(string projectsDirectory, string file)
    {
        var fullProjects = Path.GetFullPath(projectsDirectory);
        var fullFile = Path.GetFullPath(file);
        if (!fullFile.StartsWith(fullProjects, StringComparison.Ordinal)
            || fullFile.Length <= fullProjects.Length
            || (fullFile[fullProjects.Length] != Path.DirectorySeparatorChar
                && fullFile[fullProjects.Length] != Path.AltDirectorySeparatorChar))
        {
            return [];
        }

        return fullFile[(fullProjects.Length + 1)..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
    }
}
