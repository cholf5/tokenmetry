namespace CCUsage.Core.Tests.Services;

using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

public class PricingEngineTests
{
    private const string Sonnet46 = "claude-sonnet-4-6";
    private const string Opus48 = "claude-opus-4-8";

    [Fact]
    public void BuiltinSnapshotIsEmbeddedAndCoversMainstreamModels()
    {
        BuiltinPricingSnapshot.Entries.Should().NotBeEmpty();
        BuiltinPricingSnapshot.SourceUrl.Should().Be("https://models.dev/api.json");

        foreach (var model in new[]
                 {
                     Sonnet46, "claude-sonnet-4-5", "claude-sonnet-5", Opus48, "claude-opus-4-5",
                     "claude-haiku-4-5", "claude-opus-5", "claude-fable-5",
                     "gpt-5", "gpt-5-mini", "gpt-5-nano", "codex", "gpt-5.6-terra",
                 })
        {
            BuiltinPricingSnapshot.TryMatch(model, out _, out _).Should()
                .BeTrue($"snapshot should cover {model}");
        }
    }

    [Fact]
    public void Snapshot_PricesTokensPerMillion()
    {
        var engine = new PricingEngine();

        // 1M input(3) + 1M output(15) + 300k cache_read(0.09) = 18.09
        var result = engine.PriceTokens(Sonnet46, Tokens(1_000_000, 1_000_000, cacheRead: 300_000));

        result.IsPriced.Should().BeTrue();
        result.Source.Should().Be(PricingSourceKind.Snapshot);
        result.MatchedKey.Should().Be($"anthropic/{Sonnet46}");
        result.CostUsd.Should().Be(3m + 15m + 0.09m);
    }

    [Fact]
    public void Snapshot_AppliesCacheWriteRateToSplit5mAndTwiceInputTo1h()
    {
        var engine = new PricingEngine();

        // 蓝本 aggregation.rs pricing_fallback_calculates_known_claude_model_with_cache_tokens 的同构用例：
        // input 1M×3 + output 1M×15 + 5m 100k×3.75 + 1h 200k×(3×2) + read 1M×0.3
        var result = engine.PriceTokens(
            Sonnet46,
            Tokens(1_000_000, 1_000_000, cacheCreation: 300_000, cache5m: 100_000, cache1h: 200_000, cacheRead: 1_000_000));

        result.CostUsd.Should().Be(3m + 15m + 0.375m + 1.2m + 0.3m);
    }

    [Fact]
    public void Snapshot_Prices1hEphemeralAtTwiceInputRate()
    {
        var engine = new PricingEngine();

        // 蓝本 claude_pricing_preserves_ephemeral_1h_cache_cost：1M 1h tokens → 1M × 3 × 2 / 1M = 6
        var result = engine.PriceTokens("claude-sonnet-4-20250514", Tokens(0, 0, cache1h: 1_000_000));

        result.CostUsd.Should().Be(6m);
        result.MatchedKey.Should().Be("anthropic/claude-sonnet-4");
    }

    [Fact]
    public void Snapshot_PricesUnsplitCacheCreationAtCacheWriteRate()
    {
        var engine = new PricingEngine();

        // 无 5m/1h 细分：cache_creation 总数按 cache write 价（input × 1.25 = 3.75）。
        var result = engine.PriceTokens(Sonnet46, Tokens(0, 0, cacheCreation: 300_000));

        result.CostUsd.Should().Be(1.125m);
    }

    [Fact]
    public void Snapshot_HandlesProviderPrefixAndFastSuffix()
    {
        var engine = new PricingEngine();

        var prefixed = engine.PriceTokens($"anthropic/{Opus48}", Tokens(1_000_000, 0));
        prefixed.CostUsd.Should().Be(5m);
        prefixed.Source.Should().Be(PricingSourceKind.Snapshot);

        var fast = engine.PriceTokens($"{Sonnet46}-fast", Tokens(1_000_000, 0));
        fast.CostUsd.Should().Be(3m);
    }

