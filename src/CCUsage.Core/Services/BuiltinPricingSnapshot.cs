using System.Reflection;
using CCUsage.Core.Models;

namespace CCUsage.Core.Services;

/// <summary>
/// 内置价格快照（I-003）：构建期内嵌的 models.dev 形状 JSON 资源
/// （CCUsage.Core/Pricing/model-pricing-snapshot.json），覆盖主流 Claude / Codex（GPT-5 系）
/// 模型及其家族模式，供断网环境离线定价，也是三级回退的最后一级。
/// 快照条目带 MatchPattern 家族模式（如 "claude-sonnet-4"），可命中带日期后缀的历史模型名
/// （如 claude-sonnet-4-20250514）；远程刷新到的 models.dev 数据按精确 id 覆盖快照。
/// </summary>
public static class BuiltinPricingSnapshot
{
    /// <summary>快照数据来源（与其远程刷新源一致）。</summary>
    public const string SourceUrl = "https://models.dev/api.json";

    /// <summary>快照整理日期（Anthropic / OpenAI 价格取自该日 models.dev 实测数据）。</summary>
    public const string SnapshotDate = "2026-10-11";

    private const string ResourceName = "CCUsage.Core.Pricing.model-pricing-snapshot.json";

    public static IReadOnlyList<ModelPriceEntry> Entries { get; } = LoadEntries();

    /// <summary>用内置快照匹配模型价格（精确 + 家族模式）。</summary>
    public static bool TryMatch(string? model, out ModelPriceEntry match, out string? matchedKey)
    {
        return PricingModelMatcher.TryMatchEntries(
            Entries,
            model,
            PricingMatchMode.ExactAndFamily,
            out match,
            out matchedKey);
    }

    private static IReadOnlyList<ModelPriceEntry> LoadEntries()
    {
        try
        {
            var assembly = typeof(BuiltinPricingSnapshot).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return Array.Empty<ModelPriceEntry>();
            }

            using var reader = new StreamReader(stream);
            return ModelsDevPricingParser.Parse(reader.ReadToEnd());
        }
        catch (Exception)
        {
            // 快照损坏不应导致应用崩溃；返回空表让计价走"未命中"路径。
            return Array.Empty<ModelPriceEntry>();
        }
    }
}
