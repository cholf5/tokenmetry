using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Services;

/// <summary>
/// 官方价单一目录投影（P-001）：把 models.dev 全量解析条目收敛为"一模型一条目、官方公布价"。
/// 三层裁决，与输入顺序无关：
/// 1. 店面白名单——只收模型持有厂商的官方店面（models.dev 实测 ID，2026-10-23 核对）；
/// 2. 家族归属——归一化模型名前缀必须属于该店面所属厂商，转售条目（alibaba-cn 的
///    MiniMax/deepseek 等）与未知家族（zai 段的 ark-code-latest 等）一律剔除；
/// 3. 海外店优先——同厂商多店面（zai/zhipuai、alibaba/alibaba-cn）同键时取海外店（计价
///    统一美元，避免人民币直标价换算的不规则小数）；海外店缺该模型时同厂商内回退国内店。
/// 目录键 = <see cref="PricingModelMatcher.NormalizePricingKey"/>(小写化 ModelId)；同键冲突按
/// 店面偏好序裁决，绝不按输入顺序。设计定稿见 docs/plans/2026-10-23-official-pricing-catalog-design.md。
/// </summary>
public static class OfficialPricingCatalog
{
    /// <summary>厂商 → 官方店面（models.dev 线上 ID）；列表顺序即海外优先偏好序，越前越优先。</summary>
    private static readonly IReadOnlyDictionary<string, string[]> VendorStorefronts = new Dictionary<string, string[]>
    {
        ["anthropic"] = ["anthropic"],
        ["openai"] = ["openai"],
        ["google"] = ["google"],
        ["deepseek"] = ["deepseek"],
        ["zhipu"] = ["zai", "zhipuai"],
        ["moonshot"] = ["moonshotai"],
        ["alibaba"] = ["alibaba", "alibaba-cn"],
        ["xai"] = ["xai"],
    };

    /// <summary>厂商 → 持有模型的归一化名前缀（家族归属表）；未覆盖的家族不进官方目录（如实 Missing，用户映射）。</summary>
    private static readonly IReadOnlyDictionary<string, string[]> VendorFamilyPrefixes = new Dictionary<string, string[]>
    {
        ["anthropic"] = ["claude"],
        ["openai"] = ["gpt", "chatgpt", "o1", "o3", "o4"],
        ["google"] = ["gemini", "gemma"],
        ["deepseek"] = ["deepseek"],
        ["zhipu"] = ["glm"],
        ["moonshot"] = ["kimi"],
        ["alibaba"] = ["qwen", "qvq"],
        ["xai"] = ["grok"],
    };

    /// <summary>店面 ID → (厂商, 厂商内偏好序)。家族前缀跨厂商不相交，同键经家族过滤后至多一个厂商存活，
    /// 偏好序只在同厂商店面间比较——全局比较安全。</summary>
    private static readonly IReadOnlyDictionary<string, (string Vendor, int Preference)> StorefrontIndex =
        VendorStorefronts.SelectMany(kv => kv.Value.Select((storefront, index) => (storefront, kv.Key, index)))
            .ToDictionary(t => t.storefront, t => (t.Key, t.index), StringComparer.Ordinal);

    /// <summary>
    /// 投影：白名单过滤 → 家族归属过滤 → 按归一键去重（零价不压价优先于店面偏好，同零价性时
    /// 海外店优先）。输出按 ModelId 排序保证确定性。
    /// 终审 I-1：裁决键 = (是否全零, 店面偏好)——同键零价与有价并存时有价必胜（含同店面
    /// 大小写重复拼写、海外零价压国内有价两个残留形态，10-21 事故的同类机制），全部候选
    /// 皆零时才按店面偏好落 $0（官方真免费模型如实计 0）。
    /// </summary>
    public static IReadOnlyList<ModelPriceEntry> Project(IEnumerable<ModelPriceEntry> entries)
    {
        var best = new Dictionary<string, (int Zero, int Preference, ModelPriceEntry Entry)>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var provider = entry.Provider.Trim().ToLowerInvariant();
            if (!StorefrontIndex.TryGetValue(provider, out var storefront))
            {
                continue;
            }

            var key = NormalizeKey(entry.ModelId);
            if (!VendorFamilyPrefixes.TryGetValue(storefront.Vendor, out var prefixes)
                || !prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            var candidate = (Zero: IsAllZero(entry) ? 1 : 0, Preference: storefront.Preference, Entry: entry);
            if (!best.TryGetValue(key, out var current)
                || candidate.Zero < current.Zero
                || (candidate.Zero == current.Zero && candidate.Preference < current.Preference))
            {
                best[key] = candidate;
            }
        }

        return best.Values
            .Select(value => value.Entry)
            .OrderBy(entry => entry.ModelId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>全零价条目（套餐 / 免费挂牌的记法形态）；零价条目在同键竞争中永不压过有价条目。</summary>
    private static bool IsAllZero(ModelPriceEntry entry)
    {
        return entry.InputPerMillion == 0m
            && entry.OutputPerMillion == 0m
            && entry.CacheWritePerMillion == 0m
            && entry.CacheReadPerMillion == 0m;
    }

    private static string NormalizeKey(string modelId)
    {
        return PricingModelMatcher.NormalizePricingKey(modelId.Trim().ToLowerInvariant());
    }
}
