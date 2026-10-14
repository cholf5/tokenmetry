using System.Text.Json;

namespace TokenPulse.Core.Tests.Services;

using TokenPulse.Core.Models;
using TokenPulse.Core.Services;

public class ModelsDevPricingParserTests
{
    [Fact]
    public void ParsesProviderMapWithPerMillionPrices()
    {
        const string json = """
            {
              "anthropic": {
                "id": "anthropic",
                "name": "Anthropic",
                "models": {
                  "claude-opus-4-8": {
                    "id": "claude-opus-4-8",
                    "name": "Claude Opus 4.8",
                    "cost": { "input": 10, "output": 50, "cache_read": 1, "cache_write": 12.5 }
                  }
                }
              }
            }
            """;

        var entries = ModelsDevPricingParser.Parse(json);

        var entry = entries.Should().ContainSingle().Subject;
        entry.ModelId.Should().Be("claude-opus-4-8");
        entry.Provider.Should().Be("anthropic");
        entry.DisplayName.Should().Be("Claude Opus 4.8");
        entry.InputPerMillion.Should().Be(10m);
        entry.OutputPerMillion.Should().Be(50m);
        entry.CacheReadPerMillion.Should().Be(1m);
        entry.CacheWritePerMillion.Should().Be(12.5m);
    }

    [Fact]
    public void DerivesMissingCacheRatesFromInput()
    {
        const string json = """
            {
              "openai": {
                "models": {
                  "gpt-5": { "id": "gpt-5", "cost": { "input": 1.25, "output": 10 } }
                }
              }
            }
            """;

        var entries = ModelsDevPricingParser.Parse(json);

        var entry = entries.Should().ContainSingle().Subject;
        entry.CacheWritePerMillion.Should().Be(1.25m * ModelPriceEntry.DefaultCacheWriteMultiplier);
        entry.CacheReadPerMillion.Should().Be(1.25m * ModelPriceEntry.DefaultCacheReadMultiplier);
    }

    [Fact]
    public void ParsesLegacyModelsArrayAndObjectShapes()
    {
        const string arrayJson = """
            {
              "models": [
                { "id": "claude-sonnet-4-6", "provider": "anthropic", "pricing": { "input": 3, "output": 15, "cache_read": 0.3, "cache_write": 3.75 } }
              ]
            }
            """;
        const string objectJson = """
            {
              "models": {
                "claude-sonnet-4-6": { "id": "claude-sonnet-4-6", "cost": { "input": 3, "output": 15 } }
              }
            }
            """;
        const string topLevelArrayJson = """
            [
              { "id": "claude-sonnet-4-6", "cost": { "input": 3, "output": 15 } }
            ]
            """;

        ModelsDevPricingParser.Parse(arrayJson).Should().ContainSingle(entry => entry.ModelId == "claude-sonnet-4-6" && entry.Provider == "anthropic");
        ModelsDevPricingParser.Parse(objectJson).Should().ContainSingle(entry => entry.ModelId == "claude-sonnet-4-6" && entry.Provider == "unknown");
        ModelsDevPricingParser.Parse(topLevelArrayJson).Should().ContainSingle(entry => entry.ModelId == "claude-sonnet-4-6");
    }

    [Fact]
    public void SkipsModelsWithoutPricesInsteadOfFailing()
    {
        const string json = """
            {
              "anthropic": {
                "models": {
                  "no-prices": { "id": "no-prices", "name": "No prices" },
                  "no-input": { "id": "no-input", "cost": { "output": 10 } },
                  "claude-opus-4-8": { "id": "claude-opus-4-8", "cost": { "input": 10, "output": 50 } }
                }
              }
            }
            """;

        var entries = ModelsDevPricingParser.Parse(json);

        entries.Should().ContainSingle(entry => entry.ModelId == "claude-opus-4-8");
    }

    [Fact]
    public void IgnoresContextTierFieldsAndUsesBaseCost()
    {
        const string json = """
            {
              "openai": {
                "models": {
                  "gpt-5.4": { "id": "gpt-5.4", "cost": { "input": 2.5, "output": 15, "cache_read": 0.25, "context_over_200k": { "input": 5, "output": 22.5 } } }
                }
              }
            }
            """;

        var entry = ModelsDevPricingParser.Parse(json).Should().ContainSingle().Subject;

        entry.InputPerMillion.Should().Be(2.5m);
        entry.OutputPerMillion.Should().Be(15m);
        entry.CacheReadPerMillion.Should().Be(0.25m);
    }

    [Fact]
    public void ReadsMatchPatternForSnapshotFamilyRows()
    {
        const string json = """
            {
              "anthropic": {
                "models": {
                  "claude-sonnet-4": { "id": "claude-sonnet-4", "match": "claude-sonnet-4", "cost": { "input": 3, "output": 15 } }
                }
              }
            }
            """;

        var entry = ModelsDevPricingParser.Parse(json).Should().ContainSingle().Subject;

        entry.MatchPattern.Should().Be("claude-sonnet-4");
    }

    [Fact]
    public void ReturnsEmptyListWhenNoPricedModelsExist()
    {
        ModelsDevPricingParser.Parse("{ }").Should().BeEmpty();
        ModelsDevPricingParser.Parse("{ \"anthropic\": { \"models\": {} } }").Should().BeEmpty();
    }

    [Fact]
    public void ThrowsOnMalformedJson()
    {
        var act = () => ModelsDevPricingParser.Parse("{ not-json");

        act.Should().Throw<JsonException>();
    }
}
