using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.GooseSqlite;

/// <summary>
/// Goose 会话库定位（I-012，逐条移植蓝本 ../ccusage/rust/adapters/goose/src/paths.rs；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// - GOOSE_PATH_ROOT（去首尾空白；**为空视为未设置**回退默认）：&lt;root&gt;/data/sessions/sessions.db；
/// - 默认三候选（依次探测、全部保留存在的文件）：
///   ~/.local/share/goose/sessions/sessions.db、~/Library/Application Support/goose/sessions/sessions.db、
///   ~/.local/share/Block/goose/sessions/sessions.db（双默认根口径）；
/// - 候选按序去重后只保留真实存在的库文件；蓝本对每条候选 canonicalize（符号链接归一），
///   C# 用 GetFullPath 不解析 symlink——经链接指向同一物理库的候选不合并（登记为与 I-006 定位器
///   同类的已登记差异，(canonical path, session id) 去重键兜底）。
/// 环境变量经可注入访问器读取，测试不依赖真实用户目录。
/// </summary>
public class GooseLogLocator
{
    /// <summary>Goose 根目录环境变量（蓝本 GOOSE_PATH_ROOT_ENV）。</summary>
    public const string PathRootEnvironmentVariable = "GOOSE_PATH_ROOT";

    /// <summary>库文件名（蓝本 GOOSE_DB_FILE_NAME）。</summary>
    public const string DatabaseFileName = "sessions.db";

    /// <summary>默认候选相对 home 的路径（蓝本 default_goose_db_candidates）。</summary>
    public static readonly IReadOnlyList<string> DefaultDatabaseRelativePaths = new[]
    {
        Path.Combine(".local", "share", "goose", "sessions", DatabaseFileName),
        Path.Combine("Library", "Application Support", "goose", "sessions", DatabaseFileName),
        Path.Combine(".local", "share", "Block", "goose", "sessions", DatabaseFileName),
    };

    private readonly Func<string, string?> _getEnvironmentVariable;

    public GooseLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _getEnvironmentVariable = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>解析参与扫描的 Goose 会话库（按序去重，只保留真实存在的文件）。</summary>
    public IReadOnlyList<string> ResolveDatabases(string? configuredRoot)
    {
        var root = configuredRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = _getEnvironmentVariable(PathRootEnvironmentVariable);
        }

        // GOOSE_PATH_ROOT 去首尾空白后为空视为未设置（蓝本 trim + is_empty 回退默认）。
        var candidates = !string.IsNullOrWhiteSpace(root)
            ? [Path.Combine(Path.GetFullPath(JsonlHomePath.ExpandHomePath(root.Trim(), _getEnvironmentVariable)), "data", "sessions", DatabaseFileName)]
            : DefaultHomeCandidates();

        var databases = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && seen.Add(candidate))
            {
                databases.Add(candidate);
            }
        }

        return databases;
    }

    private List<string> DefaultHomeCandidates()
    {
        if (JsonlHomePath.GetHomeDir(_getEnvironmentVariable) is not { } home)
        {
            return [];
        }

        return DefaultDatabaseRelativePaths
            .Select(relative => Path.GetFullPath(Path.Combine(home, relative)))
            .ToList();
    }
}
