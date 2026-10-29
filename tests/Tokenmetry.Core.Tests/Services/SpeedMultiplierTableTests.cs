namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

public class SpeedMultiplierTableTests
{
    [Theory]
    [InlineData("claude-opus-4-6", 6.0)]
    [InlineData("claude-opus-4-6-20260101", 6.0)]
    [InlineData("claude-opus-4-6-fast", 6.0)]
    [InlineData("claude-opus-4-8", 2.0)]
    [InlineData("gpt-5.3-codex", 2.0)]
    [InlineData("gpt-5.5", 2.5)]
    [InlineData("gpt-6.1-terra", 2.0)]
    [InlineData("claude-sonnet-4-5-20250929", 1.0)]
    public void Fast_tier_resolves_from_the_override_table(string model, double expected)
    {
        SpeedMultiplierTable.Resolve(model, SpeedTier.Fast).Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("gpt-5.5", 0.5)]
    [InlineData("gpt-6.1-sol", 0.5)]
    [InlineData("claude-opus-4-6", 1.0)]
    public void Flex_tier_resolves_from_the_override_table(string model, double expected)
    {
        SpeedMultiplierTable.Resolve(model, SpeedTier.Flex).Should().Be((decimal)expected);
    }

    [Fact]
    public void Standard_tier_is_never_multiplied()
    {
        SpeedMultiplierTable.Resolve("claude-opus-4-6", SpeedTier.Standard).Should().Be(1m);
        SpeedMultiplierTable.Resolve(null, SpeedTier.Fast).Should().Be(1m);
        SpeedMultiplierTable.Resolve("unknown-model", SpeedTier.Fast).Should().Be(1m);
    }
}
