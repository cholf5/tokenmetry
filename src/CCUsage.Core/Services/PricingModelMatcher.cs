using CCUsage.Core.Models;

namespace CCUsage.Core.Services;

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

        // 1) 模型名带 provider 前缀时，优先在该 provider 内精确匹配。
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
        }

        // 2) 不限 provider 的精确匹配；多个 provider 提供同名模型时按 rank 取最优先。
        ModelPriceEntry? best = null;
        var bestRank = int.MaxValue;
        foreach (var candidate in candidates)
        {
            foreach (var entry in entries)
            {
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
