namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

/// <summary>
/// P-001 T4：用户定价条目的 Provider 前缀作用域（Lua 语义——官方价是全局变量，用户条目是
/// 局部变量，局部优先）。作用域按"原始名最后一个 '/' 之前的完整 provider 路径"精确匹配；
/// 作用域条目绝不泄漏到裸名 / 官方价路径。
/// </summary>
public class ModelCatalogUserScopeTests
{
    private static TokenUsageDetail Tokens(long input, long output) =>
        TokenUsageDetail.FromRecord(new UsageRecord
        {
            Timestamp = DateTimeOffset.Now,
            Agent = "ZCode",
            ProjectPath = "/tmp",
            SessionId = "s",
            Model = "probe",
            CostUsd = null,
            InputTokens = input,
            OutputTokens = output,
            CacheCreationTokens = 0,
            CacheReadTokens = 0,
            MessageId = "m",
        });

    [Fact]
    public void ScopedEntry_shadows_official_price_for_matching_provider_path()
    {
        var engine = new PricingEngine(catalog: new ModelCatalog(null, new[]
        {
            new ModelDefinitionOverride
            {
                Id = "glm-5.3-flash",
                ProviderScope = "openrouter/z-ai",
                InputPerMillion = 0.047m,
                OutputPerMillion = 0.12m,
            },
        }));

        var result = engine.PriceTokens("openrouter/z-ai/glm-5.3-flash", Tokens(1_000_000, 0));

        result.Source.Should().Be(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(0.047m);
    }

    [Fact]
    public void ScopedEntry_never_leaks_to_other_paths_or_bare_names()
    {
        var engine = new PricingEngine(catalog: new ModelCatalog(null, new[]
        {
            new ModelDefinitionOverride
            {
                Id = "glm-5.3-flash",
                ProviderScope = "openrouter/z-ai",
                InputPerMillion = 0.047m,
                OutputPerMillion = 0.12m,
            },
        }));

        // provider 路径不同（openrouter ≠ openrouter/z-ai）→ 官方价。
        engine.PriceTokens("openrouter/glm-5.3-flash", Tokens(1_000_000, 0))
            .CostUsd.Should().Be(0.15m);
        // 裸名 → 官方价（作用域条目不得冒充全局价）。
        engine.PriceTokens("glm-5.3-flash", Tokens(1_000_000, 0))
            .CostUsd.Should().Be(0.15m);
    }

    [Fact]
    public void Bare_user_entry_covers_every_normalized_variant_including_prefixed()
    {
        var engine = new PricingEngine(catalog: new ModelCatalog(null, new[]
        {
            new ModelDefinitionOverride { Id = "glm-5.3-flash", InputPerMillion = 0.2m, OutputPerMillion = 0.6m },
        }));

        engine.PriceTokens("glm-5.3-flash", Tokens(1_000_000, 0))
            .CostUsd.Should().Be(0.2m);
        engine.PriceTokens("z-ai/glm-5.3-flash", Tokens(1_000_000, 0))
            .CostUsd.Should().Be(0.2m);
    }

    [Fact]
    public void Scoped_entry_beats_bare_entry_only_on_its_own_provider_path()
    {
        var engine = new PricingEngine(catalog: new ModelCatalog(null, new[]
        {
            new ModelDefinitionOverride { Id = "glm-5.3-flash", InputPerMillion = 0.2m, OutputPerMillion = 0.6m },
            new ModelDefinitionOverride
            {
                Id = "glm-5.3-flash",
                ProviderScope = "z-ai",
                InputPerMillion = 0.047m,
                OutputPerMillion = 0.12m,
            },
        }));

        engine.PriceTokens("z-ai/glm-5.3-flash", Tokens(1_000_000, 0))
            .CostUsd.Should().Be(0.047m);
        engine.PriceTokens("glm-5.3-flash", Tokens(1_000_000, 0))
            .CostUsd.Should().Be(0.2m);
    }

    [Fact]
    public void Typo_in_scope_silently_falls_back_to_official_price()
    {
        // scope 拼错（opnrouter）而原始名用正确前缀（openrouter）→ 静默不命中，落官方价；
        // 若用户把 scope 和原始名拼成同一个错，那是合法命中（两段精确比对的一致性）。
        var engine = new PricingEngine(catalog: new ModelCatalog(null, new[]
        {
            new ModelDefinitionOverride
            {
                Id = "glm-5.3-flash",
                ProviderScope = "opnrouter",
                InputPerMillion = 0.047m,
                OutputPerMillion = 0.12m,
            },
        }));

        var result = engine.PriceTokens("openrouter/glm-5.3-flash", Tokens(1_000_000, 0));

        result.Source.Should().NotBe(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(0.15m);
    }

    [Fact]
    public void Scoped_entry_works_for_models_absent_from_official_catalog()
    {
        // 逃生舱完整语义：官方目录没有的中转站自造模型，也能经作用域条目定价。
        var engine = new PricingEngine(catalog: new ModelCatalog(null, new[]
        {
            new ModelDefinitionOverride
            {
                Id = "some-relay-only-model",
                ProviderScope = "z-ai",
                InputPerMillion = 1m,
                OutputPerMillion = 2m,
            },
        }));

        var result = engine.PriceTokens("z-ai/some-relay-only-model", Tokens(1_000_000, 0));

        result.Source.Should().Be(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(1m);
    }

    [Fact]
    public void Scoped_entry_applies_in_pricing_plan_walk_paths_too()
    {
        // 终审 M-1：OpenClaw/Grok/Antigravity 走计划候选链走法（PriceTokens(plan,…)）——
        // 作用域条目以原始名在最优先检查，否则对这些 agent 是无声死配置。
        var engine = new PricingEngine(catalog: new ModelCatalog(null, new[]
        {
            new ModelDefinitionOverride
            {
                Id = "glm-5.3-flash",
                ProviderScope = "openrouter/z-ai",
                InputPerMillion = 0.047m,
                OutputPerMillion = 0.12m,
            },
        }));

        var result = engine.PriceTokens(
            new UsagePricingPlan(
                ["openrouter/z-ai/glm-5.3-flash", "glm-5.3-flash"],
                PricingCandidateWalk.ExactThenFamilyAcrossCandidates),
            "openrouter/z-ai/glm-5.3-flash",
            Tokens(1_000_000, 0));

        result.Source.Should().Be(PricingSourceKind.UserOverride);
        result.CostUsd.Should().Be(0.047m);
    }
}