    [Fact]
    public void Snapshot_PricesCodexGptFamilies()
    {
        var engine = new PricingEngine();

        engine.PriceTokens("gpt-5", Tokens(1_000_000, 1_000_000)).CostUsd.Should().Be(1.25m + 10m);
        engine.PriceTokens("gpt-5-nano", Tokens(1_000_000, 1_000_000)).CostUsd.Should().Be(0.05m + 0.4m);
        engine.PriceTokens("gpt-5.1-codex-mini", Tokens(1_000_000, 0)).CostUsd.Should().Be(1.25m);
        engine.PriceTokens("codex-mini-latest", Tokens(1_000_000, 0)).CostUsd.Should().Be(1.25m);
    }

    [Fact]
    public void Snapshot_ExactSubModelEntriesBeatFamilyPatterns()
    {
        // gpt-4.1 / o3 的家族模式是旗舰价，mini / nano 子型号必须有精确条目，否则被吞成 5 倍价
        var engine = new PricingEngine();

        engine.PriceTokens("gpt-4.1-mini", Tokens(1_000_000, 1_000_000)).CostUsd.Should().Be(0.4m + 1.6m);
        engine.PriceTokens("gpt-4.1-nano", Tokens(1_000_000, 1_000_000)).CostUsd.Should().Be(0.1m + 0.4m);
        engine.PriceTokens("o3-mini", Tokens(1_000_000, 1_000_000)).CostUsd.Should().Be(1.1m + 4.4m);
        engine.PriceTokens("o4-mini", Tokens(1_000_000, 1_000_000)).CostUsd.Should().Be(1.1m + 4.4m);
    }

    [Fact]
    public void Snapshot_PricesGpt56TerraAtOwnRateNotSolFamily()
    {
        // Q-001 真实数据对账发现的回归：快照缺 gpt-5.6-terra 条目时被 "gpt-5.6"（sol，2× 价）吞掉，
        // 与 ccusage（LiteLLM 表 terra = $2/$12/$0.2）对账差 +96%。
        // 价格取自 models.dev（openai/gpt-5.6-terra，2026-10-11 实测：2 / 12 / 0.2 / 2.5）。
        var engine = new PricingEngine();

        var terra = engine.PriceTokens("gpt-5.6-terra", Tokens(1_000_000, 1_000_000, cacheRead: 1_000_000));
        terra.CostUsd.Should().Be(2m + 12m + 0.2m);
        terra.MatchedKey.Should().Contain("gpt-5.6-terra");

        // sol 家族兜底对其他 gpt-5.6* 变体仍生效。
        engine.PriceTokens("gpt-5.6-codex", Tokens(1_000_000, 0)).CostUsd.Should().Be(4m);
    }

    [Fact]
    public void UnknownModelReturnsMissingInsteadOfZeroAndIsRecorded()
    {
        var engine = new PricingEngine();

        var result = engine.PriceTokens("totally-unknown-model", Tokens(100, 200, cacheRead: 50));

        result.IsPriced.Should().BeFalse();
        result.Source.Should().Be(PricingSourceKind.Missing);
        result.CostUsd.Should().BeNull();
        result.MatchedKey.Should().BeNull();

        var unpriced = engine.GetUnpricedModels();
        unpriced.Should().ContainSingle(info => info.Model == "totally-unknown-model");
        unpriced[0].RecordCount.Should().Be(1);
        unpriced[0].TotalTokens.Should().Be(350);
    }

