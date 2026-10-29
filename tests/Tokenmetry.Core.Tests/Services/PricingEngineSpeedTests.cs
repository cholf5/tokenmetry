namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

/// <summary>
/// 档位倍率接线（对齐 ccusage cost.rs：倍率乘在整条成本上，日志自带 costUSD 不进倍率）。
/// </summary>
public class PricingEngineSpeedTests
{
    [Fact]
    public void Fast_tier_six_times_on_claude_opus_4_6()
    {
        var engine = new PricingEngine();

        var standard = engine.PriceTokens("claude-opus-4-6", Tokens(SpeedTier.Standard));
        var fast = engine.PriceTokens("claude-opus-4-6", Tokens(SpeedTier.Fast));

        standard.CostUsd.Should().Be(5m, "1M input × $5/M");
        fast.CostUsd.Should().Be(30m, "1M input × $5/M × 6（claude-opus-4-6 加速档）");
    }

    [Fact]
    public void Fast_tier_doubles_gpt_5_3_codex()
    {
        var engine = new PricingEngine();

        var standard = engine.PriceTokens("gpt-5.3-codex", Tokens(SpeedTier.Standard));
        var fast = engine.PriceTokens("gpt-5.3-codex", Tokens(SpeedTier.Fast));

        standard.CostUsd.Should().NotBeNull();
        fast.CostUsd.Should().Be(standard.CostUsd * 2m);
    }

    [Fact]
    public void Flex_tier_halves_gpt_5_5()
    {
        var engine = new PricingEngine();

        var standard = engine.PriceTokens("gpt-5.5", Tokens(SpeedTier.Standard));
        var flex = engine.PriceTokens("gpt-5.5", Tokens(SpeedTier.Flex));

        flex.CostUsd.Should().Be(standard.CostUsd * 0.5m);
    }

    [Fact]
    public void Log_cost_is_never_multiplied()
    {
        var engine = new PricingEngine();
        var record = new UsageRecord
        {
            Model = "claude-opus-4-6",
            Speed = SpeedTier.Fast,
            CostUsd = 0.42m,
            InputTokens = 1_000_000,
        };

        var result = engine.PriceRecord(record);

        result.Source.Should().Be(PricingSourceKind.LogCost);
        result.CostUsd.Should().Be(0.42m);
    }

    [Fact]
    public void Fast_tier_applies_through_a_pricing_plan()
    {
        var engine = new PricingEngine();
        var plan = UsagePricingPlan.Single("claude-opus-4-6");

        var fast = engine.PriceTokens(plan, "claude-opus-4-6", Tokens(SpeedTier.Fast));

        fast.CostUsd.Should().Be(30m);
    }

    [Fact]
    public void Record_speed_reaches_the_engine()
    {
        var engine = new PricingEngine();
        var record = new UsageRecord
        {
            Model = "claude-opus-4-6",
            Speed = SpeedTier.Fast,
            InputTokens = 1_000_000,
        };

        engine.PriceRecord(record).CostUsd.Should().Be(30m);
    }

    private static TokenUsageDetail Tokens(SpeedTier speed)
        => new() { InputTokens = 1_000_000, Speed = speed };
}
