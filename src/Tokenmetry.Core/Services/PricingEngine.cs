using System.Collections.Concurrent;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Services;

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
/// I-024 候选计价接线：记录带 <see cref="UsageRecord.Pricing"/> 计价计划（各源适配器按蓝本
/// open_code_model_candidates 等填充的有序候选链）时按计划走向逐候选定价、首个命中生效；
/// 快照条目带长上下文分档（models.dev cost.tiers）时按请求上下文选档（蓝本 cost.rs 两段式）。
/// 本类无任何网络调用；远程数据由 <see cref="IRemotePricingSource"/>（Infrastructure）以后台方式提供。
/// </summary>
public sealed class PricingEngine
{
    private readonly ModelCatalog _catalog;
    private readonly ConcurrentDictionary<string, UnpricedModelInfo> _unpricedModels = new(StringComparer.Ordinal);

    /// <param name="remoteSource">models.dev 远程数据源；<paramref name="catalog"/> 非空时忽略
    /// （远程数据归目录所有，避免两个源不同步）。</param>
    /// <param name="overrides">旧用户覆盖规则（I-038 兼容桥）：仅在未注入 <paramref name="catalog"/> 时
    /// 转为目录用户条目；新代码一律走 <see cref="ModelCatalog.UpdateUserDefinitions"/>。</param>
    /// <param name="catalog">共享模型目录（I-035）：身份解析与三层计价条目的唯一出处。</param>
    public PricingEngine(
        IRemotePricingSource? remoteSource = null,
        IEnumerable<ModelPricingOverride>? overrides = null,
        ModelCatalog? catalog = null)
    {
        _catalog = catalog ?? new ModelCatalog(
            remoteSource,
            (overrides ?? Array.Empty<ModelPricingOverride>())
                .Select(ModelDefinitionOverride.FromLegacy)
                .Where(definition => definition is not null)
                .Select(definition => definition!));
    }

    /// <summary>共享模型目录（身份解析 / 用户条目编辑的唯一入口）。</summary>
    public ModelCatalog Catalog => _catalog;

    /// <summary>替换用户覆盖规则（设置保存后调用；线程安全）。</summary>
    public void UpdateOverrides(IEnumerable<ModelPricingOverride> overrides)
    {
        _catalog.UpdateUserDefinitions(
            (overrides ?? Array.Empty<ModelPricingOverride>())
                .Select(ModelDefinitionOverride.FromLegacy)
                .Where(definition => definition is not null)
                .Select(definition => definition!));
    }

    /// <summary>当前生效的用户目录条目（仅有效条目，按设置顺序）。</summary>
    public IReadOnlyList<ModelDefinitionOverride> UserDefinitions => _catalog.UserOverrides;

    /// <summary>
    /// 为统一记录计价：日志自带 costUSD 时直接采用（LogCost），否则按 token 明细 + 计价计划
    /// （<see cref="UsageRecord.Pricing"/>，缺省按模型名单候选）计算。
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

        var tokens = TokenUsageDetail.FromRecord(record);
        if (record.Pricing is { } plan && plan.ModelCandidates.Count > 0)
        {
            return PriceTokens(plan, record.Model, tokens);
        }

