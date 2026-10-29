using System.Text.Json;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Services;

/// <summary>
/// 服务档位倍率表（fast / flex）：ccusage 在 `calculate_cost_from_tokens` 里按 `usage.speed`
/// 对整条成本乘倍率，本表是它倍率数据的桌面等价物（数据见 Pricing/speed-multipliers.json）。
/// 匹配口径：用 <see cref="PricingModelMatcher.GetCandidates"/> 的候选链（已剥 provider 前缀与
/// `-fast` 采集标记）逐候选查 exact；全未命中再查 prefix（candidate == prefix 或以 "prefix-" 开头，
/// 蓝本 normalized_prefix 语义——`claude-opus-4-6` 命中 `claude-opus-4-6-20260101`）。
/// 前缀按长度降序尝试：蓝本用的是 HashMap 首命中（无序），这里取最长前缀以保证确定性。
/// </summary>
public static class SpeedMultiplierTable
{
    private const string ResourceName = "Tokenmetry.Core.Pricing.speed-multipliers.json";

    private static readonly (Dictionary<string, decimal> Exact, string[] PrefixKeys, Dictionary<string, decimal> Prefix) Fast;
    private static readonly (Dictionary<string, decimal> Exact, string[] PrefixKeys, Dictionary<string, decimal> Prefix) Flex;

    static SpeedMultiplierTable()
    {
        var (fastExact, fastPrefix) = Load("fast");
        var (flexExact, flexPrefix) = Load("flex");
        Fast = (fastExact, OrderPrefixes(fastPrefix), fastPrefix);
        Flex = (flexExact, OrderPrefixes(flexPrefix), flexPrefix);
    }

    /// <summary>该模型在该档位下的倍率；standard 或表未命中返回 1。</summary>
    public static decimal Resolve(string? model, SpeedTier speed)
    {
        if (speed == SpeedTier.Standard)
        {
            return 1m;
        }

        var table = speed == SpeedTier.Fast ? Fast : Flex;
        var candidates = PricingModelMatcher.GetCandidates(model);

        foreach (var candidate in candidates)
        {
            if (table.Exact.TryGetValue(candidate, out var value))
            {
                return value;
            }
        }

        foreach (var pattern in table.PrefixKeys)
        {
            foreach (var candidate in candidates)
            {
                if (candidate == pattern
                    || candidate.StartsWith(pattern + "-", StringComparison.Ordinal))
                {
                    return table.Prefix[pattern];
                }
            }
        }

        return 1m;
    }

    private static string[] OrderPrefixes(Dictionary<string, decimal> prefix)
        => prefix.Keys.OrderByDescending(key => key.Length).ThenBy(key => key, StringComparer.Ordinal).ToArray();

    private static (Dictionary<string, decimal> Exact, Dictionary<string, decimal> Prefix) Load(string tier)
    {
        var exact = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var prefix = new Dictionary<string, decimal>(StringComparer.Ordinal);

        using var stream = typeof(SpeedMultiplierTable).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return (exact, prefix);
        }

        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty(tier, out var section))
        {
            return (exact, prefix);
        }

        ReadSection(section, "exact", exact);
        ReadSection(section, "prefix", prefix);
        return (exact, prefix);
    }

    private static void ReadSection(JsonElement section, string name, Dictionary<string, decimal> target)
    {
        if (!section.TryGetProperty(name, out var entries) || entries.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var entry in entries.EnumerateObject())
        {
            if (entry.Value.TryGetDecimal(out var value))
            {
                target[entry.Name.Trim().ToLowerInvariant()] = value;
            }
        }
    }
}
