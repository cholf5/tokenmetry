using System.Buffers;
using System.Text;
using System.Text.Json;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Infrastructure.QwenCode;

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

    /// <summary>
    /// 与蓝本 entry_id 一致用 JSON 数组序列化（serde_json 转义同规则，字段含逗号/冒号不串键）。
    /// 直接用 Utf8JsonWriter 逐元素写出而非 JsonSerializer：object[] 多态元素在 JSON 源生成下
    /// 无法静态定型（Native AOT 适配，P-004），而本键全部是基元类型，写出结果与反射序列化
    /// （默认选项）字节一致——字符串走同一默认转义器，数值同为不带小数点的整数字面量。
    /// </summary>
    internal static string BuildKey(UsageRecord record)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(record.SessionId);
            writer.WriteNumberValue(record.Timestamp.ToUnixTimeMilliseconds());
            writer.WriteStringValue(record.Model ?? string.Empty);
            writer.WriteNumberValue(record.InputTokens);
            writer.WriteNumberValue(record.OutputTokens);
            writer.WriteNumberValue(record.CacheReadTokens);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
