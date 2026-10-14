namespace TokenPulse.Core.Models;

/// <summary>
/// 计价输入的 token 明细（I-003）：与 <see cref="UsageRecord"/> 的 token 字段同构，
/// 便于不带完整记录（如聚合后的 facts 行）直接调用计价。
/// </summary>
public sealed class TokenUsageDetail
{
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    /// <summary>cache_creation 总 token 数（无 5m/1h 细分时全部按 5m 写入价计）。</summary>
    public long CacheCreationTokens { get; init; }

    /// <summary>cache_creation 的 5 分钟 ephemeral 细分：按模型 cache write 价计。</summary>
    public long CacheCreationEphemeral5mTokens { get; init; }

    /// <summary>cache_creation 的 1 小时 ephemeral 细分：按 input × 2 计（蓝本 claude.rs 规则）。</summary>
    public long CacheCreationEphemeral1hTokens { get; init; }

    public long CacheReadTokens { get; init; }

    /// <summary>总 token 数（input + output + cache_creation + cache_read，与 ccusage 口径一致）。</summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    public static TokenUsageDetail FromRecord(UsageRecord record)
    {
        return new TokenUsageDetail
        {
            InputTokens = record.InputTokens,
            OutputTokens = record.OutputTokens,
            CacheCreationTokens = record.CacheCreationTokens,
            CacheCreationEphemeral5mTokens = record.CacheCreationEphemeral5mTokens,
            CacheCreationEphemeral1hTokens = record.CacheCreationEphemeral1hTokens,
            CacheReadTokens = record.CacheReadTokens,
        };
    }
}
