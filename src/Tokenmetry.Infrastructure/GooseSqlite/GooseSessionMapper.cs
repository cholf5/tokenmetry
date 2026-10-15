using System.Globalization;
using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.GooseSqlite;

/// <summary>
/// Goose sessions 行 → 统一 usage 记录的映射（I-012，逐条移植蓝本
/// ../ccusage/rust/adapters/goose/src/parser.rs 的 row_to_entry；蓝本许可：MIT License，
/// Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// - token 取值（蓝本 read_token_value）：accumulated_* 列优先、仅正整数生效，回退 total/input/output 列，
///   total 再回退 input+output；全零行不是 usage 行，跳过；
/// - reasoning = total − (input + output)（saturating），统一记录无 extra_total_tokens 字段，按 I-006/I-009
///   口径并入 OutputTokens（蓝本计价把 reasoning 按输出价计费，成本一致；显示口径偏差见卡片 Dev Log）；
/// - 时间戳（蓝本 parse_goose_timestamp）：纯整数按 毫秒（&gt;1e12）/ 秒（×1000，checked 溢出作废）判别且须 &gt;0，
///   否则按时间文本解析（RFC3339 超集，<see cref="JsonlTimestamp"/>），再兜底 19 字符 "YYYY-MM-DD HH:MM:SS"
///   归一为 UTC 与 10 字符纯日期；不可解析整行作废；
/// - 模型名取 model_config_json 的 model_name（non_empty_string；JSON 解析失败或缺失整行作废）；
/// - provider 归一化（蓝本 normalize_provider）：显式 provider '-'→'_'；缺省按模型前缀 claude-→anthropic、
///   gpt-/chatgpt-/o*→openai、gemini-→google、qwen*→openrouter、其余 goose（I-024 起参与计价候选链的
///   provider 限定，分组/显示仍用裸模型名）；
/// - 成本：蓝本逐行走计价（无 stored cost），自研 CostUsd 恒 null 交定价引擎（I-008 口径）；
///   cache 两桶恒 0（Goose 不记缓存）。
/// </summary>
public static class GooseSessionMapper
{
    /// <summary>统一记录的采集来源名（蓝本 UsageLoadAgent("Goose")）。</summary>
    public const string AgentName = "Goose";

    /// <summary>项目路径占位（蓝本 LoadedEntry.project_path = "Goose"）。</summary>
    public const string ProjectPath = "Goose";

    /// <summary>纯整数毫秒判别阈值（蓝本 1_000_000_000_000：大于它按毫秒，否则按秒 ×1000）。</summary>
    internal const long MillisScaleThreshold = 1_000_000_000_000;

    /// <summary>DateTimeOffset.FromUnixTimeMilliseconds 的可表示上限（9999-12-31）；I-006 同款防御边界。</summary>
    internal const long MaxUnixMilliseconds = 253_402_300_799_999;

    /// <summary>sessions 行的读取形态（token 三元组已按 accumulated 优先 / 正整数过滤解析）。</summary>
    public sealed record SessionRow(
        string Id,
        string ModelConfigJson,
        string? ProviderName,
        string CreatedAtRaw,
        long InputTokens,
        long OutputTokens,
        long TotalTokens);

