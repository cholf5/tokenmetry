using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.CodexJsonl;

namespace Tokenmetry.Infrastructure.Cindy;

/// <summary>
/// 宿主归属后处理：把"执行引擎"记录与 Cindy 的产品会话对上，写入
/// <see cref="UsageRecord.Host"/> 与 <see cref="UsageRecord.ProjectPathOverride"/>。
/// <list type="bullet">
/// <item>Pi runtime：本来就只有 Cindy 会写 <c>pi-agent-home</c>，Host 恒为 Cindy；</item>
/// <item>Codex runtime：rollout 在 Cindy 私有 home（<c>codex-home</c> / <c>codex-accounts</c>）即归 Cindy；
/// thread id 命中 owner 库时再用产品的 project_dir 修正项目；</item>
/// <item>Claude Code runtime：transcript 在共享 <c>~/.claude</c>，只能靠
/// <c>sessions.sdk_session_id</c> 命中 owner 库来判定 Cindy 发起，未命中的保持"无宿主"（用户自己
/// 用 CLI）；</item>
/// <item>任何记录的项目键若落在 Cindy 自己的目录（dialogue 的 owners 目录）或 Claude 编码目录形态，
/// 一律覆盖成 "Unknown Project"，不让宿主内部目录冒充用户项目。</item>
/// </list>
/// <para>
/// 每轮对全部记录重算（先清空 Host / override）：增量扫描器跨轮复用记录实例，归属必须由本后处理
/// 权威覆盖，不能只"命中才写"——否则索引变化或库暂时不可读时旧值会残留。项目原值不被改写。
/// </para>
/// </summary>
internal static class CindyHostAttribution
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Claude Code 把 cwd 编码成目录名后的 Cindy 内部路径标记（owners / Host profile）。</summary>
    private static readonly string[] EncodedCindyMarkers =
    [
        "-Cindy-owners-",
        "-CindyGlobal-owners-",
        "-CindyDev-owners-",
        "-xdt-maker-owners-",
        "-CindyHost-",
    ];

    public static List<UsageRecord> Apply(IReadOnlyList<UsageRecord> records, CindyOwnershipIndex index)
    {
        var roots = SafeResolve(index.ResolveUserDataRoots);
        var codexHomes = SafeResolve(index.ResolveCodexHomes);

        foreach (var record in records)
        {
            // 权威重算：清掉上一轮（或适配器）留下的归属结果。
            record.Host = string.Empty;
            record.ProjectPathOverride = null;

            switch (record.Agent)
            {
                case CindyPiAdapter.AgentName:
                    record.Host = CindyOwnershipIndex.HostName;
                    if (index.TryResolve(record.SessionId, out var piEntry) && IsPiKind(piEntry.AgentKind))
                    {
                        ApplyProject(record, piEntry, roots);
                    }

                    NormalizeCindyInternalProject(record, roots);
                    break;

                case ClaudeCodeJsonlLineParser.AgentName:
                    if (index.TryResolve(record.SessionId, out var claudeEntry) && IsClaudeKind(claudeEntry.AgentKind))
                    {
                        record.Host = CindyOwnershipIndex.HostName;
                        ApplyProject(record, claudeEntry, roots);
                    }

                    NormalizeCindyInternalProject(record, roots);
                    break;

                case CodexJsonlFileParser.AgentName:
                    if (TryResolveCodexThreadId(record, out var threadId)
                        && index.TryResolve(threadId, out var codexEntry)
                        && IsCodexKind(codexEntry.AgentKind))
                    {
                        record.Host = CindyOwnershipIndex.HostName;
                        ApplyProject(record, codexEntry, roots);
                    }
                    else if (IsUnderAnyRoot(record.SourceFile, codexHomes))
                    {
                        // 文件就在 Cindy 私有 home 里：归属确定，项目仍按 rollout 的 cwd 推导。
                        record.Host = CindyOwnershipIndex.HostName;
                    }

                    NormalizeCindyInternalProject(record, roots);
                    break;

                default:
                    NormalizeCindyInternalProject(record, roots);
                    break;
            }
        }

        return records as List<UsageRecord> ?? records.ToList();
    }

    private static bool TryResolveCodexThreadId(UsageRecord record, out string threadId)
    {
        if (CodexLogLocator.TryGetRolloutThreadId(record.SourceFile, out threadId))
        {
            return true;
        }

        return CodexLogLocator.TryGetRolloutThreadId(record.SessionId, out threadId);
    }

    private static void ApplyProject(UsageRecord record, CindyOwnershipEntry entry, IReadOnlyList<string> roots)
    {
        if (ResolveProject(entry, roots) is { } project)
        {
            record.ProjectPathOverride = project;
        }
    }

    /// <summary>
    /// 产品项目归属：project_dir（显式项目）优先；dialogue 会话不是项目，回落 Unknown Project；
    /// 其余用 working_dir，但落在 Cindy 自己目录里的（老数据没有 workspace_kind）同样按 dialogue 处理。
    /// </summary>
    private static string? ResolveProject(CindyOwnershipEntry entry, IReadOnlyList<string> roots)
    {
        if (NormalizeProject(entry.ProjectDir) is { } projectDir)
        {
            return projectDir;
        }

        if (string.Equals(entry.WorkspaceKind, "dialogue", StringComparison.OrdinalIgnoreCase))
        {
            return CindyPiAdapter.UnknownProject;
        }

        if (NormalizeProject(entry.WorkingDir) is { } workingDir)
        {
            return IsUnderAnyRoot(workingDir, roots) ? CindyPiAdapter.UnknownProject : workingDir;
        }

        return null;
    }

    private static void NormalizeCindyInternalProject(UsageRecord record, IReadOnlyList<string> roots)
    {
        if (record.ProjectPathOverride is not null)
        {
            return;
        }

        if (IsUnderAnyRoot(record.ProjectPath, roots) || LooksLikeEncodedCindyPath(record.ProjectPath))
        {
            record.ProjectPathOverride = CindyPiAdapter.UnknownProject;
        }
    }

    private static bool LooksLikeEncodedCindyPath(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        foreach (var marker in EncodedCindyMarkers)
        {
            if (path.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderAnyRoot(string? path, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || roots.Count == 0)
        {
            return false;
        }

        var candidate = NormalizeSeparators(path.Trim());
        if (candidate.IndexOf('/') < 0)
        {
            // "Unknown Project" / 编码目录名等非路径形态：不参与前缀判定。
            return false;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalizedRoot = NormalizeSeparators(Path.TrimEndingDirectorySeparator(root.Trim()));
            if (normalizedRoot.Length == 0)
            {
                continue;
            }

            if (candidate.Equals(normalizedRoot, PathComparison)
                || candidate.StartsWith(normalizedRoot + "/", PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeSeparators(string path)
        => path.Replace('\\', '/');

    private static string? NormalizeProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length > 0 ? trimmed : null;
    }

    private static IReadOnlyList<string> SafeResolve(Func<IReadOnlyList<string>> resolver)
    {
        try
        {
            return resolver();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsClaudeKind(string kind)
        => kind is "cc" or "claude-code";

    private static bool IsCodexKind(string kind)
        => string.Equals(kind, "codex", StringComparison.OrdinalIgnoreCase);

    private static bool IsPiKind(string kind)
        => string.Equals(kind, "pi", StringComparison.OrdinalIgnoreCase);
}