    [Fact]
    public void UnpricedModelsAccumulateCountsAndCanBeCleared()
    {
        var engine = new PricingEngine();
        engine.PriceTokens("model-a", Tokens(10, 10));
        engine.PriceTokens("model-a", Tokens(1, 1));
        engine.PriceTokens("model-b", Tokens(5, 5));

        var unpriced = engine.GetUnpricedModels();
        unpriced.Should().HaveCount(2);
        unpriced[0].Model.Should().Be("model-a");
        unpriced[0].RecordCount.Should().Be(2);
        unpriced[0].TotalTokens.Should().Be(22);
        unpriced[1].Model.Should().Be("model-b");

        engine.ClearUnpricedModels();
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    [Fact]
    public void NullModelIsMissingButNotRecordedAsUnpricedModel()
    {
        var engine = new PricingEngine();

        var result = engine.PriceTokens(null, Tokens(100, 100));

        result.IsPriced.Should().BeFalse();
        result.Source.Should().Be(PricingSourceKind.Missing);
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    [Fact]
    public void PriceRecordPrefersLogCostWhenPresent()
    {
        var engine = new PricingEngine();
        var record = CreateRecord(model: "totally-unknown-model", costUsd: 0.0123m);

        var result = engine.PriceRecord(record);

        result.Source.Should().Be(PricingSourceKind.LogCost);
        result.CostUsd.Should().Be(0.0123m);
        result.IsPriced.Should().BeTrue();
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    [Fact]
    public void PriceRecordComputesCostWhenLogCostMissing()
    {
        var engine = new PricingEngine();
        var record = CreateRecord(model: Sonnet46, costUsd: null, input: 1_000_000, output: 0);

        var result = engine.PriceRecord(record);

        result.Source.Should().Be(PricingSourceKind.Snapshot);
        result.CostUsd.Should().Be(3m);
    }

    [Fact]
    public void UserOverrideBeatsSnapshot()
    {
        var engine = new PricingEngine(overrides: new[]
        {
            new ModelPricingOverride { Model = Sonnet46, InputPerMillion = 9m, OutputPerMillion = 90m },
        });

        var result = engine.PriceTokens(Sonnet46, Tokens(1_000_000, 1_000_000));

        result.Source.Should().Be(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(99m);
    }

    [Fact]
    public void UserOverrideAppliesDefaultCacheRatesWhenOmitted()
    {
        var engine = new PricingEngine(overrides: new[]
        {
            new ModelPricingOverride { Model = Sonnet46, InputPerMillion = 4m, OutputPerMillion = 40m },
        });

        // 5m 100k × (4×1.25=5) = 0.5；1h 200k × (4×2)=1.6；read 1M × (4×0.1=0.4)
        var result = engine.PriceTokens(
            Sonnet46,
            Tokens(0, 0, cacheCreation: 300_000, cache5m: 100_000, cache1h: 200_000, cacheRead: 1_000_000));

        result.Source.Should().Be(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(0.5m + 1.6m + 0.4m);
    }

    [Fact]
    public void UserOverrideMatchesFastSuffixAndIsCaseInsensitive()
    {
        var engine = new PricingEngine(overrides: new[]
        {
            new ModelPricingOverride { Model = "Claude-Opus-4-8", InputPerMillion = 1m, OutputPerMillion = 2m },
        });

        var result = engine.PriceTokens($"{Opus48}-fast", Tokens(1_000_000, 0));

        result.Source.Should().Be(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(1m);
    }

    [Fact]
    public void UpdateOverridesTakesEffectImmediately()
    {
        var engine = new PricingEngine();
        engine.PriceTokens(Sonnet46, Tokens(1_000_000, 0)).CostUsd.Should().Be(3m);

        engine.UpdateOverrides(new[]
        {
            new ModelPricingOverride { Model = Sonnet46, InputPerMillion = 100m, OutputPerMillion = 200m },
        });

        var result = engine.PriceTokens(Sonnet46, Tokens(1_000_000, 0));
        result.Source.Should().Be(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(100m);
    }

    [Fact]
    public void InvalidOverridesAreIgnored()
    {
        var engine = new PricingEngine(overrides: new ModelPricingOverride[]
        {
            new() { Model = " ", InputPerMillion = 1m, OutputPerMillion = 2m },
            new() { Model = Sonnet46, InputPerMillion = -1m, OutputPerMillion = 2m },
        });
        engine.Overrides.Should().BeEmpty();

        engine.PriceTokens(Sonnet46, Tokens(1_000_000, 0)).Source.Should().Be(PricingSourceKind.Snapshot);
    }

    [Fact]
    public void RemoteDataOverridesSnapshot()
    {
        var remote = new FakeRemotePricingSource(new RemotePricingSnapshot
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Entries = new[]
            {
                new ModelPriceEntry
                {
                    ModelId = Sonnet46,
                    Provider = "anthropic",
                    InputPerMillion = 30m,
                    OutputPerMillion = 300m,
                    CacheWritePerMillion = 37.5m,
                    CacheReadPerMillion = 3m,
                },
            },
        });
        var engine = new PricingEngine(remote);

        var result = engine.PriceTokens(Sonnet46, Tokens(1_000_000, 1_000_000, cacheRead: 1_000_000));

        result.Source.Should().Be(PricingSourceKind.Remote);
        result.MatchedKey.Should().Be($"anthropic/{Sonnet46}");
        result.CostUsd.Should().Be(30m + 300m + 3m);
    }

    [Fact]
    public void RemoteWithoutEntryFallsBackToSnapshotFamilyMatch()
    {
        var remote = new FakeRemotePricingSource(new RemotePricingSnapshot
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Entries = new[]
            {
                new ModelPriceEntry
                {
                    ModelId = "claude-opus-4-8",
                    Provider = "anthropic",
                    InputPerMillion = 10m,
                    OutputPerMillion = 50m,
                    CacheWritePerMillion = 12.5m,
                    CacheReadPerMillion = 1m,
                },
            },
        });
        var engine = new PricingEngine(remote);

        // 远程没有 sonnet-4 的精确条目 → 回退内置快照（家族模式命中 claude-sonnet-4-6）。
        var result = engine.PriceTokens("claude-sonnet-4-6-20260210", Tokens(1_000_000, 0));

        result.Source.Should().Be(PricingSourceKind.Snapshot);
        result.CostUsd.Should().Be(3m);
    }

    [Fact]
    public void ProviderPrefixedModelPrefersThatProviderInRemoteData()
    {
        var remote = new FakeRemotePricingSource(new RemotePricingSnapshot
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Entries = new[]
            {
                new ModelPriceEntry
                {
                    ModelId = "claude-fable-5", Provider = "anthropic",
                    InputPerMillion = 10m, OutputPerMillion = 50m, CacheWritePerMillion = 12.5m, CacheReadPerMillion = 1m,
                },
                new ModelPriceEntry
                {
                    ModelId = "claude-fable-5", Provider = "openrouter",
                    InputPerMillion = 20m, OutputPerMillion = 99m, CacheWritePerMillion = 25m, CacheReadPerMillion = 2m,
                },
            },
        });
        var engine = new PricingEngine(remote);

        var result = engine.PriceTokens("openrouter/claude-fable-5", Tokens(1_000_000, 0));

        result.Source.Should().Be(PricingSourceKind.Remote);
        result.MatchedKey.Should().Be("openrouter/claude-fable-5");
        result.CostUsd.Should().Be(20m);
    }

    [Fact]
    public void TryResolveModelPriceReportsHitAndMiss()
    {
        var engine = new PricingEngine();

        engine.TryResolveModelPrice(Opus48, out var hit).Should().BeTrue();
        hit.Source.Should().Be(PricingSourceKind.Snapshot);
        hit.Entry.InputPerMillion.Should().Be(5m);

        engine.TryResolveModelPrice("no-such-model", out _).Should().BeFalse();
        engine.TryResolveModelPrice(null, out _).Should().BeFalse();
    }

    private static TokenUsageDetail Tokens(
        long input,
        long output,
        long cacheCreation = 0,
        long cache5m = 0,
        long cache1h = 0,
        long cacheRead = 0)
    {
        return new TokenUsageDetail
        {
            InputTokens = input,
            OutputTokens = output,
            CacheCreationTokens = cacheCreation,
            CacheCreationEphemeral5mTokens = cache5m,
            CacheCreationEphemeral1hTokens = cache1h,
            CacheReadTokens = cacheRead,
        };
    }

    private static UsageRecord CreateRecord(
        string model,
        decimal? costUsd,
        long input = 0,
        long output = 0)
    {
        return new UsageRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            Agent = "Claude Code",
            ProjectPath = "/tmp/project",
            SessionId = "session",
            Model = model,
            CostUsd = costUsd,
            InputTokens = input,
            OutputTokens = output,
        };
    }

    private sealed class FakeRemotePricingSource : IRemotePricingSource
    {
        public FakeRemotePricingSource(RemotePricingSnapshot? snapshot)
        {
            Current = snapshot;
        }

        public RemotePricingSnapshot? Current { get; }
    }
}
