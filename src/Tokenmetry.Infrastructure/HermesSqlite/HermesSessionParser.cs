using System.Globalization;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Infrastructure.HermesSqlite;

/// <summary>行级作废原因（对应蓝本 read_session_row 返回 None 的各分支，用于诊断计数）。</summary>
public enum HermesRowRejection
{
    /// <summary>成功产出记录。</summary>
    None,

    /// <summary>五桶 tokens 与 cost 全零——无用量的会话行，正常跳过。</summary>
    NoUsage,

    /// <summary>started_at 不可读 / 非有限数 / 换算后非正（蓝本静默丢弃，此处计数）。</summary>
    InvalidTimestamp,

    /// <summary>时间戳超出 DateTimeOffset 可表示范围——实际数据不可达的防御边界。</summary>
    TimestampOutOfRange,
}

/// <summary>
/// Hermes sessions 行 → 统一 usage 记录的映射（I-016，逐条移植蓝本
/// ../ccusage/rust/adapters/hermes/src/parser.rs 的 read_session_row / to_loaded_entry / 相关助手；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// - 列值容错（蓝本 read_u64 / read_f64 / read_non_negative_f64）：token/message_count 列 i64 max(0) →
///   有限正 f64 截断 → 其余 0；started_at / cost 列 f64 优先 → i64 兜底，非有限按缺失；
/// - 时间戳（蓝本 timestamp_from_number）：秒/毫秒判别（&gt;1e12 按毫秒，否则 ×1000），截断取整、须 &gt; 0；
/// - 五桶与 cost（actual_cost_usd 优先、回退 estimated_cost_usd，非负）全零的行不是 usage 行，跳过；
/// - reasoning（extra_total_tokens）并入 OutputTokens（I-006/I-009/I-012 口径；蓝本计价把 reasoning
///   按输出价计费，成本一致；CLI daily 的 outputTokens 不含 reasoning——显示口径偏差见卡片 Dev Log）；
/// - 成本：stored cost（actual/estimated）&gt; 0 直接采用（蓝本 calculate_hermes_cost 同门槛），否则
///   CostUsd = null 交定价引擎（I-024 起走蓝本 model_candidates 候选链，见 BuildModelCandidates 接线）；
/// - provider 归一化（蓝本 normalize_provider / infer_provider_from_model 全分支）与计价候选顺序
///   （蓝本 model_candidates：provider 限定名仅在 exact-with-fallback 命中时插入，hermes 兜底不限定）
///   移植为纯函数供移植测试；I-024 起接入映射产物（精确门槛由引擎匹配承载，见 Map 内注释）；
/// - message id 取蓝本 message.id 形状 "hermes:&lt;session_id&gt;"（跨库去重键与 session id 一一对应）。
/// </summary>
public static class HermesSessionParser
{
    /// <summary>统一记录的采集来源名与项目路径（蓝本 UsageLoadAgent("Hermes") / project_path="Hermes"）。</summary>
    public const string AgentName = "Hermes";
    public const string ProjectPath = "Hermes";

    /// <summary>纯数字秒/毫秒判别阈值（蓝本 1e12）。</summary>
    internal const double MillisScaleThreshold = 1e12;

    /// <summary>DateTimeOffset.FromUnixTimeMilliseconds 的可表示上限（9999-12-31）；I-006 同款防御边界。</summary>
    internal const long MaxUnixMilliseconds = 253_402_300_799_999;

    /// <summary>sessions 行的读取形态（token 列已按蓝本 read_u64 口径解析）。</summary>
    public sealed record SessionRow(
        string SessionId,
        string Model,
        string? ProviderRaw,
        double? StartedAt,
        long MessageCount,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheCreationTokens,
        long ReasoningTokens,
        double? EstimatedCost,
        double? ActualCost);