    /// <summary>
    /// 行映射；作废返回 null 与原因（对应蓝本 row_to_entry 返回 None 的分支，供诊断计数）。
    /// </summary>
    public static UsageRecord? Map(SessionRow row, out GooseRowRejection rejection)
    {
        rejection = GooseRowRejection.None;
        if (!TryParseTimestamp(row.CreatedAtRaw, out var timestamp))
        {
            rejection = GooseRowRejection.UnparsableTimestamp;
            return null;
        }

        if (!TryParseModelName(row.ModelConfigJson, out var model))
        {
            rejection = GooseRowRejection.UnparsableModelConfig;
            return null;
        }

        if (timestamp > MaxUnixMilliseconds)
        {
            rejection = GooseRowRejection.TimestampOutOfRange;
            return null;
        }

        var inputTokens = row.InputTokens;
        var outputTokens = row.OutputTokens;
        var totalTokens = row.TotalTokens;
        if (inputTokens == 0 && outputTokens == 0 && totalTokens == 0)
        {
            rejection = GooseRowRejection.NoUsage;
            return null;
        }

        var reasoningTokens = Math.Max(0, totalTokens - SaturatingAdd(inputTokens, outputTokens));
        // provider 归一化（蓝本 row_to_entry 的 normalize_provider）此前仅登记偏差未参与计价；
        // I-024 起接入蓝本 calculate_goose_cost / missing_goose_pricing 的候选链：
        // [裸模型名] +（provider ≠ goose 时）["{provider}/{model}"]，逐候选首个命中生效。
        var provider = NormalizeProvider(row.ProviderName, model);

        return new UsageRecord
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
            Agent = AgentName,
            ProjectPath = ProjectPath,
            SessionId = row.Id,
            Model = model,
            // I-036 三元组的 provider：会话行 ProviderName 的归一化结果（与计价候选链同一个值）。
            Provider = provider,
            Pricing = new UsagePricingPlan(BuildModelCandidates(model, provider)),
            // Goose 不记逐请求成本，统一交定价引擎（蓝本 calculate_goose_cost 逐行计价同口径）。
            CostUsd = null,
            InputTokens = inputTokens,
            // reasoning（total 超出分量和的余量）并入 output：蓝本计价把 reasoning 按输出价计费，
            // total/cost 一致；CLI daily 的 outputTokens 不含 reasoning（显示口径偏差见卡片 Dev Log）。
            OutputTokens = SaturatingAdd(outputTokens, reasoningTokens),
            CacheCreationTokens = 0,
            CacheReadTokens = 0,
            MessageId = row.Id,
        };
    }

    /// <summary>
    /// 计价模型候选（蓝本 calculate_goose_cost / missing_goose_pricing 的候选集）：
    /// [裸模型名] +（provider ≠ goose 时）["{provider}/{model}"]——provider 为 goose 兜底时不做
    /// provider 限定（蓝本 `raw > 0.0 || provider_id == "goose"` 的短路同式）。
    /// </summary>
    public static IReadOnlyList<string> BuildModelCandidates(string model, string provider)
    {
        return provider == "goose"
            ? [model]
            : [model, $"{provider}/{model}"];
    }

    /// <summary>蓝本 parse_goose_timestamp：整数秒/毫秒判别 → 时间文本 → 19 字符空格/ T 分隔 → 纯日期。</summary>
    public static bool TryParseTimestamp(string? raw, out long millis)
    {
        millis = 0;
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            try
            {
                var candidate = number > MillisScaleThreshold ? number : checked(number * 1000);
                if (candidate > 0)
                {
                    millis = candidate;
                    return true;
                }

                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        // 时间文本路径：可解析即生效（蓝本 parse_ts_timestamp 无正负号门槛）；越界由
        // Map 的 TimestampOutOfRange 防御边界兜底。
        if (JsonlTimestamp.TryParse(trimmed, out var parsed))
        {
            millis = parsed.ToUnixTimeMilliseconds();
            return true;
        }

        if (trimmed.Length == 19
            && trimmed[4] == '-'
            && trimmed[7] == '-'
            && (trimmed[10] == ' ' || trimmed[10] == 'T'))
        {
            var normalized = $"{trimmed[..10]}T{trimmed[11..]}Z";
            if (JsonlTimestamp.TryParse(normalized, out var spaceParsed))
            {
                millis = spaceParsed.ToUnixTimeMilliseconds();
                return true;
            }

            return false;
        }

        if (trimmed.Length == 10 && trimmed[4] == '-' && trimmed[7] == '-')
        {
            if (JsonlTimestamp.TryParse($"{trimmed}T00:00:00Z", out var dateOnly))
            {
                millis = dateOnly.ToUnixTimeMilliseconds();
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>model_config_json → model_name（蓝本 GooseModelConfig + non_empty_string）。</summary>
    public static bool TryParseModelName(string modelConfigJson, out string model)
    {
        model = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(modelConfigJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("model_name", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var trimmed = value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    model = trimmed;
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>provider 归一化（蓝本 normalize_provider）：显式 provider 优先，缺省按模型前缀推断。</summary>
    public static string NormalizeProvider(string? providerName, string model)
    {
        var provider = providerName?.Trim();
        if (!string.IsNullOrEmpty(provider))
        {
            return provider.Replace('-', '_');
        }

        if (model.StartsWith("claude-", StringComparison.Ordinal))
        {
            return "anthropic";
        }

        if (model.StartsWith("gpt-", StringComparison.Ordinal)
            || model.StartsWith("chatgpt-", StringComparison.Ordinal)
            || model.StartsWith('o'))
        {
            return "openai";
        }

        if (model.StartsWith("gemini-", StringComparison.Ordinal))
        {
            return "google";
        }

        if (model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return "openrouter";
        }

        return "goose";
    }

    private static long SaturatingAdd(long left, long right)
    {
        var sum = unchecked(left + right);
        return sum < 0 ? long.MaxValue : sum;
    }
}

/// <summary>行级作废原因（对应蓝本 row_to_entry 返回 None 的各分支，用于诊断计数）。</summary>
public enum GooseRowRejection
{
    /// <summary>成功产出记录。</summary>
    None,

    /// <summary>tokens 三元组全零——无 usage 的会话行，正常跳过。</summary>
    NoUsage,

    /// <summary>created_at 不可解析（蓝本静默丢弃，此处计数）。</summary>
    UnparsableTimestamp,

    /// <summary>model_config_json 非法或缺 model_name（蓝本静默丢弃，此处计数）。</summary>
    UnparsableModelConfig,

    /// <summary>时间戳超出 DateTimeOffset 可表示范围——实际数据不可达的防御边界。</summary>
    TimestampOutOfRange,
}
