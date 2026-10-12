namespace CCUsage.Core.Tests.Services;

using CCUsage.Core.Models;
using CCUsage.Core.Services;

/// <summary>
/// I-024：PricingModelMatcher 的键归一化精确匹配（蓝本 pricing.rs find_exact_with_fallback /
/// normalized_pricing_key：候选与条目键两侧 '.' / '@' → '-' 归一）。Hermes/Kilo qualified 门槛的
/// "点分归一化"语义由此进入引擎。
/// </summary>
public class PricingModelMatcherTests
{
    private static readonly IReadOnlyList<ModelPriceEntry> Entries =
    [
        new ModelPriceEntry
        {
            ModelId = "gemini-2.5-pro",
            Provider = "google",
            InputPerMillion = 1.25m,
            OutputPerMillion = 10m,
            CacheWritePerMillion = 0.375m,
            CacheReadPerMillion = 0.125m,
        },
        new ModelPriceEntry
        {
            ModelId = "kimi-k2.6",
            Provider = "moonshot",
            InputPerMillion = 1m,
            OutputPerMillion = 4.16m,
            CacheWritePerMillion = 1m,
            CacheReadPerMillion = 0.18m,
        },
    ];

    [Fact]
    public void Dotted_candidate_hits_dashed_entry_via_key_normalization()
    {
        PricingModelMatcher.TryMatchEntries(Entries, "gemini.2.5.pro", PricingMatchMode.ExactOnly, out var match, out var key)
            .Should().BeTrue("候选与条目两侧按 '.'→'-' 归一后相等");
        key.Should().Be("google/gemini-2.5-pro");
        match.ModelId.Should().Be("gemini-2.5-pro");
    }

    [Fact]
    public void At_sign_in_candidate_normalizes_to_dash()
    {
        PricingModelMatcher.TryMatchEntries(Entries, "kimi@k2@6", PricingMatchMode.ExactOnly, out _, out var key)
            .Should().BeTrue("'@' 与 '.' 同为键归一化分隔符（蓝本 contains(['.', '@'])）");
        key.Should().Be("moonshot/kimi-k2.6");
    }

    [Fact]
    public void Provider_qualified_dotted_candidate_prefers_that_provider()
    {
        PricingModelMatcher.TryMatchEntries(Entries, "google/gemini.2.5.pro", PricingMatchMode.ExactOnly, out var match, out var key)
            .Should().BeTrue();
        key.Should().Be("google/gemini-2.5-pro");
        match.Provider.Should().Be("google");
    }

    [Fact]
    public void Dot_free_candidates_normalize_against_dotted_entries_both_ways()
    {
        // 蓝本 normalized_keys 索引按条目键构建：dash-only 候选与点分条目经两侧归一化互相命中
        //（条目无分隔符时归一化为恒等——Cow::Borrowed 同式）。
        PricingModelMatcher.TryMatchEntries(Entries, "kimi-k2-6", PricingMatchMode.ExactOnly, out _, out var dashedKey)
            .Should().BeTrue("dash-only 候选 ≈ 点分条目 kimi-k2.6（两侧归一化）");
        dashedKey.Should().Be("moonshot/kimi-k2.6");

        PricingModelMatcher.TryMatchEntries(Entries, "kimi-k2.6", PricingMatchMode.ExactOnly, out _, out var dottedKey)
            .Should().BeTrue("原拼写精确命中不受影响");
        dottedKey.Should().Be("moonshot/kimi-k2.6");
    }

    [Fact]
    public void Normalized_miss_stays_miss_in_both_modes()
    {
        PricingModelMatcher.TryMatchEntries(Entries, "kimi.k2.7", PricingMatchMode.ExactOnly, out _, out _)
            .Should().BeFalse("归一化拼写 kimi-k2-7 无条目");
        PricingModelMatcher.TryMatchEntries(Entries, "kimi.k2.7", PricingMatchMode.ExactAndFamily, out _, out _)
            .Should().BeFalse("条目无家族模式，归一化未中即未中");
    }

    /// <summary>
    /// I-025 歧义守卫场景：点分/横线别名对（蓝本 normalized_keys 碰撞形状）。别名对价格拆开后，
    /// 两侧原拼写都未命中的候选（如 '@' 拼写）经归一化会同时命中两条——守卫要求该拼写不指向
    /// 任何条目（蓝本 and_modify 置 None），不得按 rank/顺序取一。
    /// </summary>
    private static readonly IReadOnlyList<ModelPriceEntry> SplitAliasPairEntries =
    [
        new ModelPriceEntry
        {
            ModelId = "cla-x-1",
            Provider = "anthropic",
            InputPerMillion = 3m,
            OutputPerMillion = 15m,
            CacheWritePerMillion = 3.75m,
            CacheReadPerMillion = 0.3m,
        },
        new ModelPriceEntry
        {
            ModelId = "cla.x.1",
            Provider = "anthropic",
            InputPerMillion = 5m,
            OutputPerMillion = 25m,
            CacheWritePerMillion = 6.25m,
            CacheReadPerMillion = 0.5m,
        },
    ];

