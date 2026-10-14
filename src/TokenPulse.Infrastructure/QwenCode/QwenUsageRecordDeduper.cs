using System.Text.Json;
using TokenPulse.Core.Models;

namespace TokenPulse.Infrastructure.QwenCode;

/// <summary>
/// Qwen usage 记录的内容键去重（蓝本 ../ccusage/rust/adapters/qwen/src/parser.rs entry_id 的移植，I-011；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 去重键为 JSON 数组 [sessionId, timestamp, model, input, output, cache_read, extra_total_tokens] 的
/// serde_json 序列化文本，跨文件 first-wins——按文件发现顺序首个出现者保留（蓝本 load_entries 的
/// seen.insert(entry_id(&amp;entry)) 同口径）。Qwen 日志无 message id，与 Claude 的 MessageId/RequestId
/// 去重语义不同，故作为独立去重器注入共享扫描引擎。
/// 与蓝本的等价性：桌面统一记录已把 extra_total_tokens 并入 OutputTokens（蓝本 billable 口径），
/// 且 cache_creation 恒 0，故键取 (session, 时间戳毫秒, model, input, output[含 extra], cache_read)；
/// 时间戳取毫秒时刻而非蓝本的原始文本——同刻不同 RFC3339 文本（如 Z 与 +00:00）在蓝本算两条、
/// 此处算一条（见卡片偏差清单）。
/// </summary>
public class QwenUsageRecordDeduper
{
    public IReadOnlyList<UsageRecord> Dedupe(IEnumerable<UsageRecord> records)
    {
        var deduped = new List<UsageRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (seen.Add(BuildKey(record)))
            {
                deduped.Add(record);
            }
        }

        return deduped;
    }

    /// <summary>与蓝本 entry_id 一致用 JSON 数组序列化（serde_json 转义同规则，字段含逗号/冒号不串键）。</summary>
    internal static string BuildKey(UsageRecord record)
        => JsonSerializer.Serialize(new object[]
        {
            record.SessionId,
            record.Timestamp.ToUnixTimeMilliseconds(),
            record.Model ?? string.Empty,
            record.InputTokens,
            record.OutputTokens,
            record.CacheReadTokens,
        });
}
