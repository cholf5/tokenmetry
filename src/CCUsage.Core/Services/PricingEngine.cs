using System.Collections.Concurrent;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.Core.Services;

/// <summary>一次价格解析的产物：命中的价格条目、来源与展示键。</summary>
public readonly record struct ResolvedModelPrice(
    ModelPriceEntry Entry,
    PricingSourceKind Source,
    string? MatchedKey);

/// <summary>
/// 自有定价引擎（I-003）：为 JSONL usage 记录计算成本，三级回退
/// 用户覆盖（exact 模型名）→ models.dev 远程数据（后台刷新，查询只读内存）→ 内置快照。
/// 计价规则对齐蓝本 claude.rs（aggregation.rs price_entry）：
/// input / output 按 models.dev 口径单价（USD / 百万 tokens）；cache_creation 有 5m/1h 细分时
/// 5m 部分按 cache write 价、1h 部分按 input × 2 计，无细分时整体按 cache write 价；
/// cache_read 按其单价。模型未命中时返回 <see cref="PricingSourceKind.Missing"/>（CostUsd 为 null，
/// 不是 0），并把模型记入未命中清单供对账与诊断。
/// 本类无任何网络调用；远程数据由 <see cref="IRemotePricingSource"/>（Infrastructure）以后台方式提供。
/// </summary>
public sealed class PricingEngine
{
    private readonly IRemotePricingSource? _remoteSource;
    private readonly ConcurrentDictionary<string, UnpricedModelInfo> _unpricedModels = new(StringComparer.Ordinal);
    private volatile ModelPricingOverride[] _overrides;

    public PricingEngine(IRemotePricingSource? remoteSource = null, IEnumerable<ModelPricingOverride>? overrides = null)
    {
        _remoteSource = remoteSource;
        _overrides = (overrides ?? Array.Empty<ModelPricingOverride>())
            .Where(rule => rule.ToPriceEntry() is not null)
            .ToArray();
    }

    /// <summary>替换用户覆盖规则（设置保存后调用；线程安全）。</summary>
    public void UpdateOverrides(IEnumerable<ModelPricingOverride> overrides)
    {
        _overrides = (overrides ?? Array.Empty<ModelPricingOverride>())
            .Where(rule => rule.ToPriceEntry() is not null)
            .ToArray();
    }

    /// <summary>当前生效的用户覆盖规则（仅有效规则，按设置顺序）。</summary>
    public IReadOnlyList<ModelPricingOverride> Overrides => _overrides;

    /// <summary>
    /// 为统一记录计价：日志自带 costUSD 时直接采用（LogCost），否则按 token 明细计算。
    /// </summary>
    public PricingResult PriceRecord(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.CostUsd is { } logCost)
        {
            return new PricingResult
            {
                Source = PricingSourceKind.LogCost,
                CostUsd = logCost,
                Model = record.Model,
            };
        }

        return PriceTokens(record.Model, TokenUsageDetail.FromRecord(record));
    }

    /// <summary>按 token 明细 + 模型名计价（不做"日志自带成本"短路）。</summary>
    public PricingResult PriceTokens(string? model, TokenUsageDetail tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (TryResolveModelPrice(model, out var resolved))
        {
            return new PricingResult
            {
                Source = resolved.Source,
                CostUsd = ComputeCost(tokens, resolved.Entry),
                Model = model,
                MatchedKey = resolved.MatchedKey,
            };
        }

        RecordUnpriced(model, tokens);
        return new PricingResult
        {
            Source = PricingSourceKind.Missing,
            CostUsd = null,
            Model = model,
        };
    }

    /// <summary>
    /// 解析模型价格（三级回退），供诊断与对账使用；未命中返回 false。
    /// </summary>
    public bool TryResolveModelPrice(string? model, out ResolvedModelPrice resolved)
    {
        var overrides = _overrides;
        if (overrides.Length > 0 && TryMatchOverride(overrides, model, out var overrideEntry))
        {
            resolved = new ResolvedModelPrice(overrideEntry, PricingSourceKind.UserOverride, overrideEntry.ToKey());
            return true;
        }

        var remote = _remoteSource?.Current;
        if (remote is { Entries.Count: > 0 }
            && PricingModelMatcher.TryMatchEntries(remote.Entries, model, PricingMatchMode.ExactOnly, out var remoteEntry, out var remoteKey))
        {
            resolved = new ResolvedModelPrice(remoteEntry, PricingSourceKind.Remote, remoteKey);
            return true;
        }

        if (BuiltinPricingSnapshot.TryMatch(model, out var snapshotEntry, out var snapshotKey))
        {
            resolved = new ResolvedModelPrice(snapshotEntry, PricingSourceKind.Snapshot, snapshotKey);
            return true;
        }

        resolved = default;
        return false;
    }

    /// <summary>未命中价格的模型清单（按模型名排序，供 Q-001 对账测试与诊断）。</summary>
    public IReadOnlyList<UnpricedModelInfo> GetUnpricedModels()
    {
        return _unpricedModels
            .Values
            .OrderBy(info => info.Model, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>清空未命中记录（重新计价周期开始时可调用）。</summary>
    public void ClearUnpricedModels()
    {
        _unpricedModels.Clear();
    }

    /// <summary>
    /// 计价公式（蓝本 aggregation.rs price_entry）：1h cache write 按 input × 2，
    /// 5m cache write 按模型 cache write 价；有 5m/1h 细分时不再对 cache_creation 总数重复计价。
    /// </summary>
    private static decimal ComputeCost(TokenUsageDetail tokens, ModelPriceEntry entry)
    {
        var cacheCreation1h = tokens.CacheCreationEphemeral1hTokens;
        var hasEphemeralSplit = tokens.CacheCreationEphemeral5mTokens > 0 || cacheCreation1h > 0;
        var cacheCreation5m = hasEphemeralSplit ? tokens.CacheCreationEphemeral5mTokens : tokens.CacheCreationTokens;

        return Price(tokens.InputTokens, entry.InputPerMillion)
               + Price(tokens.OutputTokens, entry.OutputPerMillion)
               + Price(cacheCreation5m, entry.CacheWritePerMillion)
               + Price(cacheCreation1h, entry.InputPerMillion * 2m)
               + Price(tokens.CacheReadTokens, entry.CacheReadPerMillion);
    }

    private static decimal Price(long tokens, decimal perMillion)
    {
        return tokens * perMillion / 1_000_000m;
    }

    private static bool TryMatchOverride(ModelPricingOverride[] overrides, string? model, out ModelPriceEntry entry)
    {
        var candidates = PricingModelMatcher.GetCandidates(model);
        foreach (var candidate in candidates)
        {
            foreach (var rule in overrides)
            {
                var ruleModel = rule.Model.Trim();
                if (ruleModel.Length > 0 && string.Equals(ruleModel, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    entry = rule.ToPriceEntry()!;
                    return true;
                }
            }
        }

        entry = null!;
        return false;
    }

    private void RecordUnpriced(string? model, TokenUsageDetail tokens)
    {
        // 无模型名（&lt;synthetic&gt; 等）不是"模型未命中"，不计入清单。
        var normalized = model?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        _unpricedModels.AddOrUpdate(
            normalized,
            _ => new UnpricedModelInfo(normalized, recordCount: 1, totalTokens: tokens.TotalTokens),
            (_, existing) => new UnpricedModelInfo(
                existing.Model,
                existing.RecordCount + 1,
                existing.TotalTokens + tokens.TotalTokens));
    }
}