    [Fact]
    public void Alias_pair_original_spellings_still_resolve_raw()
    {
        PricingModelMatcher.TryMatchEntries(SplitAliasPairEntries, "cla-x-1", PricingMatchMode.ExactOnly, out var dashed, out var dashedKey)
            .Should().BeTrue("原拼写趟不涉归一化，别名对各自可解析");
        dashedKey.Should().Be("anthropic/cla-x-1");
        dashed.InputPerMillion.Should().Be(3m);

        PricingModelMatcher.TryMatchEntries(SplitAliasPairEntries, "cla.x.1", PricingMatchMode.ExactOnly, out var dotted, out var dottedKey)
            .Should().BeTrue("点分原拼写精确命中点分条目（别名设计不变）");
        dottedKey.Should().Be("anthropic/cla.x.1");
        dotted.InputPerMillion.Should().Be(5m);
    }

    [Fact]
    public void Ambiguous_normalized_spelling_names_neither_entry()
    {
        // "cla@x.1" 归一化 = "cla-x-1"：两条共享该拼写，归一化趟必须整体不命中
        //（守卫前行为：按顺序取 dashed 条目——价格拆开后二义）。
        PricingModelMatcher.TryMatchEntries(SplitAliasPairEntries, "cla@x.1", PricingMatchMode.ExactOnly, out _, out _)
            .Should().BeFalse("同归一化拼写多键 → names neither entry（蓝本 normalized_keys）");
        PricingModelMatcher.TryMatchEntries(SplitAliasPairEntries, "cla@x.1", PricingMatchMode.ExactAndFamily, out _, out _)
            .Should().BeFalse("条目无家族模式可兜底，歧义即未中");
    }

    [Fact]
    public void Provider_qualified_ambiguous_spelling_is_guarded_too()
    {
        PricingModelMatcher.TryMatchEntries(SplitAliasPairEntries, "anthropic/cla@x.1", PricingMatchMode.ExactOnly, out _, out _)
            .Should().BeFalse("provider 限定档与全局档共享同一守卫");
    }

    [Fact]
    public void Unique_normalized_spelling_still_matches_after_guard()
    {
        var entries = new List<ModelPriceEntry>(SplitAliasPairEntries)
        {
            new()
            {
                ModelId = "kimi-k2.6",
                Provider = "moonshot",
                InputPerMillion = 1m,
                OutputPerMillion = 4.16m,
                CacheWritePerMillion = 1m,
                CacheReadPerMillion = 0.18m,
            },
        };
        PricingModelMatcher.TryMatchEntries(entries, "kimi@k2@6", PricingMatchMode.ExactOnly, out _, out var key)
            .Should().BeTrue("唯一携带该归一化拼写的条目照常命中");
        key.Should().Be("moonshot/kimi-k2.6");
    }

    [Fact]
    public void Cross_provider_normalized_collision_names_neither()
    {
        // 蓝本碰撞域是 full key（"provider/model" 归一化整体）；桌面条目 provider 与 modelid 分离，
        // 全局档跨 provider 汇总计数——两 provider 各一条、归一化 modelid 相同即碰撞。
        var entries = new List<ModelPriceEntry>
        {
            new()
            {
                ModelId = "dup-model",
                Provider = "anthropic",
                InputPerMillion = 1m,
                OutputPerMillion = 2m,
                CacheWritePerMillion = 1.25m,
                CacheReadPerMillion = 0.1m,
            },
            new()
            {
                ModelId = "dup.model",
                Provider = "google",
                InputPerMillion = 3m,
                OutputPerMillion = 4m,
                CacheWritePerMillion = 3.75m,
                CacheReadPerMillion = 0.3m,
            },
        };
        PricingModelMatcher.TryMatchEntries(entries, "dup@model", PricingMatchMode.ExactOnly, out _, out _)
            .Should().BeFalse("跨 provider 归一化碰撞同式置 None");

        // 原拼写仍按 provider rank 取优（anthropic 优先）——归一化守卫不波及原拼写趟。
        PricingModelMatcher.TryMatchEntries(entries, "dup.model", PricingMatchMode.ExactOnly, out var raw, out var rawKey)
            .Should().BeTrue();
        rawKey.Should().Be("google/dup.model");
        raw.Provider.Should().Be("google");
    }

    [Fact]
    public void Family_fallback_still_applies_after_guarded_normalized_miss()
    {
        var entries = new List<ModelPriceEntry>(SplitAliasPairEntries)
        {
            new()
            {
                ModelId = "cla-x-family",
                Provider = "anthropic",
                MatchPattern = "cla",
                InputPerMillion = 9m,
                OutputPerMillion = 90m,
                CacheWritePerMillion = 11.25m,
                CacheReadPerMillion = 0.9m,
            },
        };
        PricingModelMatcher.TryMatchEntries(entries, "cla@x.1", PricingMatchMode.ExactAndFamily, out var family, out var familyKey)
            .Should().BeTrue("归一化趟守卫落空后家族模式兜底（桌面既有架构）");
        familyKey.Should().Be("anthropic/cla-x-family");
        family.MatchPattern.Should().Be("cla");
    }
}
