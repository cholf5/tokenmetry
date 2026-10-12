namespace CCUsage.Core.Models;

/// <summary>
/// 单个模型的计价条目（I-003）。价格统一为 USD / 百万 tokens（models.dev 的 cost 字段口径）。
/// 远程条目（models.dev 拉取）只做精确匹配；内置快照条目可带 <see cref="MatchPattern"/>
/// 家族模式（如 "claude-sonnet-4"），用于命中带日期后缀的历史模型名。
/// </summary>
public sealed class ModelPriceEntry
{
    /// <summary>缓存写入（5m ephemeral）缺失时按 input × <see cref="DefaultCacheWriteMultiplier"/> 计（蓝本 pricing.rs 同规则）。</summary>
    public const decimal DefaultCacheWriteMultiplier = 1.25m;

    /// <summary>缓存读取缺失时按 input × <see cref="DefaultCacheReadMultiplier"/> 计。</summary>
    public const decimal DefaultCacheReadMultiplier = 0.1m;

    /// <summary>models.dev 的模型 id（如 "claude-opus-4-8"）；用户覆盖条目为覆盖的模型名。</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>provider id（如 "anthropic"）；用户覆盖条目为 "user"。</summary>
    public string Provider { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    /// <summary>
    /// 家族匹配模式（仅内置快照使用）：候选模型名（小写化）包含该串即视为命中，
    /// 多个命中时取最长模式。null 表示只做精确匹配。
    /// </summary>
    public string? MatchPattern { get; init; }

    public decimal InputPerMillion { get; init; }

    public decimal OutputPerMillion { get; init; }

    /// <summary>cache_creation（5m ephemeral）单价，USD / 百万 tokens。</summary>
    public decimal CacheWritePerMillion { get; init; }

    /// <summary>cache_read 单价，USD / 百万 tokens。</summary>
    public decimal CacheReadPerMillion { get; init; }

    /// <summary>
    /// 长上下文分档阈值（I-024 观察项：models.dev cost.tiers 的 context 档，蓝本 pricing.rs
    /// long_context_tier 取最小阈值）；null = 无分档。请求上下文（input + cache 各桶）超过阈值时
    /// 全部桶整体切换到分档单价（蓝本 cost.rs 两段式，非边际分段）；分档缺某项单价时回落基础档。
    /// </summary>
    public long? LongContextThreshold { get; init; }

    /// <summary>长上下文分档的 input 单价；null 回落 <see cref="InputPerMillion"/>。</summary>
    public decimal? InputAboveThresholdPerMillion { get; init; }

    /// <summary>长上下文分档的 output 单价；null 回落 <see cref="OutputPerMillion"/>。</summary>
    public decimal? OutputAboveThresholdPerMillion { get; init; }

    /// <summary>长上下文分档的 cache_creation（5m）单价；null 回落 <see cref="CacheWritePerMillion"/>。</summary>
    public decimal? CacheWriteAboveThresholdPerMillion { get; init; }

    /// <summary>长上下文分档的 cache_read 单价；null 回落 <see cref="CacheReadPerMillion"/>。</summary>
    public decimal? CacheReadAboveThresholdPerMillion { get; init; }

    /// <summary>展示用键："provider/modelId"（provider 为空时仅 modelId）。</summary>
    public string ToKey()
    {
        return string.IsNullOrEmpty(Provider) ? ModelId : $"{Provider}/{ModelId}";
    }
}

/// <summary>
/// models.dev（或内置快照）一次成功拉取得到的完整价格表。
/// 以不可变引用整体替换（volatile 读），查询路径永远不阻塞、不触发网络。
/// </summary>
public sealed class RemotePricingSnapshot
{
    /// <summary>拉取成功时间（TTL 判断基准）。</summary>
    public DateTimeOffset FetchedAt { get; init; }

    public IReadOnlyList<ModelPriceEntry> Entries { get; init; } = Array.Empty<ModelPriceEntry>();
}
