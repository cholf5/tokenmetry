using System.Globalization;
using System.Text.RegularExpressions;
using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.DshSession;

/// <summary>
/// DSH 会话日志工件：一个会话目录内选定的一份日志（只读最高版本）与其目录键。
/// </summary>
public sealed record DshSessionArtifact(
    string LogPath,
    string ProjectDirectoryName,
    string SessionDirectoryName);

/// <summary>
/// DSH 会话日志定位（I-007，口径对齐 ../dsh-usage-stats/scripts/verify-raw.mjs）：
/// DSH_HOME 环境变量 → ~/.dsh（USERPROFILE 优先于 HOME，与 Claude / Codex / ZCode 定位器一致），
/// 会话根固定为 &lt;home&gt;/sessions。工件枚举递归遍历 sessions/，每个目录内收集
/// canonical 文件名 session.jsonl（v0）/ session.v&lt;N&gt;.jsonl（v1+，均可 .zstd）的各代文件，
/// 只保留最高版本——迁移过的会话目录保留多代文件，最高版本才是已提交的完整日志
/// （DSH 每代文件重写完整历史，不做跨代拼接）。
/// 环境变量通过可注入的访问器读取，测试不依赖真实用户目录。
/// </summary>
public class DshLogLocator
{
    /// <summary>DSH 数据根环境变量（verify-raw.mjs 同名同语义）。</summary>
    public const string HomeEnvironmentVariable = "DSH_HOME";

    /// <summary>会话日志根相对 home 的固定路径。</summary>
    public const string SessionsRelativePath = "sessions";

    private static readonly Regex CanonicalLogFileName =
        new(@"^session(\.v([1-9]\d*))?\.jsonl(\.zstd)?$", RegexOptions.CultureInvariant);

    private readonly Func<string, string?> _getEnvironmentVariable;

    public DshLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _getEnvironmentVariable = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>解析 DSH home（显式配置优先，支持 ~ 展开）；无候选时返回 null。</summary>
    public string? ResolveHome(string? configuredHome)
    {
        var raw = string.IsNullOrWhiteSpace(configuredHome)
            ? _getEnvironmentVariable(HomeEnvironmentVariable)
            : configuredHome;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return JsonlHomePath.GetHomeDir(_getEnvironmentVariable) is { } home
                ? Path.Combine(home, ".dsh")
                : null;
        }

        return Path.GetFullPath(JsonlHomePath.ExpandHomePath(raw.Trim(), _getEnvironmentVariable));
    }

    /// <summary>枚举会话工件（每个会话目录一个最高版本日志），按路径排序保证稳定输出。</summary>
    public IReadOnlyList<DshSessionArtifact> CollectSessionArtifacts(string? configuredHome)
    {
        var home = ResolveHome(configuredHome);
        if (home is null)
        {
            return [];
        }

        var sessionsRoot = Path.Combine(home, SessionsRelativePath);
        if (!Directory.Exists(sessionsRoot))
        {
            return [];
        }

        var artifacts = new List<DshSessionArtifact>();
        CollectFromDirectory(sessionsRoot, projectDirectoryName: null, artifacts);
        artifacts.Sort(static (left, right) => string.CompareOrdinal(left.LogPath, right.LogPath));
        return artifacts;
    }

    /// <summary>
    /// 递归收集目录内各代日志并选定最高版本。<paramref name="projectDirectoryName"/> 为上层项目目录名
    /// （sessions/ 直下为 null；进入子目录时由调用方传入当前目录名），用于记录工件的项目归属。
    /// </summary>
    private void CollectFromDirectory(string directory, string? projectDirectoryName, List<DshSessionArtifact> artifacts)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        (int Version, string Path)? best = null;
        var directoryName = Path.GetFileName(directory);
        foreach (var entry in entries)
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    CollectFromDirectory(entry, directoryName, artifacts);
                    continue;
                }

                var match = CanonicalLogFileName.Match(Path.GetFileName(entry));
                if (!match.Success)
                {
                    continue;
                }

                var version = match.Groups[2].Success
                    ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
                    : 0;
                if (best is not { } current || version > current.Version)
                {
                    best = (version, entry);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        if (best is { } selected)
        {
            artifacts.Add(new DshSessionArtifact(
                selected.Path,
                projectDirectoryName ?? directoryName,
                directoryName));
        }
    }
}
