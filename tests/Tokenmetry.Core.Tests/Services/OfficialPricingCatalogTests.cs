namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

/// <summary>
/// P-001 官方目录投影：白名单厂商 + 家族归属 + 海外店优先 + 归一键去重。
/// 用例数据复刻 2026-10-21 事故形态（scnet-token-plan/GLM-5.3-Flash 全零条目压价）。
/// </summary>
public class OfficialPricingCatalogTests
{
    private static ModelPriceEntry Entry(
        string provider,
        string modelId,
        decimal input,
        decimal output,
        decimal cacheRead = 0m,
        decimal cacheWrite = 0m) => new()
    {
        Provider = provider,
        ModelId = modelId,
        InputPerMillion = input,
        OutputPerMillion = output,
        CacheReadPerMillion = cacheRead,
        CacheWritePerMillion = cacheWrite,
    };

    [Fact]
    public void Incident_all_zero_scnet_entry_is_dropped_and_zai_is_sole_survivor_for_glm_flash_key()
    {
        var projected = OfficialPricingCatalog.Project(
        [
            Entry("scnet-token-plan", "GLM-5.3-Flash", 0m, 0m, 0m, 0m),
            Entry("zai", "glm-5.3-flash", 0.15m, 0.5m, 0.03m, 0m),
        ]);

        projected.Where(e => e.Provider == "scnet-token-plan").Should().BeEmpty();
        var glmFlash = projected.Where(e => e.ModelId.Equals("glm-5.3-flash", StringComparison.OrdinalIgnoreCase)).ToList();
        glmFlash.Should().ContainSingle();
        glmFlash[0].Provider.Should().Be("zai");
        glmFlash[0].InputPerMillion.Should().Be(0.15m);
    }

    [Fact]
    public void Non_whitelist_storefronts_are_dropped_even_with_exact_same_model_name()
    {
        var projected = OfficialPricingCatalog.Project(
        [
            Entry("privatemode-ai", "glm-5.3-flash", 0.2311m, 0.7511m, 0.0578m),
            Entry("deepinfra", "zai-org/GLM-5.3-Flash", 0.15m, 0.5m, 0.03m),
        ]);

        projected.Should().BeEmpty();
    }

    [Fact]
    public void Overseas_storefront_shadows_domestic_for_same_key()
    {
        var projected = OfficialPricingCatalog.Project(
        [
            Entry("zhipuai", "glm-5.3", 1.19516m, 4.18306m, 0.29879m),
            Entry("zai", "glm-5.3", 1.4m, 4.4m, 0.26m),
            Entry("alibaba-cn", "qwen-x", 1m, 2m, 0.1m),
            Entry("alibaba", "qwen-x", 1m, 2m, 0.1m),
        ]);

        var glm = projected.Where(e => e.ModelId.Equals("glm-5.3", StringComparison.OrdinalIgnoreCase)).ToList();
        glm.Should().ContainSingle();
        glm[0].Provider.Should().Be("zai");

        var qwen = projected.Where(e => e.ModelId.Equals("qwen-x", StringComparison.OrdinalIgnoreCase)).ToList();
        qwen.Should().ContainSingle();
        qwen[0].Provider.Should().Be("alibaba");
    }

    [Fact]
    public void Resold_model_from_foreign_family_is_dropped_from_storefront()
    {
        var projected = OfficialPricingCatalog.Project(
        [
            Entry("alibaba-cn", "MiniMax-M2.5", 0.4m, 1.6m, 0.04m),
            Entry("alibaba-cn", "deepseek-v4.1-flash", 0.14m, 0.28m, 0.0028m),
        ]);

        projected.Should().BeEmpty();
    }

    [Fact]
    public void Model_family_not_owned_by_any_vendor_is_dropped()
    {
        var projected = OfficialPricingCatalog.Project(
        [
            Entry("zai", "ark-code-latest", 0m, 0m),
        ]);

        projected.Should().BeEmpty();
    }

    [Fact]
    public void Projection_is_independent_of_input_order()
    {
        var first = OfficialPricingCatalog.Project(
        [
            Entry("zhipuai", "glm-5.3", 1.19516m, 4.18306m, 0.29879m),
            Entry("zai", "glm-5.3", 1.4m, 4.4m, 0.26m),
        ]);
        var second = OfficialPricingCatalog.Project(
        [
            Entry("zai", "glm-5.3", 1.4m, 4.4m, 0.26m),
            Entry("zhipuai", "glm-5.3", 1.19516m, 4.18306m, 0.29879m),
        ]);

        first.Single(e => e.ModelId.Equals("glm-5.3", StringComparison.OrdinalIgnoreCase)).Provider
            .Should().Be(second.Single(e => e.ModelId.Equals("glm-5.3", StringComparison.OrdinalIgnoreCase)).Provider);
    }

    [Fact]
    public void All_zero_duplicate_in_same_storefront_loses_to_priced_entry()
    {
        // 终审 I-1(a)：10-21 事故记法若搬进白名单店面（同店面大小写重复拼写、零价在前——
        // ordinal 排序大写先到），零价条目仍不得压过有价条目。
        var projected = OfficialPricingCatalog.Project(
        [
            Entry("zai", "GLM-5.3-Flash", 0m, 0m, 0m, 0m),
            Entry("zai", "glm-5.3-flash", 0.15m, 0.5m, 0.03m, 0m),
        ]);

        var glmFlash = projected.Where(e => e.ModelId.Equals("glm-5.3-flash", StringComparison.OrdinalIgnoreCase)).ToList();
        glmFlash.Should().ContainSingle();
        glmFlash[0].InputPerMillion.Should().Be(0.15m, "同键零价与有价并存时有价必胜（零价条目绝不压价）");
    }

    [Fact]
    public void Zero_price_entry_loses_to_priced_entry_across_storefronts()
    {
        // 终审 I-1(b)：海外店挂零价、国内店挂有价、同键——"零价不压价"优先于"海外店优先"
        //（用户拍板的核心是 \$0 假价这类边界情况结构性不可能；全部候选皆零时才按海外优先落 \$0）。
        var projected = OfficialPricingCatalog.Project(
        [
            Entry("zai", "glm-5.3", 0m, 0m, 0m, 0m),
            Entry("zhipuai", "glm-5.3", 1.19516m, 4.18306m, 0.29879m),
        ]);

        var glm = projected.Where(e => e.ModelId.Equals("glm-5.3", StringComparison.OrdinalIgnoreCase)).ToList();
        glm.Should().ContainSingle();
        glm[0].Provider.Should().Be("zhipuai");
        glm[0].InputPerMillion.Should().Be(1.19516m);
    }

    [Fact]
    public void Genuinely_free_official_model_survives_as_zero_when_it_is_the_only_entry()
    {
        // 终审 I-1 配套：白名单店面唯一的官方全零条目（真免费模型）如实保留为 $0——
        // 零价守卫只禁止零价"压过"有价，不消灭真实的免费挂牌。
        var projected = OfficialPricingCatalog.Project(
        [
            Entry("zai", "glm-9-free", 0m, 0m, 0m, 0m),
        ]);

        projected.Should().ContainSingle();
        projected[0].InputPerMillion.Should().Be(0m);
    }
}
