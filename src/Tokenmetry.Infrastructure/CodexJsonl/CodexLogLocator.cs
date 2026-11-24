using System.Text.RegularExpressions;
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
/// - 显式配置的 home 优先（存在与否由源归一化判断，不再回退自动探测，附加发现根也不参与）；
/// - 否则 CODEX_HOME（逗号分隔，支持 ~ 前缀）→ ~/.codex（USERPROFILE 优先于 HOME）；
/// - 再追加宿主提供的附加 home 候选（additionalHomeCandidates）：Cindy 等桌面宿主会把 Codex 子进程的
///   CODEX_HOME 指到自己的私有目录（Cindy = <c>&lt;userData&gt;/codex-home</c> 与
///   <c>codex-accounts/&lt;owner&gt;/&lt;provider&gt;</c>），只扫用户 home 会整段漏计；
/// - 每个 home 优先使用 sessions/ 与 archived_sessions/ 子目录，两者都不存在时回退 home 本身；
/// - 同一 home 下 sessions/ 与 archived_sessions/ 的同相对路径文件只保留 sessions/ 活动副本；
/// - 跨 home 再按 rollout 逻辑身份去重：宿主收养外部会话时会把同一个 rollout 原样复制进私有 home，
///   两个物理副本不能各计一次（见 <see cref="CollectDedupedFiles"/>）。
/// 递归收集与 home 路径展开与 Claude 定位器共用（<see cref="JsonlFileCollector"/> / <see cref="JsonlHomePath"/>），
/// Codex 特有的源归一化与按 home 去重保留在本类。
/// 环境变量通过可注入的访问器读取，测试不依赖真实用户目录。
/// </summary>
public class CodexLogLocator
{
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<IReadOnlyList<string>>? _additionalHomeCandidates;

