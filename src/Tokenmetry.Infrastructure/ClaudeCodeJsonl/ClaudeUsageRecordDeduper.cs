using Tokenmetry.Core.Models;

namespace Tokenmetry.Infrastructure.ClaudeCodeJsonl;

/// <summary>
/// Claude usage 记录的消息级去重，键口径对齐 ccusage 20.1.x
/// （rust/adapters/claude daily/response_chain.rs + dedupe.rs）：
/// - 有 requestId：键 = (messageId, requestId)，跨会话折叠（request id 唯一标识一次响应）；
/// - 无 requestId：基础键 = (messageId, sessionId, 时间戳毫秒)，不同时间戳默认各自独立
///   （网关会把同一个占位 id 复用到多次响应上，只有时间戳能区分）；
/// - 无 requestId 且适配器给出了 <see cref="UsageRecord.ResponseChainIndex"/>：响应链键 =
///   (messageId, sessionId, sourceFile, 链索引)，跨时间戳折叠同一次响应按 content block
///   写出的多行（链由 <see cref="ClaudeResponseChainTracker"/> 沿 uuid/parentUuid 与
///   tool_use↔tool_result 归属证明，且签名一致）；
/// - message id 的拼写（msg_ / resp_ 前缀）不参与判定；
/// - sidechain 回放兜底：同一 messageId 在同一会话内，任一方为 sidechain 即视为重复，非 sidechain
///   （父记录）优先——跨会话不再折叠（网关在不同会话复用同一 id 是两次请求）；
/// - 替换规则：优先非 sidechain，其次 token 总量更高，再次模型名带 -fast 后缀；
/// - 无 message id 的记录不参与去重。
/// </summary>
public class ClaudeUsageRecordDeduper
{
    public List<UsageRecord> Dedupe(IEnumerable<UsageRecord> records)
    {
        var deduped = new List<UsageRecord?>();
        var exactIndex = new Dictionary<DedupeKey, int>();
        var responseIndex = new Dictionary<ResponseKey, int>();
        var messageIndex = new Dictionary<(string MessageId, string SessionId), List<int>>();

        foreach (var entry in records)
        {
            var duplicateIndex = FindDuplicateIndex(entry, exactIndex, responseIndex, messageIndex, deduped);
            if (duplicateIndex is { } index)
            {
                if (ShouldReplace(entry, deduped[index]!))
                {
                    var replaced = deduped[index]!;
                    deduped[index] = entry;
                    // 只改被替换那一条的索引。全表重建是 O(n)，每次替换都做就变成
                    // O(替换次数 × n)，历史量大时去重会把整轮采集卡住。
                    RemoveFromIndexes(replaced, index, exactIndex, responseIndex, messageIndex);
                    AddToIndexes(entry, index, exactIndex, responseIndex, messageIndex);
                }
            }
            else
            {
                AddToIndexes(entry, deduped.Count, exactIndex, responseIndex, messageIndex);
                deduped.Add(entry);
            }
        }

        return deduped.Count == 0 ? new List<UsageRecord>() : deduped.OfType<UsageRecord>().ToList();
    }

