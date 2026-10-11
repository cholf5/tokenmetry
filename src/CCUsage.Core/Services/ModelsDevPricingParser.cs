using System.Text.Json;
using CCUsage.Core.Models;

namespace CCUsage.Core.Services;

/// <summary>
/// models.dev api.json 解析器（I-003，蓝本 pricing_refresh.rs parse_models_dev_pricing 的移植）。
/// 兼容三种形状：
/// 1. provider map（现行）：{ "&lt;provider&gt;": { "id": ..., "models": { "&lt;model&gt;": { "id", "name", "cost": {...} } } } }；
/// 2. { "models": [ ... ] } / { "models": { ... } }（旧形状）；
/// 3. 顶层模型数组。
/// cost 内价格均为 USD / 百万 tokens（input / output / cache_read / cache_write，另有本引擎忽略的
/// tiers / context_over_200k 分档字段——按基础档计价）。cache_write / cache_read 缺失时按
/// input × 1.25 / × 0.1 推导。缺价格的模型跳过而不是失败；一个可用条目都没有时返回空列表。
/// </summary>
public static class ModelsDevPricingParser
{
    private static readonly string[] InputKeys = ["input", "input_cost_per_1m_tokens", "input_per_million"];
    private static readonly string[] OutputKeys = ["output", "output_cost_per_1m_tokens", "output_per_million"];
    private static readonly string[] CacheWriteKeys = ["cache_write", "cache_creation", "cache_creation_input"];
    private static readonly string[] CacheReadKeys = ["cache_read", "cached_input"];

    public static IReadOnlyList<ModelPriceEntry> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var entries = new List<ModelPriceEntry>();

        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                ParseModelsArray(root, provider: null, entries);
                break;
            case JsonValueKind.Object when root.TryGetProperty("models", out var models):
                if (models.ValueKind == JsonValueKind.Array)
                {
                    ParseModelsArray(models, provider: null, entries);
                }
                else if (models.ValueKind == JsonValueKind.Object)
                {
                    ParseModelsObject(models, provider: null, entries);
                }

                break;
            case JsonValueKind.Object:
                ParseProviderMap(root, entries);
                break;
        }

        return entries
            .OrderBy(entry => entry.ModelId, StringComparer.Ordinal)
            .ToList();
    }

    private static void ParseProviderMap(JsonElement root, List<ModelPriceEntry> entries)
    {
        foreach (var providerProperty in root.EnumerateObject())
        {
            if (providerProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var providerObject = providerProperty.Value;
            var provider = providerObject.TryGetProperty("id", out var providerId)
                           && providerId.ValueKind == JsonValueKind.String
                ? providerId.GetString()
                : providerProperty.Name;

            if (!providerObject.TryGetProperty("models", out var models))
            {
                continue;
            }

            if (models.ValueKind == JsonValueKind.Object)
            {
                ParseModelsObject(models, provider, entries);
            }
            else if (models.ValueKind == JsonValueKind.Array)
            {
                ParseModelsArray(models, provider, entries);
            }
        }
    }

    private static void ParseModelsObject(JsonElement models, string? provider, List<ModelPriceEntry> entries)
    {
        foreach (var modelProperty in models.EnumerateObject())
        {
            ParseModel(modelProperty.Value, fallbackId: modelProperty.Name, provider, entries);
        }
    }

    private static void ParseModelsArray(JsonElement models, string? provider, List<ModelPriceEntry> entries)
    {
        foreach (var model in models.EnumerateArray())
        {
            ParseModel(model, fallbackId: null, provider, entries);
        }
    }

    private static void ParseModel(JsonElement model, string? fallbackId, string? provider, List<ModelPriceEntry> entries)
    {
        if (model.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var modelId = model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()?.Trim()
            : fallbackId?.Trim();
        if (string.IsNullOrEmpty(modelId))
        {
            return;
        }

        if (!model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            model.TryGetProperty("pricing", out cost);
            if (cost.ValueKind != JsonValueKind.Object)
            {
                return;
            }
        }

        if (!TryReadPrice(cost, InputKeys, out var input) || !TryReadPrice(cost, OutputKeys, out var output))
        {
            return;
        }

        entries.Add(new ModelPriceEntry
        {
            ModelId = modelId,
            Provider = ReadString(model, "provider") ?? provider ?? "unknown",
            DisplayName = ReadString(model, "name") ?? ReadString(model, "display_name"),
            MatchPattern = ReadString(model, "match"),
            InputPerMillion = input,
            OutputPerMillion = output,
            CacheWritePerMillion = TryReadPrice(cost, CacheWriteKeys, out var cacheWrite)
                ? cacheWrite
                : input * ModelPriceEntry.DefaultCacheWriteMultiplier,
            CacheReadPerMillion = TryReadPrice(cost, CacheReadKeys, out var cacheRead)
                ? cacheRead
                : input * ModelPriceEntry.DefaultCacheReadMultiplier,
        });
    }

    private static bool TryReadPrice(JsonElement cost, string[] keys, out decimal price)
    {
        foreach (var key in keys)
        {
            if (!cost.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            if (!value.TryGetDecimal(out var parsed))
            {
                continue;
            }

            // "…_per_token" 形态的键按单 token 价给出，换算回百万 token 口径。
            price = key.Contains("per_token", StringComparison.Ordinal) ? parsed * 1_000_000m : parsed;
            return true;
        }

        price = 0m;
        return false;
    }

    private static string? ReadString(JsonElement model, string propertyName)
    {
        return model.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
