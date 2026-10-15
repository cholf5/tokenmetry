using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.Antigravity;

/// <summary>
/// Antigravity 会话数据库定位（I-021，逐条移植蓝本 ../ccusage/rust/adapters/antigravity/src/paths.rs；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// - ANTIGRAVITY_DATA_DIR（**多根**）：逗号分隔、逐段去首尾空白、空段丢弃；**设置即只用配置根**
///   （蓝本 env::var 成功即 return，空值得到空根列表，不回退默认——与 Kilo 同款、与 OpenClaw 的
///   "空白回退默认"不同）；每根可含 conversations/ 子目录或即为会话目录本身（蓝本 conversation_dir）；
/// - 默认六根（蓝本 DEFAULT_ANTIGRAVITY_ROOTS 固定清单，**非 glob**——卡片 Background 的
///   "antigravity*" 为描述性写法）：~/.gemini/antigravity、~/.gemini/antigravity-cli、
///   ~/.gemini/antigravity-ide、~/.gemini/antigravity-backup、~/.gemini/antigravity-acp、
///   ~/.config/antigravity（存在者保留按序，与蓝本一致默认根不做存在性过滤交给收集后按文件去重——
///   蓝本 roots() 无 is_dir 过滤，conversation_dir 的 is_dir 只用于 conversations/ 子目录判定）；
/// - .db 发现（蓝本 collect_files_with_extension）：根下递归收集扩展名恰为 ".db" 的文件
///   （共享 <see cref="SourceFileCollector"/>：符号链接不跟随、读不了的目录静默跳过），每根内
///   conversation_dir 判定后收集；跨根按规范化路径去重（蓝本 fs::canonicalize——C# 用
///   ResolveLinkTarget 终点解析 + GetFullPath 兜底，失败回退原路径），最后全部路径按序排序
///   （蓝本 paths.sort()，跨根统一 Ordinal 排序而非根序拼接）。
/// 环境变量经可注入访问器读取，测试不依赖真实用户目录。
/// </summary>
public class AntigravityLogLocator
{
    /// <summary>Antigravity 数据目录环境变量（蓝本 paths.rs ANTIGRAVITY_DATA_DIR_ENV）。</summary>
    public const string DataDirEnvironmentVariable = "ANTIGRAVITY_DATA_DIR";

    /// <summary>会话子目录名（蓝本 conversation_dir 的嵌套候选）。</summary>
    public const string ConversationsDirectoryName = "conversations";

    /// <summary>默认根相对 home 的路径（蓝本 DEFAULT_ANTIGRAVITY_ROOTS 六项固定清单，按序探测）。</summary>
    public static readonly IReadOnlyList<string> DefaultHomeRoots = new[]
    {
        Path.Combine(".gemini", "antigravity"),
        Path.Combine(".gemini", "antigravity-cli"),
        Path.Combine(".gemini", "antigravity-ide"),
        Path.Combine(".gemini", "antigravity-backup"),
        Path.Combine(".gemini", "antigravity-acp"),
        Path.Combine(".config", "antigravity"),
    };

    private readonly Func<string, string?> _getEnvironmentVariable;

    public AntigravityLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _getEnvironmentVariable = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析参与扫描的会话数据库文件（蓝本 conversation_db_paths）：根解析（env 或默认六根）→
    /// 每根 conversation_dir 判定 → 递归收集 ".db" → 规范化路径去重 → 全体排序。
    /// </summary>
    public IReadOnlyList<string> CollectConversationDatabasePaths(string? configuredDataDir)
    {
        var roots = ResolveRoots(configuredDataDir);
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var files = new List<string>();
            SourceFileCollector.Collect(ConversationDirectory(root), "db", files);
            foreach (var file in files)
            {
                // 蓝本 fs::canonicalize 失败回退原路径；此处以链接终点解析近似 canonicalize，
                // 失败回退 GetFullPath（不解析符号链接时与 I-012 Goose 同款已登记差异）。
                if (seen.Add(Canonicalize(file)))
                {
                    paths.Add(file);
                }
            }
        }

        // 蓝本 paths.sort()：收集完成后全体排序（跨根统一，Ordinal 同 PathBuf 字节序口径）。
        return paths.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 根解析（蓝本 roots）：ANTIGRAVITY_DATA_DIR / configuredDataDir 已设置（任意值，**含空**）
    /// 即只用配置根（逗号分隔、去空白、空段丢弃、无存在性过滤）；否则主目录下六默认根按序。
    /// </summary>
    public IReadOnlyList<string> ResolveRoots(string? configuredDataDir)
    {
        var raw = configuredDataDir ?? _getEnvironmentVariable(DataDirEnvironmentVariable);
        if (raw is not null)
        {
            var roots = new List<string>();
            foreach (var segment in raw.Split(','))
            {
                var trimmed = segment.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                roots.Add(Path.GetFullPath(JsonlHomePath.ExpandHomePath(trimmed, _getEnvironmentVariable)));
            }

            return roots;
        }

        if (JsonlHomePath.GetHomeDir(_getEnvironmentVariable) is not { } home)
        {
            return [];
        }

        return DefaultHomeRoots
            .Select(relative => Path.GetFullPath(Path.Combine(home, relative)))
            .ToList();
    }

    /// <summary>会话目录（蓝本 conversation_dir）：&lt;root&gt;/conversations 存在即用它，否则根本身。</summary>
    public static string ConversationDirectory(string root)
    {
        var nested = Path.Combine(root, ConversationsDirectoryName);
        return Directory.Exists(nested) ? nested : root;
    }

    /// <summary>规范化路径（蓝本 fs::canonicalize 回退原路径）：符号链接解析到终点，失败回退全路径。</summary>
    private static string Canonicalize(string path)
    {
        try
        {
            if (new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                return Path.GetFullPath(target.FullName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 蓝本 unwrap_or_else(|_| path.clone())：解析失败按原路径去重。
        }

        return Path.GetFullPath(path);
    }
}
