namespace TokenPulse.Infrastructure.ZCodeSqlite;

/// <summary>
/// ZCode 账本 model_usage 行的原始读取形态（蓝本 zcode/src/parser.rs ZcodeUsageRow）。
/// 各 token 计数按蓝本为 u64：出现负值视为读取异常行（与蓝本 i64→u64 转换失败跳过该行一致）。
/// </summary>
public sealed record ZCodeUsageRow(
    string Id,
    string SessionId,
    long StartedAt,
    string ModelId,
    string? ProviderId,
    long InputTokens,
    long OutputTokens,
    long CacheCreationInputTokens,
    long CacheReadInputTokens,
    long ComputedTotalTokens,
    string? Directory);