        return PriceTokens(record.Model, tokens);
    }

    /// <summary>按 token 明细 + 模型名计价（不做"日志自带成本"短路；单候选现状口径）。</summary>
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
    /// 按计价计划（有序候选链 + 匹配走向 + output 覆盖）计价（I-024）：候选链逐个尝试、首个命中
    /// 生效（蓝本各源"首个正成本/首个命中"口径的桌面化——首个能解析出价格的候选生效，见卡片
    /// 与蓝本偏差清单：蓝本还会跳过解析成功但单价全零的候选，桌面无此数据形态）；全候选未命中时
    /// 行为与单候选一致（Missing + UnpricedModels fail-loud，键为显示模型名）。
    /// </summary>
    public PricingResult PriceTokens(UsagePricingPlan plan, string? displayModel, TokenUsageDetail tokens)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tokens);

        // P-001 T4（终审 M-1）：作用域用户条目以原始名（displayModel）在最优先检查——
        // Exact*AcrossCandidates 走法不经 TryResolveModelPrice，缺此检查则作用域条目对
        // OpenClaw / Grok / Antigravity 等多候选源无声失效。
        if (TryResolveScopedUserEntry(displayModel, out var scopedResolved))
        {
            return new PricingResult
            {
                Source = scopedResolved.Source,
                CostUsd = ComputeCost(tokens, scopedResolved.Entry, plan.OutputTokenOverride),
                Model = displayModel,
                MatchedKey = scopedResolved.MatchedKey,
            };
        }

        switch (plan.Walk)
        {
            case PricingCandidateWalk.ExactThenFamilyAcrossCandidates:
                if (TryResolveExactAcrossCandidates(plan.ModelCandidates, out var exactResolved))
                {
                    return Priced(exactResolved);
                }

                if (TryResolveFamilyAcrossCandidates(plan.ModelCandidates, out var familyResolved))
                {
                    return Priced(familyResolved);
                }

                break;

            case PricingCandidateWalk.ExactOnlyAcrossCandidates:
                if (TryResolveExactAcrossCandidates(plan.ModelCandidates, out var onlyResolved))
                {
                    return Priced(onlyResolved);
                }

                break;

            default:
                foreach (var candidate in plan.ModelCandidates)
                {
                    if (TryResolveModelPrice(candidate, out var resolved))
                    {
                        return Priced(resolved);
                    }
                }

                break;
        }

        RecordUnpriced(displayModel, tokens);
        return new PricingResult
        {
            Source = PricingSourceKind.Missing,
            CostUsd = null,
            Model = displayModel,
        };

        PricingResult Priced(ResolvedModelPrice resolved)
        {
            return new PricingResult
            {
                Source = resolved.Source,
                CostUsd = ComputeCost(tokens, resolved.Entry, plan.OutputTokenOverride),
                Model = displayModel,
                MatchedKey = resolved.MatchedKey,
            };
        }
    }

    /// <summary>
    /// 解析模型价格（三级回退 + 家族兜底），供诊断与对账使用；未命中返回 false。
    /// I-035 的接线点：<b>用户层按模型 ID 命中</b>（<see cref="ModelCatalog.ResolveIdentity"/> 把原始名
    /// 归一到 ID）——用户在设置页做的关联（别名 / 目录条目）由此对<b>价格</b>生效，此前只作用于统计聚合。
    /// 导入两层（models.dev 远程 / 内置快照）继续按<b>原始名（别名解析后）</b>走既有匹配口径：
    /// 快照条目的 <c>MatchPattern</c> 家族匹配是按拼写做包含判定，喂进归一化 ID 会同时打坏
    /// "点号条目命中点号名"与"子型号精确条目压过家族模式"两条已验证口径（Q-001 对账锚），故不动。
    /// </summary>
    public bool TryResolveModelPrice(string? model, out ResolvedModelPrice resolved)
    {
        // P-001 T4：Provider 作用域用户条目最优先（Lua 局部变量语义）——按原始名最后一个 '/'
        // 之前的完整 provider 路径精确匹配，与身份解析无关（官方目录没有的中转站自造模型也能命中）。
        if (TryResolveScopedUserEntry(model, out resolved))
        {
            return true;
        }

        var identity = _catalog.ResolveIdentity(model);
        if (TryResolveUserEntry(identity.Id, out resolved))
        {
            return true;
        }

        if (TryResolveRemoteEntry(identity.LookupName, out resolved))
        {
            return true;
        }

        if (PricingModelMatcher.TryMatchEntries(
                _catalog.SnapshotEntries,
                identity.LookupName,
                PricingMatchMode.ExactAndFamily,
                out var snapshotEntry,
                out var snapshotKey))
        {
            resolved = new ResolvedModelPrice(snapshotEntry, PricingSourceKind.Snapshot, snapshotKey);
            return true;
        }

        resolved = default;
        return false;
    }

    /// <summary>
    /// P-001 T4 作用域用户条目匹配：scope = 原始名最后一个 '/' 之前的完整 provider 路径
    /// （忽略大小写），裸名段经 <see cref="ModelId.Normalize"/> 后与条目 ID 比对。
    /// scope 拼错静默不命中（落官方价）——逃生舱语义。
    /// </summary>
    private bool TryResolveScopedUserEntry(string? model, out ResolvedModelPrice resolved)
    {
        resolved = default;
        var raw = model?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var separator = raw.LastIndexOf('/');
        if (separator <= 0)
        {
            return false;
        }

        var providerPath = raw[..separator].Trim().ToLowerInvariant();
        var bareId = ModelId.Normalize(raw[(separator + 1)..]);
        foreach (var scoped in _catalog.ScopedUserEntries)
        {
            if (string.Equals(scoped.Scope, providerPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(scoped.ModelId, bareId, StringComparison.Ordinal))
            {
                resolved = new ResolvedModelPrice(scoped.Entry, PricingSourceKind.UserOverride, scoped.Entry.ToKey());
                return true;
            }
        }

        return false;
    }

    /// <summary>用户层：用户目录条目里带价的那一档（按模型 ID 精确匹配）。</summary>
    private bool TryResolveUserEntry(string modelId, out ResolvedModelPrice resolved)
    {
        var userEntries = _catalog.UserEntries;
        if (userEntries.Count > 0
            && PricingModelMatcher.TryMatchEntries(userEntries, modelId, PricingMatchMode.ExactOnly, out var entry, out var key))
        {
            resolved = new ResolvedModelPrice(entry, PricingSourceKind.UserOverride, key);
            return true;
        }

        resolved = default;
        return false;
    }

    /// <summary>远程层：models.dev 条目的精确匹配（既有口径，含 provider 前缀优先）。</summary>
    private bool TryResolveRemoteEntry(string? model, out ResolvedModelPrice resolved)
    {
        var remoteEntries = _catalog.RemoteEntries;
        if (remoteEntries.Count > 0
            && PricingModelMatcher.TryMatchEntries(remoteEntries, model, PricingMatchMode.ExactOnly, out var entry, out var key))
        {
            resolved = new ResolvedModelPrice(entry, PricingSourceKind.Remote, key);
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
    /// 精确档逐候选解析（用户条目 → 远程 → 快照精确；不含家族模式）：蓝本 grok calculate_grok_cost_at
    /// 第一阶段与 antigravity flash 变体 find_exact_pricing_with_alias 的"全候选精确优先"口径。
    /// 用户层按归一化 ID 命中（I-035），导入两层仍按候选名原文匹配（见 <see cref="TryResolveModelPrice"/>）。
    /// </summary>
    private bool TryResolveExactAcrossCandidates(IReadOnlyList<string> candidates, out ResolvedModelPrice resolved)
    {
        foreach (var candidate in candidates)
        {
            var identity = _catalog.ResolveIdentity(candidate);
            if (TryResolveUserEntry(identity.Id, out resolved))
            {
                return true;
            }

            if (TryResolveRemoteEntry(identity.LookupName, out resolved))
            {
                return true;
            }

            if (PricingModelMatcher.TryMatchEntries(
                    _catalog.SnapshotEntries,
                    identity.LookupName,
                    PricingMatchMode.ExactOnly,
                    out var snapshotEntry,
                    out var snapshotKey))
            {
                resolved = new ResolvedModelPrice(snapshotEntry, PricingSourceKind.Snapshot, snapshotKey);
                return true;
            }
        }

        resolved = default;
        return false;
    }

    /// <summary>家族模式逐候选兜底（仅内置快照）：蓝本 find（fuzzy）档的桌面化口径。</summary>
    private bool TryResolveFamilyAcrossCandidates(IReadOnlyList<string> candidates, out ResolvedModelPrice resolved)
    {
        foreach (var candidate in candidates)
        {
            if (PricingModelMatcher.TryMatchEntries(
                    _catalog.SnapshotEntries,
                    _catalog.ResolveIdentity(candidate).LookupName,
                    PricingMatchMode.ExactAndFamily,
                    out var entry,
                    out var key))
            {
                resolved = new ResolvedModelPrice(entry, PricingSourceKind.Snapshot, key);
                return true;
            }
        }

        resolved = default;
        return false;
    }

    /// <summary>
    /// 计价公式（蓝本 aggregation.rs price_entry）：1h cache write 按 input × 2，
    /// 5m cache write 按模型 cache write 价；有 5m/1h 细分时不再对 cache_creation 总数重复计价。
    /// 长上下文分档（蓝本 cost.rs 两段式）：请求上下文（input + cache 各桶）超过条目阈值时全部桶
    /// 整体切换分档单价（缺项回落基础档；1h cache write 按"生效 input 价 × 2"跟随切换）。
    /// </summary>
    private static decimal ComputeCost(TokenUsageDetail tokens, ModelPriceEntry entry, long? outputTokenOverride = null)
    {
        var cacheCreation1h = tokens.CacheCreationEphemeral1hTokens;
        var hasEphemeralSplit = tokens.CacheCreationEphemeral5mTokens > 0 || cacheCreation1h > 0;
        var cacheCreation5m = hasEphemeralSplit ? tokens.CacheCreationEphemeral5mTokens : tokens.CacheCreationTokens;
        var outputTokens = outputTokenOverride ?? tokens.OutputTokens;

        var inputPerMillion = entry.InputPerMillion;
        var outputPerMillion = entry.OutputPerMillion;
        var cacheWritePerMillion = entry.CacheWritePerMillion;
        var cacheReadPerMillion = entry.CacheReadPerMillion;
        if (entry.LongContextThreshold is { } threshold)
        {
            var contextTokens = SaturatingAdd(SaturatingAdd(tokens.InputTokens, tokens.CacheReadTokens), SaturatingAdd(cacheCreation5m, cacheCreation1h));
            if (contextTokens > threshold)
            {
                inputPerMillion = entry.InputAboveThresholdPerMillion ?? entry.InputPerMillion;
                outputPerMillion = entry.OutputAboveThresholdPerMillion ?? entry.OutputPerMillion;
                cacheWritePerMillion = entry.CacheWriteAboveThresholdPerMillion ?? entry.CacheWritePerMillion;
                cacheReadPerMillion = entry.CacheReadAboveThresholdPerMillion ?? entry.CacheReadPerMillion;
            }
        }

        return Price(tokens.InputTokens, inputPerMillion)
               + Price(outputTokens, outputPerMillion)
               + Price(cacheCreation5m, cacheWritePerMillion)
               + Price(cacheCreation1h, inputPerMillion * 2m)
               + Price(tokens.CacheReadTokens, cacheReadPerMillion);
    }

    /// <summary>token 计数饱和加（lenient 解析域内防御，蓝本 u64 saturating_add 同式）。</summary>
    private static long SaturatingAdd(long left, long right)
    {
        var sum = unchecked(left + right);
        return sum < 0 ? long.MaxValue : sum;
    }

    private static decimal Price(long tokens, decimal perMillion)
    {
        return tokens * perMillion / 1_000_000m;
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
