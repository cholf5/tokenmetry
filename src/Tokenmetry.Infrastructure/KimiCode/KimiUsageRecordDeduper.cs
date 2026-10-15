using Tokenmetry.Core.Models;

namespace Tokenmetry.Infrastructure.KimiCode;

/// <summary>
/// Kimi usage 记录的内容键去重（蓝本 ../ccusage/rust/adapters/kimi/src/parser.rs kimi_entry_key 的
/// 移植，I-013；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 去重键为冒号连接的 [sessionId, message_id, timestamp, model, input, output, cache_creation,
/// cache_read, extra_total_tokens] 文本，跨文件 first-wins——按文件发现顺序首个出现者保留（蓝本
/// load_entries 的 seen.insert 同口径）。Kimi 无 Claude 式 message.id 全局去重语义（旧格式的
/// message_id 只是键的一个分量），故作为独立去重器注入共享扫描引擎。
/// 与蓝本的等价性：桌面统一记录已把 extra_total_tokens 并入 OutputTokens（仓库口径），故键为
/// 8 分量 (session, message_id, 时间戳毫秒, model, input, output[含 extra], cache_creation,
/// cache_read)；时间戳取毫秒时刻而非蓝本的格式化文本。蓝本冒号连接无转义（同键碰撞面与蓝本一致）。
/// </summary>
public class KimiUsageRecordDeduper
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

    internal static string BuildKey(UsageRecord record)
        => string.Join(':',
            record.SessionId,
            record.MessageId ?? string.Empty,
            record.Timestamp.ToUnixTimeMilliseconds(),
            record.Model ?? string.Empty,
            record.InputTokens,
            record.OutputTokens,
            record.CacheCreationTokens,
            record.CacheReadTokens);
}