    /// <summary>行映射；作废返回 null 与原因（供诊断计数）。</summary>
    public static UsageRecord? Map(SessionRow row, out HermesRowRejection rejection)
    {
        rejection = HermesRowRejection.None;
        if (row.StartedAt is not { } startedAt)
        {
            rejection = HermesRowRejection.InvalidTimestamp;
            return null;
        }

        if (!TryTimestampFromNumber(startedAt, out var timestamp))
        {
            rejection = HermesRowRejection.InvalidTimestamp;
            return null;
        }

        if (timestamp > MaxUnixMilliseconds)
        {
            rejection = HermesRowRejection.TimestampOutOfRange;
            return null;
        }

        var costUsd = row.ActualCost ?? row.EstimatedCost;
        if (row.InputTokens == 0
            && row.OutputTokens == 0
            && row.CacheReadTokens == 0
            && row.CacheCreationTokens == 0
            && row.ReasoningTokens == 0
            && (costUsd is not { } cost || cost == 0))
        {
            rejection = HermesRowRejection.NoUsage;
            return null;
        }

        return new UsageRecord
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
            Agent = AgentName,
            ProjectPath = ProjectPath,
            SessionId = row.SessionId,
            Model = row.Model,
            // I-036 三元组的 provider：会话行自带的 provider（归一化后；归一化不出结果且模型名可推导时
            // 由模型名推导，与计价候选链同一套口径）。
            Provider = NormalizeProvider(row.ProviderRaw, row.Model),
            // 计价走蓝本 model_candidates 候选链（I-024 接线）：provider 限定名 + 裸模型名，去重保序。
            // 蓝本"限定名仅 exact-with-fallback 命中时插入"的门槛由引擎精确匹配承载——限定名不能
            // 精确（含 '.'/'@'→'-' 键归一化，HasExactNormalizedPrice 同口径，已进匹配器）命中时
            // 候选自然不生效；分组/显示仍用裸模型名。
            Pricing = new UsagePricingPlan(BuildModelCandidates(
                NormalizeProvider(row.ProviderRaw, row.Model),
                row.Model,
                _ => true)),
            // stored cost（actual 优先、estimated 回退）> 0 直接采用，否则交定价引擎
            //（蓝本 calculate_hermes_cost 的 cost_usd.filter(> 0) 门槛同式）。
            CostUsd = ToDecimalCost(costUsd),
            InputTokens = row.InputTokens,
            // reasoning（蓝本 extra_total_tokens，按输出价计费）并入 output（I-006 口径）。
            OutputTokens = SaturatingAdd(row.OutputTokens, row.ReasoningTokens),
            CacheCreationTokens = row.CacheCreationTokens,
            CacheReadTokens = row.CacheReadTokens,
            MessageId = $"hermes:{row.SessionId}",
        };
    }

    /// <summary>蓝本 timestamp_from_number：秒/毫秒判别 → 截断取整 → 须 &gt; 0；f64→i64 溢出按 Rust
    /// as-cast 饱和语义钳到 long 边界（越界值随后由 Map 的防御边界拒绝）。</summary>
    public static bool TryTimestampFromNumber(double value, out long millis)
    {
        millis = 0;
        if (!double.IsFinite(value))
        {
            return false;
        }

        var scaled = value > MillisScaleThreshold ? value : value * 1000.0;
        if (scaled <= 0)
        {
            return false;
        }

        var truncated = Math.Truncate(scaled);
        if (truncated >= 9223372036854775808.0)
        {
            millis = long.MaxValue;
            return true;
        }

        if (truncated <= -9223372036854775808.0)
        {
            return false;
        }

        millis = (long)truncated;
        return millis > 0;
    }

    /// <summary>stored cost → decimal（正且有限才生效；蓝本 cost_usd.filter(|cost| *cost > 0.0)）。</summary>
    internal static decimal? ToDecimalCost(double? cost)
    {
        if (cost is not { } value || !double.IsFinite(value) || value <= 0)
        {
            return null;
        }

        if (decimal.TryParse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0m)
        {
            return parsed;
        }

        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>provider 归一化（蓝本 normalize_provider）：小写、'-'→'_'、别名收敛；缺省按模型前缀推断。</summary>
    public static string NormalizeProvider(string? providerRaw, string model)
    {
        var value = providerRaw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return InferProviderFromModel(model);
        }

        var normalized = value.ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "anthropic" or "claude" => "anthropic",
            "openai" or "openai_codex" => "openai",
            "google" or "google_ai" or "gemini" or "vertex" or "vertex_ai" => "google",
            "openrouter" => "openrouter",
            "xai" => "xai",
            "groq" => "groq",
            _ => normalized,
        };
    }

    /// <summary>模型前缀推断（蓝本 infer_provider_from_model）：claude-*/claude/* → anthropic；
    /// gpt*/chatgpt*/o&lt;digit&gt; → openai；gemini-*/gemini/* → google；其余 hermes。</summary>
    public static string InferProviderFromModel(string model)
    {
        var lowered = model.ToLowerInvariant();
        if (lowered.StartsWith("claude-", StringComparison.Ordinal)
            || lowered.StartsWith("claude/", StringComparison.Ordinal))
        {
            return "anthropic";
        }

        if (lowered.StartsWith("gpt", StringComparison.Ordinal)
            || lowered.StartsWith("chatgpt", StringComparison.Ordinal)
            || (lowered.StartsWith('o') && lowered.Length > 1 && lowered[1] is >= '0' and <= '9'))
        {
            return "openai";
        }

        if (lowered.StartsWith("gemini-", StringComparison.Ordinal)
            || lowered.StartsWith("gemini/", StringComparison.Ordinal))
        {
            return "google";
        }

        return "hermes";
    }

    /// <summary>
    /// 计价模型候选（蓝本 model_candidates）：provider 非 hermes 且限定名能 exact-with-fallback 命中
    /// （exact 或 '.'/'@'→'-' 归一化拼写，由 <paramref name="hasExactPrice"/> 判定）时先插限定名，
    /// 再插裸模型名，去重保序。
    /// </summary>
    public static IReadOnlyList<string> BuildModelCandidates(
        string provider, string model, Func<string, bool> hasExactPrice)
    {
        var candidates = new List<string>();
        if (!string.Equals(provider, "hermes", StringComparison.Ordinal))
        {
            var qualified = $"{provider}/{model}";
            if (hasExactPrice(qualified))
            {
                candidates.Add(qualified);
            }
        }

        candidates.Add(model);
        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>蓝本 find_exact_with_fallback 的键归一化口径：'.' / '@' → '-'（normalized_pricing_key）。</summary>
    public static bool HasExactNormalizedPrice(IEnumerable<(string Key, string ModelId)> entries, string candidate)
    {
        var normalized = NormalizePricingKey(candidate);
        foreach (var (key, modelId) in entries)
        {
            if (string.Equals(NormalizePricingKey(key), normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizePricingKey(modelId), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePricingKey(string value)
    {
        return value.Contains('.') || value.Contains('@')
            ? value.Replace(".", "-").Replace("@", "-")
            : value;
    }

    private static long SaturatingAdd(long left, long right)
    {
        var sum = unchecked(left + right);
        return sum < 0 ? long.MaxValue : sum;
    }
}
