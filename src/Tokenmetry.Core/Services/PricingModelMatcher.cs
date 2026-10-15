using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Services;

/// <summary>条目匹配口径：ExactOnly（远程数据）或 ExactAndFamily（内置快照的家族模式兜底）。</summary>
public enum PricingMatchMode
{
    ExactOnly,
    ExactAndFamily,
}

/// <summary>
/// 模型名 → 价格条目的匹配（I-003，蓝本 pricing.rs normalize_model_candidates /
/// resolve_cached_price 与 adapters/pricing.rs rates_for_model 的移植）。
/// 候选名生成：原名、provider 前缀剥离（"openrouter/x" → "x"）、"[pi] x" / 尾缀空格剥离，
/// 并对每个候选补一个剥离 "-fast" 采集标记的变体；全部小写。
/// 匹配顺序：provider 指定的精确匹配 → 任意 provider 的精确匹配（同名时按 provider rank 取优，
/// anthropic/openai/google 最优）→（仅快照）最长家族模式包含匹配。
/// 精确匹配含键归一化变体（I-024，蓝本 pricing.rs find_exact_with_fallback / normalized_pricing_key）：
/// 每档先全部候选的原拼写、未命中再全部候选的归一化拼写（'.'/'@' → '-'，两侧归一——
/// "google/gemini.3.pro" 命中 "google/gemini-3-pro"；原拼写命中优先，扩展前行为不变）。
/// 归一化拼写歧义守卫（I-025，蓝本 pricing.rs normalized_keys 语义）：同一归一化拼写被两个及以上
/// 条目键携带时该拼写"不指向任何条目"（蓝本 and_modify 置 None），归一化趟不得按 rank 或顺序
/// 取一——否则点分/横线别名对的回退落点在价格拆开后二义。
/// </summary>
public static class PricingModelMatcher
{
    public static bool TryMatchEntries(
        IReadOnlyList<ModelPriceEntry> entries,
        string? model,
        PricingMatchMode mode,
        out ModelPriceEntry match,
        out string? matchedKey)
    {
        match = null!;
        matchedKey = null;
        if (entries.Count == 0)
        {
            return false;
        }

        var candidates = GetCandidates(model);
        if (candidates.Count == 0)
        {
            return false;
        }

        // 候选名的键归一化拼写（蓝本 normalized_pricing_key：'.' / '@' → '-'，恒等变换原样返回）。
        // 归一化趟只在原拼写未命中时运行（先原拼写、再归一化——原拼写命中优先，行为与
        // 归一化扩展前一致）；条目侧归一化拼写惰性构建一次（点分/带 @ 条目才有别于原 id）。
        var normalizedCandidates = candidates
            .Select(NormalizePricingKey)
            .ToArray();
        string[]? normalizedEntryIds = null;
        string[] NormalizedEntryIds()
        {
            if (normalizedEntryIds is null)
            {
                normalizedEntryIds = new string[entries.Count];
                for (var index = 0; index < entries.Count; index++)
                {
                    normalizedEntryIds[index] = NormalizePricingKey(entries[index].ModelId);
                }
            }

            return normalizedEntryIds;
        }

        // 1) 模型名带 provider 前缀时，优先在该 provider 内精确匹配（先原拼写、再归一化拼写）。
        var requestedProvider = ExtractRequestedProvider(model);
        if (requestedProvider is not null)
        {
            foreach (var candidate in candidates)
            {
                foreach (var entry in entries)
                {
                    if (entry.MatchPattern is not null
                        || !string.Equals(entry.Provider, requestedProvider, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(entry.ModelId, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    match = entry;
                    matchedKey = entry.ToKey();
                    return true;
                }
            }

            for (var index = 0; index < candidates.Count; index++)
            {
                var normalized = normalizedCandidates[index];
                var normalizedEntryIdsLocal = NormalizedEntryIds();
                ModelPriceEntry? only = null;
                var ambiguous = false;
                for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    var entry = entries[entryIndex];
                    if (entry.MatchPattern is not null
                        || !string.Equals(entry.Provider, requestedProvider, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(normalizedEntryIdsLocal[entryIndex], normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (only is not null)
                    {
                        // 歧义守卫（蓝本 normalized_keys 置 None）：该归一化拼写不指向任何条目。
                        ambiguous = true;
                        break;
                    }

                    only = entry;
                }

                if (!ambiguous && only is not null)
                {
                    match = only;
                    matchedKey = only.ToKey();
                    return true;
                }
            }
        }

        // 2) 不限 provider 的精确匹配；多个 provider 提供同名模型时按 rank 取最优先
        //    （先全部候选的原拼写，再归一化拼写——原拼写命中优先，行为与归一化扩展前一致）。
        ModelPriceEntry? best = null;
        var bestRank = int.MaxValue;
        foreach (var candidate in candidates)
        {
            for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                var entry = entries[entryIndex];
                if (entry.MatchPattern is not null
                    || !string.Equals(entry.ModelId, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rank = ProviderRank(entry.Provider);
                if (rank < bestRank)
                {
                    best = entry;
                    bestRank = rank;
                }
            }
        }

        if (best is null)
        {
            // 归一化趟（I-025 歧义守卫）：逐候选要求"恰好一个条目"携带该归一化拼写——跨 provider
            // 汇总计数（蓝本 full-key 归一化碰撞同式，provider 段无分隔符不改变拼写）；多键共享即
            // 跳过该候选，不按 rank 取一。首个存在唯一命中的候选生效（候选优先级序）。
            var normalizedEntryIdsLocal = NormalizedEntryIds();
            for (var index = 0; index < candidates.Count; index++)
            {
                var normalized = normalizedCandidates[index];
                ModelPriceEntry? only = null;
                var ambiguous = false;
                for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    var entry = entries[entryIndex];
                    if (entry.MatchPattern is not null
                        || !string.Equals(normalizedEntryIdsLocal[entryIndex], normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (only is not null)
                    {
                        ambiguous = true;
                        break;
                    }

                    only = entry;
                }

                if (!ambiguous && only is not null)
                {
                    best = only;
                    break;
                }
            }
        }

        if (best is not null)
        {
            match = best;
            matchedKey = best.ToKey();
            return true;
        }

        // 3) 家族模式兜底（仅内置快照）：候选名包含 pattern 即命中，取最长 pattern。
        if (mode == PricingMatchMode.ExactAndFamily)
        {
            ModelPriceEntry? family = null;
            var bestPatternLength = -1;
            foreach (var candidate in candidates)
            {
                foreach (var entry in entries)
                {
                    var pattern = entry.MatchPattern;
                    if (string.IsNullOrEmpty(pattern)
                        || pattern.Length <= bestPatternLength
                        || !candidate.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    family = entry;
                    bestPatternLength = pattern.Length;
                }
            }

            if (family is not null)
            {
                match = family;
                matchedKey = family.ToKey();
                return true;
            }
        }

        return false;
    }

    /// <summary>生成模型名的候选（小写、去 provider 前缀、去 "-fast" 标记，按优先级去重）。</summary>
    public static List<string> GetCandidates(string? model)
    {
        var candidates = new List<string>();
        if (string.IsNullOrWhiteSpace(model))
        {
            return candidates;
        }

        var trimmed = model.Trim();
        PushCandidate(candidates, trimmed);

        var separator = trimmed.LastIndexOf('/');
        if (separator >= 0)
        {
            PushCandidate(candidates, trimmed[(separator + 1)..]);
        }

        var bracket = trimmed.IndexOf("] ", StringComparison.Ordinal);
        if (bracket >= 0)
        {
            PushCandidate(candidates, trimmed[(bracket + 2)..]);
        }

        var space = trimmed.LastIndexOf(' ');
        if (space >= 0)
        {
            PushCandidate(candidates, trimmed[(space + 1)..]);
        }

        return candidates;
    }

    private static void PushCandidate(List<string> candidates, string candidate)
    {
        var value = candidate.Trim().ToLowerInvariant();
        if (value.Length == 0 || candidates.Contains(value))
        {
            return;
        }

        candidates.Add(value);

        // "-fast" 是采集侧的加速通道标记，不是真实模型名，剥离后再补一个候选。
        if (value.EndsWith("-fast", StringComparison.Ordinal))
        {
            var stripped = value[..^"-fast".Length].Trim();
            if (stripped.Length > 0 && !candidates.Contains(stripped))
            {
                candidates.Add(stripped);
            }
        }
    }

    private static string? ExtractRequestedProvider(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var trimmed = model.Trim();
        var separator = trimmed.IndexOf('/');
        return separator <= 0 ? null : trimmed[..separator].Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 键归一化（蓝本 pricing.rs normalized_pricing_key）：'.' / '@' → '-'；不含分隔符时原样返回
    /// （恒等变换，蓝本 Cow::Borrowed 同式）。
    /// </summary>
    private static string NormalizePricingKey(string value)
    {
        return value.Contains('.') || value.Contains('@')
            ? value.Replace('.', '-').Replace('@', '-')
            : value;
    }

    private static int ProviderRank(string provider)
    {
        return provider switch
        {
            "anthropic" or "openai" or "google" => 0,
            "unknown" => 2,
            _ => 1,
        };
    }
}
