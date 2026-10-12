using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.PiAgent;

/// <summary>父子重放关系（蓝本 ParentReplay）：父文件下标 + 子会话 fork 时刻。</summary>
public sealed record PiParentReplay(int ParentIndex, DateTimeOffset ForkTimestamp);

/// <summary>
/// 重放抑制计划（蓝本 PiReplayPlan 逐条移植，I-019；蓝本许可：MIT License，Copyright (c) 2025
/// ryoppippi）：对同批发现的会话文件建立「子 → 父」血缘，仅当子会话整条链路血缘有效时，按父文件
/// （树形活动分支 / 线性物理序）中 fork 时刻之前的有效 usage 签名序列，抑制子文件<b>前缀</b>中完全
/// 一致的部分。缺失头 / 头缺时间戳 / parentSession 畸形 / 父不在同批 / 自引用 / 环 → 血缘无效
/// （fail-open：不抑制，原始记录保留）。
/// </summary>
public sealed class PiReplayPlan
{
    private readonly Dictionary<int, int> _skipByFile;

    private PiReplayPlan(Dictionary<int, int> skipByFile)
    {
        _skipByFile = skipByFile;
    }

    /// <summary>子文件应跳过的前缀条目数（蓝本 skip_prefix）。</summary>
    public int SkipPrefix(int fileIndex)
    {
        return _skipByFile.TryGetValue(fileIndex, out var skip) ? skip : 0;
    }

    /// <summary>构建计划（蓝本 PiReplayPlan::new）。</summary>
    public static PiReplayPlan Build(IReadOnlyList<string> files, IReadOnlyList<PiSessionData?> loaded)
    {
        var filesByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < files.Count; index++)
        {
            var normalized = NormalizePath(files[index]);
            if (!filesByPath.ContainsKey(normalized))
            {
                filesByPath[normalized] = index;
            }
        }

        var invalidLineage = new HashSet<int>();
        for (var index = 0; index < loaded.Count; index++)
        {
            var header = loaded[index]?.Header;
            if (header is null || header.Timestamp is null || header.ParentSessionIsMalformed)
            {
                invalidLineage.Add(index);
            }
        }

        var parentByChild = new Dictionary<int, PiParentReplay>();
        for (var childIndex = 0; childIndex < loaded.Count; childIndex++)
        {
            var header = loaded[childIndex]?.Header;
            if (header?.ParentSession is not { } parentPath || header.Timestamp is not { } forkTimestamp)
            {
                continue;
            }

            var parentIndex = ResolveParentIndex(parentPath, files[childIndex], filesByPath);
            if (parentIndex is null)
            {
                invalidLineage.Add(childIndex);
                continue;
            }

            if (parentIndex == childIndex)
            {
                invalidLineage.Add(childIndex);
                continue;
            }

            parentByChild[childIndex] = new PiParentReplay(parentIndex.Value, forkTimestamp);
        }

        var skipByFile = new Dictionary<int, int>();
        foreach (var (childIndex, replay) in parentByChild)
        {
            if (!HasValidLineage(childIndex, parentByChild, invalidLineage))
            {
                continue;
            }

            var parent = loaded.Count > replay.ParentIndex ? loaded[replay.ParentIndex] : null;
            var child = loaded[childIndex];
            if (parent is null || child is null)
            {
                continue;
            }

            if (parent.MatchingReplayPrefix(child, replay.ForkTimestamp) is { } matched && matched > 0)
            {
                skipByFile[childIndex] = matched;
            }
        }

        return new PiReplayPlan(skipByFile);
    }

    /// <summary>血缘链有效性（蓝本 has_valid_lineage）：沿父链上行，链上任一节点无效或成环即假；
    /// 链终根亦须有效。</summary>
    private static bool HasValidLineage(
        int childIndex,
        Dictionary<int, PiParentReplay> parentByChild,
        HashSet<int> invalidLineage)
    {
        var visited = new HashSet<int>();
        var current = childIndex;
        while (parentByChild.TryGetValue(current, out var replay))
        {
            if (invalidLineage.Contains(current) || !visited.Add(current))
            {
                return false;
            }

            current = replay.ParentIndex;
        }

        return !invalidLineage.Contains(current);
    }

    /// <summary>父文件下标解析（蓝本 resolve_parent_index）：规范化绝对路径直查；相对路径按子文件所在
    /// 目录拼接后查。</summary>
    private static int? ResolveParentIndex(
        string parentPath, string childPath, Dictionary<string, int> filesByPath)
    {
        var normalizedParent = NormalizePath(parentPath);
        if (filesByPath.TryGetValue(normalizedParent, out var parentIndex))
        {
            return parentIndex;
        }

        if (!Path.IsPathRooted(parentPath))
        {
            var childDirectory = Path.GetDirectoryName(Path.GetFullPath(childPath));
            if (childDirectory is not null)
            {
                var joined = NormalizePath(Path.Combine(childDirectory, parentPath));
                if (filesByPath.TryGetValue(joined, out var relativeIndex))
                {
                    return relativeIndex;
                }
            }
        }

        return null;
    }

    /// <summary>路径规范化（蓝本 normalize_path）：绝对化 + 折叠 "." / ".." 段（不触文件系统、
    /// 不解析符号链接，Path.GetFullPath 同语义）。</summary>
    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path);
    }
}
