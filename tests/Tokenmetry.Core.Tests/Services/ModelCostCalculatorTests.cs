namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

/// <summary>
/// 模型计价器（I-CALC 独立工具）单测：命中率拆分、分桶成本、长上下文分档跟随、
/// 输入钳制与折合均价。计价公式本身由 PricingEngine 侧测试与对账 golden 把守，
/// 这里只验证计价器的拆分语义与"公式同源"（与 ComputeCostBreakdown 直算结果逐位一致）。
/// </summary>
public class ModelCostCalculatorTests
{
    private static ModelPriceEntry Entry(
        decimal input = 3m,
        decimal output = 15m,
        decimal cacheRead = 0.3m,
        long? threshold = null,
        decimal? inputAbove = null,
        decimal? outputAbove = null,
        decimal? cacheReadAbove = null)
    {
        return new ModelPriceEntry
        {
            ModelId = "test-model",
            Provider = "test",
            InputPerMillion = input,
            OutputPerMillion = output,
            CacheReadPerMillion = cacheRead,
            LongContextThreshold = threshold,
            InputAboveThresholdPerMillion = inputAbove,
            OutputAboveThresholdPerMillion = outputAbove,
            CacheReadAboveThresholdPerMillion = cacheReadAbove,
        };
    }

    // ── SplitCacheRead ──

    [Theory]
    [InlineData(1_000_000L, 0.80, 800_000L)]
    [InlineData(2_000_000_000L, 0.55, 1_100_000_000L)]
    [InlineData(10L, 0.55, 6L)] // AwayFromZero：5.5 → 6
    [InlineData(10L, 0.54, 5L)]
    [InlineData(1_000L, 2.0, 1_000L)]  // 命中率钳到 1
    [InlineData(1_000L, -1.0, 0L)]     // 负命中率钳到 0
    [InlineData(0L, 0.5, 0L)]          // 无输入即无命中
    [InlineData(-5L, 0.5, 0L)]         // 负输入防御
    public void SplitCacheRead_splits_input_by_rate(long input, double rate, long expected)
    {
        ModelCostCalculator.SplitCacheRead(input, (decimal)rate).Should().Be(expected);
    }

    // ── Calculate：分桶成本与折合均价 ──

    [Fact]
    public void Calculate_splits_buckets_and_blends_per_million()
    {
        // 1 亿输入 @80% 命中 + 1000 万输出：
        // 命中 80M × $0.3 = $24；未命中 20M × $3 = $60；输出 10M × $15 = $150。
        var estimate = ModelCostCalculator.Calculate(Entry(), inputTokens: 100_000_000, cacheHitRate: 0.8m, outputTokens: 10_000_000);

        estimate.FreshInputTokens.Should().Be(20_000_000);
        estimate.CacheReadTokens.Should().Be(80_000_000);
        estimate.OutputTokens.Should().Be(10_000_000);
        estimate.FreshInputCostUsd.Should().Be(60m);
        estimate.CacheReadCostUsd.Should().Be(24m);
        estimate.OutputCostUsd.Should().Be(150m);
        estimate.TotalCostUsd.Should().Be(234m);
        estimate.BlendedPerMillionUsd.Should().Be(234m / 110m);
    }

    [Fact]
    public void Calculate_with_zero_rate_prices_all_input_fresh()
    {
        var estimate = ModelCostCalculator.Calculate(Entry(), inputTokens: 1_000_000, cacheHitRate: 0m, outputTokens: 0);

        estimate.CacheReadTokens.Should().Be(0);
        estimate.FreshInputCostUsd.Should().Be(3m);
        estimate.CacheReadCostUsd.Should().Be(0m);
        estimate.TotalCostUsd.Should().Be(3m);
        estimate.BlendedPerMillionUsd.Should().Be(3m);
    }

    [Fact]
    public void Calculate_all_zero_tokens_yields_zero_and_no_division()
    {
        var estimate = ModelCostCalculator.Calculate(Entry(), inputTokens: 0, cacheHitRate: 0m, outputTokens: 0);

        estimate.TotalCostUsd.Should().Be(0m);
        estimate.BlendedPerMillionUsd.Should().Be(0m);
    }

    // ── 长上下文分档跟随（上下文 = 输入总量各桶之和）──

    [Fact]
    public void Calculate_follows_long_context_tier_when_buckets_exceed_threshold()
    {
        // 阈值 50M：未命中 40M + 命中 20M = 60M 上下文 > 阈值 → 全部桶切分档价。
        var entry = Entry(threshold: 50_000_000, inputAbove: 6m, outputAbove: 30m, cacheReadAbove: 0.6m);
        var estimate = ModelCostCalculator.Calculate(entry, inputTokens: 60_000_000, cacheHitRate: 1 / 3m, outputTokens: 10_000_000);

        // 命中 20M（60M/3 四舍五入）、未命中 40M：
        estimate.CacheReadTokens.Should().Be(20_000_000);
        estimate.FreshInputTokens.Should().Be(40_000_000);
        estimate.FreshInputCostUsd.Should().Be(240m);   // 40M × $6
        estimate.CacheReadCostUsd.Should().Be(12m);     // 20M × $0.6
        estimate.OutputCostUsd.Should().Be(300m);       // 10M × $30
        estimate.TotalCostUsd.Should().Be(552m);
    }

    [Fact]
    public void Calculate_keeps_base_prices_below_threshold()
    {
        var entry = Entry(threshold: 50_000_000, inputAbove: 6m, outputAbove: 30m, cacheReadAbove: 0.6m);
        var estimate = ModelCostCalculator.Calculate(entry, inputTokens: 10_000_000, cacheHitRate: 0m, outputTokens: 1_000_000);

        estimate.TotalCostUsd.Should().Be(30m + 15m); // 10M × $3 + 1M × $15
    }

    // ── 公式同源：与 PricingEngine.ComputeCostBreakdown 直算逐位一致 ──

    [Fact]
    public void Calculate_matches_engine_breakdown_bitwise()
    {
        var entry = Entry(threshold: 1_000_000, inputAbove: 6m, outputAbove: 30m, cacheReadAbove: 0.6m);
        var estimate = ModelCostCalculator.Calculate(entry, inputTokens: 123_456_789, cacheHitRate: 0.37m, outputTokens: 9_876_543, model: "test-model");

        var cacheRead = ModelCostCalculator.SplitCacheRead(123_456_789, 0.37m);
        var direct = PricingEngine.ComputeCostBreakdown(
            new TokenUsageDetail
            {
                InputTokens = 123_456_789 - cacheRead,
                CacheReadTokens = cacheRead,
                OutputTokens = 9_876_543,
            },
            entry,
            model: "test-model");

        estimate.TotalCostUsd.Should().Be(direct.TotalCostUsd);
        estimate.FreshInputCostUsd.Should().Be(direct.InputCostUsd);
        estimate.CacheReadCostUsd.Should().Be(direct.CacheReadCostUsd);
        estimate.OutputCostUsd.Should().Be(direct.OutputCostUsd);
    }
}
