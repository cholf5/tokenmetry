namespace CCUsage.Infrastructure.JsonSource;

/// <summary>
/// 蓝本 TokenUsageRaw 的四桶载体（I-010 / I-014 / I-015 共用；桌面统一记录模型无 speed /
/// cache_creation 细分字段，故仅保留四桶）。计数域为 long——蓝本为 u64，读取层把 u64 溢出钳到
/// long.MaxValue（saturating；I-017 偏差⑥同款），溢出兜底运算全部走 saturating 而非蓝本的
/// release-wrapping（token 计数现实值远小于边界，结果一致）。
/// </summary>
public readonly record struct RawTokenUsage(
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens)
{
    public long TotalUsageTokens()
        => SaturatingAdd(SaturatingAdd(SaturatingAdd(InputTokens, OutputTokens), CacheCreationTokens), CacheReadTokens);

    public static long SaturatingAdd(long left, long right)
    {
        var sum = left + right;
        if (left >= 0 == right >= 0 && left >= 0 != sum >= 0)
        {
            // 同号相加翻转符号：溢出，按操作数符号钳到边界。
            return left >= 0 ? long.MaxValue : long.MinValue;
        }

        return sum;
    }
}

/// <summary>
/// 蓝本 crates/ccusage-core/src/utils.rs apply_total_token_fallback 的移植（I-010 / I-014 / I-015
/// 共用）：已知四桶 + extra 不足 total 时把缺口补入——output 为 0 优先补 output，否则并入 extra
/// （reasoning 展示余量）。
/// </summary>
public static class TokenUsageFallback
{
    public static (RawTokenUsage Usage, long ExtraTokens) Apply(
        RawTokenUsage usage, long extraTokens, long totalTokens)
    {
        var knownTokens = usage.TotalUsageTokens().SaturatingAddClamped(extraTokens);
        var missingTokens = totalTokens > knownTokens ? totalTokens - knownTokens : 0;
        if (missingTokens == 0)
        {
            return (usage, extraTokens);
        }

        if (usage.OutputTokens == 0)
        {
            return (usage with { OutputTokens = missingTokens }, extraTokens);
        }

        return (usage, extraTokens.SaturatingAddClamped(missingTokens));
    }
}

/// <summary>saturating 加法助手（long 域，I-017 口径）。</summary>
internal static class SaturatingMathExtensions
{
    public static long SaturatingAddClamped(this long left, long right) => RawTokenUsage.SaturatingAdd(left, right);
}
