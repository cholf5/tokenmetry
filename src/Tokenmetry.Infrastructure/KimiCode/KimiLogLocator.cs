using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.KimiCode;

/// <summary>
/// Kimi Code 数据目录定位（蓝本 ../ccusage/rust/adapters/kimi/src/paths.rs 逐条移植，I-013；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 根目录：configuredDataDir（测试/调用方钉定，仓库共有增强，非蓝本）→ KIMI_DATA_DIR 环境变量
///   （<b>已设置即只用配置</b>，含空串——Rust env::var 对空串返回 Ok，空列表生效、**不回退双默认**，
///   蓝本 env 分支提前 return 同口径；逗号分隔/去空白/仅存目录/按序去重）→ 用户主目录下 .kimi 与
///   .kimi-code <b>两个</b>目录（存在者保留，按序去重——蓝本默认分支遍历双目录）；
/// - wire 文件（蓝本 discover_wire_files）：每个根下 sessions/ 子树递归收集 .jsonl，仅保留
///   文件名恰为 wire.jsonl 且相对 sessions/ 的路径段（Normal 组件）恰为 <b>3 或 5</b> 段的文件
///   （旧布局 sessions/&lt;group&gt;/&lt;session&gt;/wire.jsonl、新布局
///   sessions/&lt;ws&gt;/&lt;session&gt;/agents/&lt;agent&gt;/wire.jsonl），按完整路径排序去重
///   （蓝本 files.sort() + files.dedup() 同口径）。
/// </summary>
public class KimiLogLocator
{
    /// <summary>Kimi 数据目录环境变量（蓝本 paths.rs KIMI_DATA_DIR_ENV）。</summary>
    public const string KimiDataDirEnvVariable = "KIMI_DATA_DIR";

    /// <summary>会话根目录名（蓝本 KIMI_SESSIONS_DIR_NAME）。</summary>
    public const string SessionsDirName = "sessions";

    /// <summary>wire 日志文件名（蓝本 KIMI_WIRE_FILE_NAME）。</summary>
    public const string WireFileName = "wire.jsonl";

    private readonly Func<string, string?> _environmentAccessor;

    public KimiLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析数据根目录列表：configuredDataDir 非空即生效（目录可以不存在，Gemini 同款仓库增强）；
    /// 否则 KIMI_DATA_DIR 已设置即只用配置（可为空列表、不回退双默认）；否则默认 ~/.kimi 与
    /// ~/.kimi-code（存在者保留，按序去重）。
    /// </summary>
    public IReadOnlyList<string> ResolveDataDirectories(string? configuredDataDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return [configuredDataDir];
        }

        var fromEnvironment = EnvDataDirResolver.Resolve(_environmentAccessor, KimiDataDirEnvVariable);
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        if (home is null)
        {
            return [];
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in new[] { ".kimi", ".kimi-code" })
        {
            var path = Path.Combine(home, directory);
            if (Directory.Exists(path) && seen.Add(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>收集根目录列表下的全部 wire.jsonl 文件（蓝本 discover_wire_files），按完整路径排序去重。</summary>
    public IReadOnlyList<string> CollectWireFiles(IReadOnlyList<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            var sessionsDirectory = Path.Combine(root, SessionsDirName);
            var candidates = new List<string>();
            SourceFileCollector.Collect(sessionsDirectory, "jsonl", candidates);
            files.AddRange(candidates.Where(file => IsWireFile(sessionsDirectory, file)));
        }

        // 蓝本 files.sort(); files.dedup()：排序后去相邻重复 = 全量去重。
        return files
            .OrderBy(path => path, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// wire 文件判定（蓝本 is_kimi_wire_file）：文件名恰为 wire.jsonl，且相对 sessions/ 的路径段
    /// 恰为 3 段（旧布局）或 5 段（新布局，含 agents/ 一层）。
    /// </summary>
    public static bool IsWireFile(string sessionsDirectory, string file)
    {
        if (!string.Equals(Path.GetFileName(file), WireFileName, StringComparison.Ordinal))
        {
            return false;
        }

        var relative = GetRelativeParts(sessionsDirectory, file);
        return relative.Length is 3 or 5;
    }

    /// <summary>
    /// 从 wire 路径推导 Kimi 根目录（蓝本 kimi_root_from_wire_path；统一口径：旧布局自 wire 文件
    /// 4 次 GetDirectoryName / 自 wire 所在目录 3 级，agents 布局自 wire 所在目录 5 级 = 自 wire 文件
    /// 6 次）；
    /// 不可推导返回 null（调用方回退缺省模型）。
    /// </summary>
    public static string? RootFromWirePath(string file)
    {
        var agentDirectory = Path.GetDirectoryName(Path.GetFullPath(file));
        if (agentDirectory is null)
        {
            return null;
        }

        var agentParent = Path.GetDirectoryName(agentDirectory);
        var isNewLayout = agentParent is not null
                          && string.Equals(Path.GetFileName(agentParent), "agents", StringComparison.Ordinal);
        return WalkUp(agentDirectory, isNewLayout ? 5 : 3);
    }

    /// <summary>
    /// 从 wire 路径推导会话 id（蓝本 extract_session_id）：wire 所在目录的上级目录名为 "agents"
    /// 时取再上两级目录名，否则取 wire 所在目录名；目录名空白回退 "unknown"。
    /// </summary>
    public static string SessionIdFromWirePath(string file)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(file));
        if (parent is null)
        {
            return "unknown";
        }

        var parentParent = Path.GetDirectoryName(parent);
        var sessionDirectory = parentParent is not null
                               && string.Equals(Path.GetFileName(parentParent), "agents", StringComparison.Ordinal)
            ? Path.GetDirectoryName(parentParent)
            : parent;

        var name = sessionDirectory is null ? null : Path.GetFileName(sessionDirectory);
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
    }

    private static string? WalkUp(string directory, int levels)
    {
        var current = directory;
        for (var index = 0; index < levels; index++)
        {
            current = Path.GetDirectoryName(current);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static string[] GetRelativeParts(string sessionsDirectory, string file)
    {
        var fullSessions = Path.GetFullPath(sessionsDirectory);
        var fullFile = Path.GetFullPath(file);
        if (!fullFile.StartsWith(fullSessions, StringComparison.Ordinal)
            || fullFile.Length <= fullSessions.Length
            || (fullFile[fullSessions.Length] != Path.DirectorySeparatorChar
                && fullFile[fullSessions.Length] != Path.AltDirectorySeparatorChar))
        {
            return [];
        }

        return fullFile[(fullSessions.Length + 1)..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
    }
}
