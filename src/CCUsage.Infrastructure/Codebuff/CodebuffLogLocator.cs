using CCUsage.Infrastructure.Jsonl;
using CCUsage.Infrastructure.JsonSource;

namespace CCUsage.Infrastructure.Codebuff;

/// <summary>
/// Codebuff 数据目录定位（蓝本 ../ccusage/rust/adapters/codebuff/src/paths.rs 逐条移植，I-018；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 根目录：configuredDataDir（测试/调用方钉定，仓库共有增强，非蓝本）→ CODEBUFF_DATA_DIR 环境变量
///   （<b>已设置即只用配置</b>，含空串——Rust env::var 对空串返回 Ok，空段丢弃后为空列表、**不回退
///   三 channel 默认**，蓝本 env 分支提前选定同口径；逗号分隔、逐项去首尾空白；与共享
///   <see cref="EnvDataDirResolver"/> 不同：本蓝本 env 分支<b>不过滤不存在的目录</b>，存在性检查发生在
///   派生出的 projects/ 子目录上）→ 用户主目录下 .config/&lt;channel&gt; 三目录（channel 恒
///   manicode / manicode-dev / manicode-staging，同样不做存在性预检）；
/// - 项目根（蓝本 codebuff_project_roots）：每个根目录文件名恰为 "projects" 者原样使用，否则拼接
///   projects/ 子目录；仅保留实际存在的目录、按序去重（HashSet insert 同口径，路径不做规范化）；
/// - chat 文件（蓝本 discover_chat_files）：每个项目根下递归收集 .json，仅保留文件名恰为
///   chat-messages.json 者，按完整路径排序（排序由蓝本 loader.rs files.sort() 承担，此处一并完成）。
/// </summary>
public class CodebuffLogLocator
{
    /// <summary>Codebuff 数据目录环境变量（蓝本 paths.rs CODEBUFF_DATA_DIR_ENV）。</summary>
    public const string CodebuffDataDirEnvVariable = "CODEBUFF_DATA_DIR";

    /// <summary>项目根目录名（蓝本 codebuff_project_roots 的 "projects" 判定）。</summary>
    public const string ProjectsDirName = "projects";

    /// <summary>聊天记录文件名（蓝本 discover_chat_files 过滤条件）。</summary>
    public const string ChatFileName = "chat-messages.json";

    /// <summary>默认 channel 目录（蓝本 paths.rs CHANNELS）。</summary>
    public static readonly string[] Channels = ["manicode", "manicode-dev", "manicode-staging"];

    private readonly Func<string, string?> _environmentAccessor;

    public CodebuffLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析项目根目录列表（蓝本 codebuff_project_roots）：configuredDataDir 非空即生效（目录可以
    /// 不存在，仓库共有增强）；否则 CODEBUFF_DATA_DIR 已设置即只用配置（可为空列表、不回退三
    /// channel 默认，目录不存在不在此处过滤）；否则 ~/.config/&lt;channel&gt; ×3。每个根派生
    /// projects/ 子目录（根自身文件名恰为 "projects" 者原样使用），仅保留实际存在者、按序去重。
    /// </summary>
    public IReadOnlyList<string> ResolveProjectRoots(string? configuredDataDir)
    {
        var roots = ResolveConfiguredRoots(configuredDataDir);
        var projectRoots = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var projectRoot = FileNameIsProjects(root) ? root : Path.Combine(root, ProjectsDirName);
            if (Directory.Exists(projectRoot) && seen.Add(projectRoot))
            {
                projectRoots.Add(projectRoot);
            }
        }

        return projectRoots;
    }

    /// <summary>收集各项目根下全部 chat-messages.json 文件，按完整路径排序（蓝本 loader files.sort()）。</summary>
    public IReadOnlyList<string> CollectChatFiles(IReadOnlyList<string> projectRoots)
    {
        var files = new List<string>();
        foreach (var projectRoot in projectRoots)
        {
            var candidates = new List<string>();
            SourceFileCollector.Collect(projectRoot, "json", candidates);
            files.AddRange(candidates.Where(file =>
                string.Equals(Path.GetFileName(file), ChatFileName, StringComparison.Ordinal)));
        }

        return files
            .OrderBy(path => path, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private List<string> ResolveConfiguredRoots(string? configuredDataDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return [configuredDataDir];
        }

        var raw = _environmentAccessor(CodebuffDataDirEnvVariable);
        if (raw is not null)
        {
            // 蓝本 env 分支：仅逗号分隔 + trim + 丢空段；不检查目录存在性（交给 projects/ 派生层）。
            var roots = new List<string>();
            foreach (var segment in raw.Split(','))
            {
                var trimmed = segment.Trim();
                if (trimmed.Length > 0)
                {
                    roots.Add(trimmed);
                }
            }

            return roots;
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        if (home is null)
        {
            return [];
        }

        return Channels.Select(channel => Path.Combine(home, ".config", channel)).ToList();
    }

    /// <summary>
    /// 根目录自身文件名是否恰为 "projects"（蓝本 root.file_name() == Some("projects")）。
    /// 去尾部分隔符后取文件名（Rust Path::file_name 对尾随分隔符不敏感，C# 敏感，对齐前者）。
    /// </summary>
    private static bool FileNameIsProjects(string root)
    {
        var probe = root.TrimEnd('/', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (probe.Length == 0)
        {
            return false;
        }

        return string.Equals(Path.GetFileName(probe), ProjectsDirName, StringComparison.Ordinal);
    }
}
