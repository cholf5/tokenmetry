using System.Reflection;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Services;

/// <summary>
/// 内置价格快照（I-003）：构建期内嵌的 models.dev 形状 JSON 资源
/// （Tokenmetry.Core/Pricing/model-pricing-snapshot.json），覆盖主流 Claude / Codex（GPT-5 系）
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

    private const string ResourceName = "Tokenmetry.Core.Pricing.model-pricing-snapshot.json";

    public static IReadOnlyList<ModelPriceEntry> Entries { get; } = LoadEntries();

    /// <summary>
    /// 读取内嵌快照原始 JSON（I-007：供 <see cref="DeepSeekPricingCatalog"/> 解析 deepseek 定价规则段）；
    /// 资源缺失 / 读取失败返回 null。
    /// </summary>
    public static string? LoadSnapshotJson()
    {
        try
        {
            var assembly = typeof(BuiltinPricingSnapshot).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<ModelPriceEntry> LoadEntries()
    {
        try
        {
            if (LoadSnapshotJson() is not { } json)
            {
                return Array.Empty<ModelPriceEntry>();
            }

            return ModelsDevPricingParser.Parse(json);
        }
        catch (Exception)
        {
            // 快照损坏不应导致应用崩溃；返回空表让计价走"未命中"路径。
            return Array.Empty<ModelPriceEntry>();
        }
    }
}