    public CodexLogLocator(
        Func<string, string?>? environmentAccessor = null,
        Func<IReadOnlyList<string>>? additionalHomeCandidates = null)
    {
        _getEnvironmentVariable = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
        _additionalHomeCandidates = additionalHomeCandidates;
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

    /// <summary>
    /// 自动探测 Codex home 候选（与 codex.rs codex_home_candidates 一致），并在其后追加附加发现根。
    /// 附加根只用于自动探测；显式配置的 home 不会被它们扩展。
    /// </summary>
    public IReadOnlyList<string> ResolveHomeCandidates()
    {
        var homes = new List<string>();

        if (_getEnvironmentVariable("CODEX_HOME") is { } envPaths)
        {
            homes.AddRange(envPaths
                .Split(',')
                .Select(rawPath => rawPath.Trim())
                .Where(rawPath => rawPath.Length > 0)
                .Select(rawPath => JsonlHomePath.ExpandHomePath(rawPath, _getEnvironmentVariable)));
        }

        if (homes.Count == 0
            && JsonlHomePath.GetHomeDir(_getEnvironmentVariable) is { } home)
        {
            homes.Add(Path.Combine(home, ".codex"));
        }

        if (_additionalHomeCandidates is not null)
        {
            foreach (var rawPath in _additionalHomeCandidates())
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                homes.Add(JsonlHomePath.ExpandHomePath(rawPath.Trim(), _getEnvironmentVariable));
            }
        }

        return homes.Distinct(StringComparer.Ordinal).ToList();
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
    /// Cindy 私有 home 里的 rollout 副本命名（与 Cindy codexLocalSessionsCore.ts 同口径）：
    /// <c>rollout-cindy-&lt;kind&gt;[-n]-&lt;原文件名&gt;</c> 与 <c>.cindy-state-less-recovery*</c>。
    /// 这些是恢复/合成产物，原文件在场时不能与它各计一次。
    /// </summary>
    public static bool IsCindyArtifactRolloutFile(string fileName)
        => fileName.StartsWith("rollout-cindy-", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith(".cindy-state-less-recovery", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 枚举所有数据源下的 rollout 文件（与 codex.rs collect_deduped_files 一致）：
    /// 每个数据源内按完整路径排序；同一去重范围（home）内相对路径相同的文件只保留先出现的
    /// （sources 顺序保证 sessions/ 活动副本优先于 archived_sessions/）。
    /// 跨 home 再按 rollout 逻辑身份去重：桌面宿主收养外部会话时会把同一 rollout 复制进私有 home，
    /// 同名副本只保留一份（优先非恢复产物 → 活动目录 → 最新写入副本 → 明文 → 路径序）。
    /// rollout 是追加写的完整历史，最新副本通常最全；两个副本各计一次会把共享前缀翻倍。
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

        return DedupeByRolloutIdentity(files);
    }

    /// <summary>
    /// 同一逻辑 rollout 的多个物理副本只保留一份。
    /// 身份 = 文件名里的 <c>rollout-&lt;时间戳&gt;-&lt;threadId&gt;[_&lt;rolloutId&gt;]</c>（忽略压缩后缀与
    /// Cindy 恢复产物前缀）：同一 thread 的原生 revert 会产生不同 rolloutId 的独立历史，身份因此带上
    /// 第二个 UUID，不能被并成一份；只有真正同名（被复制/收养）的副本才会命中。
    /// </summary>
    private static List<(string SourceDir, string Path)> DedupeByRolloutIdentity(
        List<(string SourceDir, string Path)> files)
    {
        var slotByIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
        var unique = new List<(string SourceDir, string Path)>(files.Count);

        foreach (var candidate in files)
        {
            if (!TryGetRolloutIdentity(candidate.Path, out var identity))
            {
                unique.Add(candidate);
                continue;
            }

            if (!slotByIdentity.TryGetValue(identity, out var slot))
            {
                slotByIdentity[identity] = unique.Count;
                unique.Add(candidate);
                continue;
            }

            if (PreferCandidate(candidate, unique[slot]))
            {
                unique[slot] = candidate;
            }
        }

        return unique;
    }

    private static bool PreferCandidate(
        (string SourceDir, string Path) candidate,
        (string SourceDir, string Path) current)
    {
        var candidateArtifact = IsCindyArtifactRolloutFile(Path.GetFileName(candidate.Path));
        var currentArtifact = IsCindyArtifactRolloutFile(Path.GetFileName(current.Path));
        if (candidateArtifact != currentArtifact)
        {
            return !candidateArtifact;
        }

        var candidateArchived = IsArchivedSource(candidate.SourceDir);
        var currentArchived = IsArchivedSource(current.SourceDir);
        if (candidateArchived != currentArchived)
        {
            return !candidateArchived;
        }

        var candidateWriteTime = LastWriteTimeUtc(candidate.Path);
        var currentWriteTime = LastWriteTimeUtc(current.Path);
        if (candidateWriteTime != currentWriteTime)
        {
            return candidateWriteTime > currentWriteTime;
        }

        var candidateCompressed = IsCompressed(candidate.Path);
        var currentCompressed = IsCompressed(current.Path);
        if (candidateCompressed != currentCompressed)
        {
            return !candidateCompressed;
        }

        return string.CompareOrdinal(candidate.Path, current.Path) < 0;
    }

    private static bool IsArchivedSource(string sourceDir)
        => string.Equals(Path.GetFileName(sourceDir), "archived_sessions", StringComparison.Ordinal);

    private static bool IsCompressed(string path)
        => path.EndsWith(".jsonl.zst", StringComparison.Ordinal);

    private static DateTime LastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// 从 rollout 文件名提取逻辑身份。容忍 Cindy 恢复产物前缀（原文件名嵌在中间）与压缩后缀；
    /// 不符合原生命名（测试 fixture / 迁移占位名）的文件返回 false，保持原样各计一次。
    /// </summary>
    public static bool TryGetRolloutIdentity(string path, out string identity)
    {
        var match = RolloutIdentityPattern.Match(Path.GetFileName(path));
        if (!match.Success)
        {
            identity = string.Empty;
            return false;
        }

        identity = match.Value;
        if (identity.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
        {
            identity = identity[..^4];
        }

        if (identity.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            identity = identity[..^6];
        }

        identity = identity.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// 从 rollout 文件名提取 Codex thread id（第一个 UUID）。原生 revert 的
    /// <c>&lt;threadId&gt;_&lt;rolloutId&gt;</c> 取前者；Cindy 恢复产物前缀不影响（原文件名嵌在中间）。
    /// </summary>
    public static bool TryGetRolloutThreadId(string? pathOrFileName, out string threadId)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName))
        {
            threadId = string.Empty;
            return false;
        }

        var match = RolloutIdentityPattern.Match(Path.GetFileName(pathOrFileName));
        if (!match.Success)
        {
            threadId = string.Empty;
            return false;
        }

        threadId = match.Groups[1].Value.ToLowerInvariant();
        return threadId.Length > 0;
    }

    private static readonly Regex RolloutIdentityPattern = new(
        @"rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?:_([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}))?\.jsonl(?:\.zst)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
