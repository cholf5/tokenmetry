using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.ZCodeSqlite;

/// <summary>
/// ZCode 账本库定位（I-006，蓝本 ../ccusage/rust/adapters/zcode/src/paths.rs）：
/// 显式配置根优先（存在与否不回退自动探测）→ ZCODE_HOME（逗号分隔多根，支持 ~ 前缀）→
/// ~/.zcode（USERPROFILE 优先于 HOME，与 Claude / Codex 定位器一致）。
/// 每个根下的库固定为 &lt;root&gt;/cli/db/db.sqlite；根规范化（GetFullPath）并按序去重，
/// 无效根跳过（不静默回退默认目录，与蓝本一致）；多个根解析出同一库文件时只保留首个根的库。
/// 环境变量通过可注入的访问器读取，测试不依赖真实用户目录。
/// </summary>
public class ZCodeLogLocator
{
    /// <summary>ZCode 数据根环境变量（蓝本 ZCODE_HOME_ENV）。</summary>
    public const string HomeEnvironmentVariable = "ZCODE_HOME";

    /// <summary>库文件相对根的固定路径（蓝本 ZCODE_DB_RELATIVE_PATH）。</summary>
    public const string DatabaseRelativePath = "cli/db/db.sqlite";

    private readonly Func<string, string?> _getEnvironmentVariable;

    public ZCodeLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _getEnvironmentVariable = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 定位结果：参与扫描的库文件（按配置顺序）与诊断用的根列表。
    /// </summary>
    public sealed record ResolvedDatabases(IReadOnlyList<string> Databases, IReadOnlyList<string> Roots);

    /// <summary>
    /// 解析参与扫描的 ZCode 账本库：显式配置根优先，否则 ZCODE_HOME → ~/.zcode。
    /// 配置根与 ZCODE_HOME 同语义，支持逗号分隔多根；只返回真实存在的库文件，
    /// 根无效或库不存在即跳过该根。
    /// </summary>
    public ResolvedDatabases ResolveDatabases(string? configuredHome)
    {
        var roots = string.IsNullOrWhiteSpace(configuredHome)
            ? ResolveHomeCandidates().ToList()
            : configuredHome
                .Split(',')
                .Select(rawPath => rawPath.Trim())
                .Where(rawPath => rawPath.Length > 0)
                .Select(rawPath => Path.GetFullPath(JsonlHomePath.ExpandHomePath(rawPath, _getEnvironmentVariable)))
                .ToList();

        var databases = new List<string>();
        var seenDatabases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var database = Path.Combine(root, DatabaseRelativePath);
            if (File.Exists(database) && seenDatabases.Add(database))
            {
                databases.Add(database);
            }
        }

        return new ResolvedDatabases(databases, roots);
    }

    /// <summary>ZCode 根候选（蓝本 paths.rs：ZCODE_HOME 逗号分隔多根 → ~/.zcode）。</summary>
    public IReadOnlyList<string> ResolveHomeCandidates()
    {
        if (_getEnvironmentVariable(HomeEnvironmentVariable) is { } envPaths)
        {
            var roots = envPaths
                .Split(',')
                .Select(rawPath => rawPath.Trim())
                .Where(rawPath => rawPath.Length > 0)
                .Select(rawPath => Path.GetFullPath(JsonlHomePath.ExpandHomePath(rawPath, _getEnvironmentVariable)))
                .ToList();
            if (roots.Count > 0)
            {
                return roots;
            }
        }

        return JsonlHomePath.GetHomeDir(_getEnvironmentVariable) is { } home
            ? [Path.Combine(home, ".zcode")]
            : [];
    }
}
