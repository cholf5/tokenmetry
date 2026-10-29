using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.CodexJsonl;

/// <summary>
/// 一个 Codex 数据源目录；同一 home 下的 sessions / archived_sessions 共享去重范围。
/// </summary>
public sealed record CodexSource(string DirectoryPath, string DedupeScope);

/// <summary>
/// Codex 日志目录发现与 .jsonl 文件枚举。
/// 解析规则与 ccusage-tauri codex.rs 的 resolve_homes / codex_home_candidates /
/// codex_sources_from_homes / collect_deduped_files 对齐：
/// - 显式配置的 home 优先（存在与否由源归一化判断，不再回退自动探测）；
/// - 否则 CODEX_HOME（逗号分隔，支持 ~ 前缀）→ ~/.codex（USERPROFILE 优先于 HOME）；
/// - 每个 home 优先使用 sessions/ 与 archived_sessions/ 子目录，两者都不存在时回退 home 本身；
/// - 同一 home 下 sessions/ 与 archived_sessions/ 的同相对路径文件只保留 sessions/ 活动副本。
/// 递归收集与 home 路径展开与 Claude 定位器共用（<see cref="JsonlFileCollector"/> / <see cref="JsonlHomePath"/>），
/// Codex 特有的源归一化与按 home 去重保留在本类。
/// 环境变量通过可注入的访问器读取，测试不依赖真实用户目录。
/// </summary>
public class CodexLogLocator
{
    private readonly Func<string, string?> _getEnvironmentVariable;

    public CodexLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _getEnvironmentVariable = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析 Codex 数据源目录：显式配置的 home 优先，否则走自动探测（与 codex.rs resolve_homes +
    /// codex_sources_from_homes 一致）。只返回真实存在的目录。
    /// </summary>
    public IReadOnlyList<CodexSource> ResolveSources(string? configuredHome)
    {
        var homes = string.IsNullOrWhiteSpace(configuredHome)
            ? ResolveHomeCandidates().ToList()
            : [JsonlHomePath.ExpandHomePath(configuredHome.Trim(), _getEnvironmentVariable)];

        return SourcesFromHomes(homes);
    }

    /// <summary>自动探测 Codex home 候选（与 codex.rs codex_home_candidates 一致）。</summary>
    public IReadOnlyList<string> ResolveHomeCandidates()
    {
        if (_getEnvironmentVariable("CODEX_HOME") is { } envPaths)
        {
            var homes = envPaths
                .Split(',')
                .Select(rawPath => rawPath.Trim())
                .Where(rawPath => rawPath.Length > 0)
                .Select(rawPath => JsonlHomePath.ExpandHomePath(rawPath, _getEnvironmentVariable))
                .ToList();
            if (homes.Count > 0)
            {
                return homes;
            }
        }

        return JsonlHomePath.GetHomeDir(_getEnvironmentVariable) is { } home
            ? [Path.Combine(home, ".codex")]
            : [];
    }

    /// <summary>
    /// home → 数据源目录（与 codex.rs codex_sources_from_homes 一致）：
    /// 优先 sessions/ 与 archived_sessions/（去重范围均为所属 home），两者都不存在时回退 home 本身。
    /// </summary>
    private IReadOnlyList<CodexSource> SourcesFromHomes(IReadOnlyList<string> homes)
    {
        var sources = new List<CodexSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var home in homes)
        {
            var sessions = Path.Combine(home, "sessions");
            var archived = Path.Combine(home, "archived_sessions");
            var found = false;

            if (Directory.Exists(sessions) && seen.Add(sessions))
            {
                sources.Add(new CodexSource(sessions, home));
                found = true;
            }

            if (Directory.Exists(archived) && seen.Add(archived))
            {
                sources.Add(new CodexSource(archived, home));
                found = true;
            }

            if (!found && Directory.Exists(home) && seen.Add(home))
            {
                sources.Add(new CodexSource(home, home));
            }
        }

        return sources;
    }

    /// <summary>rollout 文件判定（蓝本 reader.rs is_rollout_file）：`.jsonl` 或 `.jsonl.zst`；`x.zst` 不算。</summary>
    public static bool IsRolloutFile(string path)
        => path.EndsWith(".jsonl", StringComparison.Ordinal)
           || path.EndsWith(".jsonl.zst", StringComparison.Ordinal);

    /// <summary>与编码无关的 rollout 路径（蓝本 uncompressed_rollout_path）：`.jsonl.zst` → `.jsonl`。</summary>
    public static string UncompressedRolloutPath(string path)
        => path.EndsWith(".jsonl.zst", StringComparison.Ordinal) ? path[..^".zst".Length] : path;

    /// <summary>
    /// 枚举所有数据源下的 rollout 文件（与 codex.rs collect_deduped_files 一致）：
    /// 每个数据源内按完整路径排序；同一去重范围（home）内相对路径相同的文件只保留先出现的
    /// （sources 顺序保证 sessions/ 活动副本优先于 archived_sessions/）。
    /// </summary>
    public static List<(string SourceDir, string Path)> CollectDedupedFiles(IReadOnlyList<CodexSource> sources)
    {
        // 元组键使用默认比较器：string 的默认相等比较即为 ordinal，与蓝本 HashSet<(PathBuf, PathBuf)> 一致。
        var seen = new HashSet<(string Scope, string Relative)>();
        var files = new List<(string SourceDir, string Path)>();

        foreach (var source in sources)
        {
            var sourceFiles = new List<string>();
            JsonlFileCollector.Collect(source.DirectoryPath, sourceFiles, IsRolloutFile);
            sourceFiles.Sort(StringComparer.Ordinal);

            foreach (var file in sourceFiles)
            {
                // 排序把 x.jsonl 排在 x.jsonl.zst 之前，明文优先（Codex 恢复压缩 rollout 时也是
                // 先还原明文再删 .zst）；跨 sessions/ 与 archived_sessions/ 的同名 rollout 同样按
                // 解压后相对路径去重。
                var relative = UncompressedRolloutPath(GetRelativePathOrSelf(source.DirectoryPath, file));
                if (seen.Add((source.DedupeScope, relative)))
                {
                    files.Add((source.DirectoryPath, file));
                }
            }
        }

        return files;
    }

    private static string GetRelativePathOrSelf(string sourceDir, string path)
    {
        try
        {
            return Path.GetRelativePath(sourceDir, path);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentNullException)
        {
            return path;
        }
    }
}
