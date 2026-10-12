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
}
