using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Services;

/// <summary>
/// 模型计价器（独立工具，与统计无关）的一次估算结果：按缓存命中率拆分输入后的
/// 分桶 token 数、分桶成本与总价。<see cref="TotalCostUsd"/> 与主管道同一公式
/// （<see cref="PricingEngine.ComputeCostBreakdown"/>，含长上下文分档），分桶值仅作展示。
/// </summary>
/// <param name="FreshInputTokens">未命中缓存的输入 tokens（按普通输入价计）。</param>
/// <param name="CacheReadTokens">命中缓存的输入 tokens（按缓存读价计）。</param>
/// <param name="OutputTokens">输出 tokens。</param>
/// <param name="FreshInputCostUsd">未命中输入的成本。</param>
/// <param name="CacheReadCostUsd">缓存命中的成本。</param>
/// <param name="OutputCostUsd">输出的成本。</param>
/// <param name="TotalCostUsd">总成本（与主管道口径逐位一致）。</param>
/// <param name="BlendedPerMillionUsd">折合每百万 tokens 均价（总成本 / 总 tokens × 1M），便于跨模型比较。</param>
public sealed record ModelCostEstimate(
    long FreshInputTokens,
    long CacheReadTokens,
    long OutputTokens,
    decimal FreshInputCostUsd,
    decimal CacheReadCostUsd,
    decimal OutputCostUsd,
    decimal TotalCostUsd,
    decimal BlendedPerMillionUsd);

/// <summary>
/// 模型计价器（I-CALC 独立工具）：选模型 + 填用量 → 算费用。输入只拆「输入总量 × 缓存命中率」
/// 两桶（命中率部分按缓存读价、其余按输入价），不建模缓存写（估算器语义，与主管道的
/// cache_creation 桶无关）；输出独立一桶。计价公式零复制——一律经
/// <see cref="PricingEngine.ComputeCostBreakdown"/>，与统计/对账同源。
/// </summary>
public static class ModelCostCalculator
{
    /// <summary>
    /// 按命中率把输入总量拆成缓存命中 tokens：四舍五入（AwayFromZero，十进制乘法不引入
    /// 二进制表示误差），命中率钳到 [0,1]，结果不超输入总量。输入非正数返回 0。
    /// </summary>
    public static long SplitCacheRead(long inputTokens, decimal cacheHitRate)
    {
        if (inputTokens <= 0)
        {
            return 0;
        }

        var rate = Math.Clamp(cacheHitRate, 0m, 1m);
        var cacheRead = (long)Math.Round(inputTokens * rate, MidpointRounding.AwayFromZero);
        return Math.Clamp(cacheRead, 0, inputTokens);
    }

    /// <summary>
    /// 计算一次估算。<paramref name="cacheHitRate"/> 是 0–1 小数（UI 层负责百分号折算）；
    /// token 数按非负钳制。<paramref name="model"/> 透传给计价公式（速度倍率按模型名解析）。
    /// </summary>
    public static ModelCostEstimate Calculate(
        ModelPriceEntry entry,
        long inputTokens,
        decimal cacheHitRate,
        long outputTokens,
        string? model = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var cacheRead = SplitCacheRead(inputTokens, cacheHitRate);
        var freshInput = Math.Clamp(inputTokens - cacheRead, 0, long.MaxValue);
        var output = Math.Max(0, outputTokens);

        var breakdown = PricingEngine.ComputeCostBreakdown(
            new TokenUsageDetail
            {
                InputTokens = freshInput,
                CacheReadTokens = cacheRead,
                OutputTokens = output,
            },
            entry,
            model: model);

        // 饱和加：三桶各自 ≤ input/output 域内已钳非负，Sum 防御性取饱和式（与引擎同款防御）。
        var totalTokens = SaturatingAdd(SaturatingAdd(freshInput, cacheRead), output);
        var blended = totalTokens > 0 ? breakdown.TotalCostUsd * 1_000_000m / totalTokens : 0m;

        return new ModelCostEstimate(
            freshInput,
            cacheRead,
            output,
            breakdown.InputCostUsd,
            breakdown.CacheReadCostUsd,
            breakdown.OutputCostUsd,
            breakdown.TotalCostUsd,
            blended);
    }

    private static long SaturatingAdd(long left, long right)
    {
        var sum = unchecked(left + right);
        return sum < 0 ? long.MaxValue : sum;
    }
}
