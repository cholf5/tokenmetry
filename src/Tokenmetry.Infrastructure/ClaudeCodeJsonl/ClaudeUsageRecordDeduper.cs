using Tokenmetry.Core.Models;

namespace Tokenmetry.Infrastructure.ClaudeCodeJsonl;

/// <summary>
/// Claude usage 记录的消息级去重，键口径对齐 ccusage 20.0.45
/// （adapters/claude/src/lib.rs `usage_dedupe_hash` + `sidechain_replay_dedupe_hash`）：
/// - 有 requestId：键 = (messageId, requestId)，跨会话折叠（request id 唯一标识一次响应）；
/// - 无 requestId：键 = (messageId, sessionId)，且 messageId 不以 `msg_` / `resp_` 开头时再并入
///   时间戳毫秒（网关会把同一个占位 id 复用到多次响应上，只有时间戳能区分）；
/// - sidechain 回放兜底：同一 messageId 在同一会话内，任一方为 sidechain 即视为重复，非 sidechain
///   （父记录）优先——跨会话不再折叠（网关在不同会话复用同一 id 是两次请求）；
/// - 替换规则：优先非 sidechain，其次 token 总量更高，再次模型名带 -fast 后缀；
/// - 无 message id 的记录不参与去重。
/// </summary>
public class ClaudeUsageRecordDeduper
{
    public List<UsageRecord> Dedupe(IEnumerable<UsageRecord> records)
    {
        var deduped = new List<UsageRecord>();
        var exactIndex = new Dictionary<DedupeKey, int>();
        var messageIndex = new Dictionary<(string MessageId, string SessionId), List<int>>();

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
        Dictionary<DedupeKey, int> exactIndex,
        Dictionary<(string MessageId, string SessionId), List<int>> messageIndex,
        List<UsageRecord> deduped)
    {
        if (entry.MessageId is not { } messageId)
        {
            return null;
        }

        if (exactIndex.TryGetValue(BuildExactKey(entry, messageId), out var exact))
        {
            return exact;
        }

        if (!messageIndex.TryGetValue((messageId, entry.SessionId ?? string.Empty), out var indexes))
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

    /// <summary>与蓝本 usage_dedupe_hash 一致：requestId 存在则只认 (messageId, requestId)。</summary>
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
            IsAnthropicMessageId(messageId) ? 0 : entry.Timestamp.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// 与蓝本 is_anthropic_message_id 一致：`msg_`（Anthropic 真实消息 id）与 `resp_`
    /// （OpenAI Responses API id）由 Claude Code 每 content block 写一行，跨时间戳仍是同一条响应。
    /// </summary>
    private static bool IsAnthropicMessageId(string messageId)
        => messageId.StartsWith("msg_", StringComparison.Ordinal)
           || messageId.StartsWith("resp_", StringComparison.Ordinal);

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
        Dictionary<DedupeKey, int> exactIndex,
        Dictionary<(string MessageId, string SessionId), List<int>> messageIndex)
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
        Dictionary<DedupeKey, int> exactIndex,
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

        indexes.Add(index);
        exactIndex[BuildExactKey(entry, messageId)] = index;
    }

    private readonly record struct DedupeKey(
        string MessageId,
        string RequestId,
        string SessionId,
        long TimestampMs);
}