    private static int? FindDuplicateIndex(
        UsageRecord entry,
        Dictionary<DedupeKey, int> exactIndex,
        Dictionary<ResponseKey, int> responseIndex,
        Dictionary<(string MessageId, string SessionId), List<int>> messageIndex,
        List<UsageRecord?> deduped)
    {
        if (entry.MessageId is not { } messageId)
        {
            return null;
        }

        var exact = exactIndex.TryGetValue(BuildExactKey(entry, messageId), out var exactHit) ? exactHit : (int?)null;
        var response = TryBuildResponseKey(entry, messageId, out var responseKey)
                       && responseIndex.TryGetValue(responseKey, out var responseHit)
            ? responseHit
            : (int?)null;

        if (exact is { } exactSlot && response is { } responseSlot && exactSlot != responseSlot)
        {
            // 两条键指向不同的存活记录：它们其实是同一次响应（例如同一 block 的重复写入与
            // 同响应其它 block 的链键），先把两个槽位并成一个，再按普通重复处理。
            return MergeSlots(exactSlot, responseSlot, deduped, exactIndex, responseIndex, messageIndex);
        }

        if (exact is not null)
        {
            return exact;
        }

        if (response is not null)
        {
            return response;
        }

        if (!messageIndex.TryGetValue((messageId, entry.SessionId ?? string.Empty), out var indexes))
        {
            return null;
        }

        foreach (var index in indexes)
        {
            if (entry.IsSidechain || deduped[index]!.IsSidechain)
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// 合并两个指向同一次响应的槽位：按替换规则选出胜者，把败者槽位所有索引键改指胜者槽位，
    /// 败者槽位置空（最终从结果中过滤）。
    /// </summary>
    private static int MergeSlots(
        int first,
        int second,
        List<UsageRecord?> deduped,
        Dictionary<DedupeKey, int> exactIndex,
        Dictionary<ResponseKey, int> responseIndex,
        Dictionary<(string MessageId, string SessionId), List<int>> messageIndex)
    {
        var keep = ShouldReplace(deduped[second]!, deduped[first]!) ? second : first;
        var drop = keep == first ? second : first;
        RemapIndexes(deduped[drop]!, drop, keep, exactIndex, responseIndex, messageIndex);
        deduped[drop] = null;
        return keep;
    }

    /// <summary>把 <paramref name="entry"/> 名下仍指向 <paramref name="from"/> 的索引键改指 <paramref name="to"/>。</summary>
    private static void RemapIndexes(
        UsageRecord entry,
        int from,
        int to,
        Dictionary<DedupeKey, int> exactIndex,
        Dictionary<ResponseKey, int> responseIndex,
        Dictionary<(string MessageId, string SessionId), List<int>> messageIndex)
    {
        if (entry.MessageId is not { } messageId)
        {
            return;
        }

        var exactKey = BuildExactKey(entry, messageId);
        if (exactIndex.TryGetValue(exactKey, out var exactSlot) && exactSlot == from)
        {
            exactIndex[exactKey] = to;
        }

        if (TryBuildResponseKey(entry, messageId, out var responseKey)
            && responseIndex.TryGetValue(responseKey, out var responseSlot)
            && responseSlot == from)
        {
            responseIndex[responseKey] = to;
        }

        var sessionId = entry.SessionId ?? string.Empty;
        if (messageIndex.TryGetValue((messageId, sessionId), out var indexes))
        {
            indexes.RemoveAll(index => index == from);
            if (!indexes.Contains(to))
            {
                indexes.Add(to);
            }

            if (indexes.Count == 0)
            {
                messageIndex.Remove((messageId, sessionId));
            }
        }
    }

    /// <summary>有 requestId 时只认 (messageId, requestId)；无 requestId 时按 (messageId, sessionId, 时间戳) 区分。</summary>
    private static DedupeKey BuildExactKey(UsageRecord entry, string messageId)
    {
        if (entry.RequestId is { } requestId)
        {
            return new DedupeKey(messageId, requestId, string.Empty, 0);
        }

        return new DedupeKey(
            messageId,
            string.Empty,
            entry.SessionId ?? string.Empty,
            entry.Timestamp.ToUnixTimeMilliseconds());
    }

    /// <summary>响应链键只在无 requestId 且适配器已跟踪到链归属时生效（ccusage requestless_response_hash 同口径）。</summary>
    private static bool TryBuildResponseKey(UsageRecord entry, string messageId, out ResponseKey key)
    {
        if (entry.RequestId is null && entry.ResponseChainIndex is { } chainIndex)
        {
            key = new ResponseKey(messageId, entry.SessionId ?? string.Empty, entry.SourceFile, chainIndex);
            return true;
        }

        key = default;
        return false;
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

    private static void RemoveFromIndexes(
        UsageRecord entry,
        int index,
        Dictionary<DedupeKey, int> exactIndex,
        Dictionary<ResponseKey, int> responseIndex,
        Dictionary<(string MessageId, string SessionId), List<int>> messageIndex)
    {
        if (entry.MessageId is not { } messageId)
        {
            return;
        }

        var exactKey = BuildExactKey(entry, messageId);
        if (exactIndex.TryGetValue(exactKey, out var exactSlot) && exactSlot == index)
        {
            exactIndex.Remove(exactKey);
        }

        if (TryBuildResponseKey(entry, messageId, out var responseKey)
            && responseIndex.TryGetValue(responseKey, out var responseSlot)
            && responseSlot == index)
        {
            responseIndex.Remove(responseKey);
        }

        var sessionId = entry.SessionId ?? string.Empty;
        if (!messageIndex.TryGetValue((messageId, sessionId), out var indexes))
        {
            return;
        }

        indexes.Remove(index);
        if (indexes.Count == 0)
        {
            messageIndex.Remove((messageId, sessionId));
        }
    }

    private static void AddToIndexes(
        UsageRecord entry,
        int index,
        Dictionary<DedupeKey, int> exactIndex,
        Dictionary<ResponseKey, int> responseIndex,
        Dictionary<(string MessageId, string SessionId), List<int>> messageIndex)
    {
        if (entry.MessageId is not { } messageId)
        {
            return;
        }

        var sessionId = entry.SessionId ?? string.Empty;
        if (!messageIndex.TryGetValue((messageId, sessionId), out var indexes))
        {
            indexes = new List<int>();
            messageIndex[(messageId, sessionId)] = indexes;
        }

        // 槽位合并后同一个索引可能已在该桶里（合并时把败者槽位的键改指胜者），避免重复追加。
        if (!indexes.Contains(index))
        {
            indexes.Add(index);
        }

        exactIndex[BuildExactKey(entry, messageId)] = index;
        if (TryBuildResponseKey(entry, messageId, out var responseKey))
        {
            responseIndex[responseKey] = index;
        }
    }

    private readonly record struct DedupeKey(
        string MessageId,
        string RequestId,
        string SessionId,
        long TimestampMs);

    private readonly record struct ResponseKey(
        string MessageId,
        string SessionId,
        string SourceFile,
        int ChainIndex);
}
