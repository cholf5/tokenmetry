using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.ClaudeCodeJsonl;

/// <summary>
/// Claude usage 记录的消息级去重，算法与 ccusage-tauri claude.rs dedupe_entries 一致：
/// - 精确 key 为 (message.id, requestId-or-空串哨兵)：auto 模式下一条 assistant 消息拆成多行流式
///   usage（共享 message id 且 request id 为 null），这些部分行折叠为一条，保留累计 token 最大值；
/// - sidechain 回放复用父消息 id 但 request id 不同：同 message id 下任一方为 sidechain 即视为重复，
///   非 sidechain（父记录）优先；
/// - 其余情况下优先保留 token 总量更高、再优先模型名带 -fast 后缀的记录；
/// - 无 message id 的记录不参与去重。
/// </summary>
public class ClaudeUsageRecordDeduper
{
    public List<UsageRecord> Dedupe(IEnumerable<UsageRecord> records)
    {
        var deduped = new List<UsageRecord>();
        var exactIndex = new Dictionary<(string MessageId, string RequestId), int>();
        var messageIndex = new Dictionary<string, List<int>>();

        foreach (var entry in records)
        {
            var duplicateIndex = FindDuplicateIndex(entry, exactIndex, messageIndex, deduped);
            if (duplicateIndex is { } index)
            {
                if (ShouldReplace(entry, deduped[index]))
                {
                    deduped[index] = entry;
                    RebuildIndexes(deduped, exactIndex, messageIndex);
                }
            }
            else
            {
                AddToIndexes(entry, deduped.Count, exactIndex, messageIndex);
                deduped.Add(entry);
            }
        }

        return deduped;
    }

    private static int? FindDuplicateIndex(
        UsageRecord entry,
        Dictionary<(string MessageId, string RequestId), int> exactIndex,
        Dictionary<string, List<int>> messageIndex,
        List<UsageRecord> deduped)
    {
        if (entry.MessageId is not { } messageId)
        {
            return null;
        }

        // 与 claude.rs 一致：normalize 阶段已拒绝空 requestId，空串哨兵不会与真实值冲突。
        var requestKey = entry.RequestId ?? string.Empty;
        if (exactIndex.TryGetValue((messageId, requestKey), out var exact))
        {
            return exact;
        }

        if (!messageIndex.TryGetValue(messageId, out var indexes))
        {
            return null;
        }

        foreach (var index in indexes)
        {
            if (entry.IsSidechain || deduped[index].IsSidechain)
            {
                return index;
            }
        }

        return null;
    }

    private static bool ShouldReplace(UsageRecord candidate, UsageRecord existing)
    {
        if (candidate.IsSidechain != existing.IsSidechain)
        {
            return !candidate.IsSidechain;
        }

        var candidateTotal = candidate.TotalTokens;
        var existingTotal = existing.TotalTokens;
        if (candidateTotal != existingTotal)
        {
            return candidateTotal > existingTotal;
        }

        return HasFastSuffix(candidate) && !HasFastSuffix(existing);
    }

    private static bool HasFastSuffix(UsageRecord entry)
        => entry.Model is { } model && model.EndsWith("-fast", StringComparison.Ordinal);

    private static void RebuildIndexes(
        List<UsageRecord> entries,
        Dictionary<(string MessageId, string RequestId), int> exactIndex,
        Dictionary<string, List<int>> messageIndex)
    {
        exactIndex.Clear();
        messageIndex.Clear();
        for (var index = 0; index < entries.Count; index++)
        {
            AddToIndexes(entries[index], index, exactIndex, messageIndex);
        }
    }

    private static void AddToIndexes(
        UsageRecord entry,
        int index,
        Dictionary<(string MessageId, string RequestId), int> exactIndex,
        Dictionary<string, List<int>> messageIndex)
    {
        if (entry.MessageId is not { } messageId)
        {
            return;
        }

        if (!messageIndex.TryGetValue(messageId, out var indexes))
        {
            indexes = new List<int>();
            messageIndex[messageId] = indexes;
        }

        indexes.Add(index);
        exactIndex[(messageId, entry.RequestId ?? string.Empty)] = index;
    }
}
