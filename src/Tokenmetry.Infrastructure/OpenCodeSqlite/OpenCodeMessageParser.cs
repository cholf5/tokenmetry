using System.Globalization;
using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.SqliteSource;

namespace Tokenmetry.Infrastructure.OpenCodeSqlite;

/// <summary>行级拒绝原因（对应蓝本 message_value_to_entry 返回 None 的各分支，用于诊断计数）。</summary>
public enum OpenCodeRowRejection
{
    /// <summary>成功产出记录。</summary>
    None,

    /// <summary>无 usage 载荷（tokens 缺失或四桶+extra 全零）——非 usage 行（如 user 消息），正常跳过。</summary>
    NoUsagePayload,

    /// <summary>有 usage 载荷但 modelID / providerID 缺失——蓝本静默丢弃，此处计 InvalidRecords。</summary>
    MissingIdentity,

    /// <summary>时间戳超出 DateTimeOffset 可表示范围——实际数据不可达的防御边界。</summary>
    TimestampOutOfRange,
}

/// <summary>
/// OpenCode message 载荷 → 统一 usage 记录的映射（I-009，逐条移植蓝本
/// ../ccusage/rust/adapters/opencode/src/parser.rs 的 OpenCodeMessage / OpenCodeV2Message /
/// message_value_to_entry / into_legacy_message；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。
/// JSON 字段读取为蓝本 jsonl.rs lenient 助手的等价移植：
/// - lenient_u64：仅能落在 u64 的非负整数生效（浮点/字符串/负数/缺失一律 0）；
/// - lenient_i64：仅整数生效（浮点为 None）；lenient_f64：任意 JSON 数值；
/// - lenient_object：对象才解析（含内部字段类型不符 → 整个对象按缺失）；
/// - non_empty_string：字符串去首尾空白后非空才生效。
/// 映射语义：tokens.cache.write/read → cache_creation/read；reasoning 与 tokens.total 超出分量和的
/// 余量都记入蓝本 extra_total_tokens（apply_total_token_fallback，output 为 0 时优先补入 output），
/// 统一记录无该字段，按 I-006 口径并入 OutputTokens（CLI 对 extra 同样按 output 价计费，成本一致）；
/// 四桶与 extra 全零的行不是 usage 行（user 消息等）→ 跳过；有载荷但缺 modelID/providerID → 拒绝；
/// time.created ≤ 0 或缺失 → epoch（蓝本 keeps_epoch 口径）；消息自带的正 cost 直接采用，
/// 否则 CostUsd=null 交定价引擎（蓝本走 open_code_model_candidates 计价，见卡片偏差清单）。
/// 模型候选机制（resolve / normalize / provider 前缀）逐条移植为纯函数，I-024 起接入映射产物
/// <see cref="UsageRecord.Pricing"/>（候选链只影响计价，分组/显示仍用原始 modelID）。
/// </summary>
public static class OpenCodeMessageParser
{
    /// <summary>统一记录的采集来源名与项目路径（蓝本 LoadedEntry project="opencode" / project_path="OpenCode"）。</summary>
    public const string AgentName = "OpenCode";
    public const string ProjectPath = "OpenCode";

    /// <summary>会话 id 缺失时的回退值（蓝本 loaded_session_id unwrap_or("unknown")）。</summary>
    public const string FallbackSessionId = "unknown";

    /// <summary>DateTimeOffset.FromUnixTimeMilliseconds 的可表示上限（9999-12-31）；I-006 同款防御边界。</summary>
    internal const long MaxUnixMilliseconds = 253_402_300_799_999;

    /// <summary>
    /// 映射 v1 `message` 表 / legacy JSON 载荷（蓝本 message_value_to_entry：列值 id / session_id
    /// 优先于载荷内字段）。data 载荷非法 JSON 或非对象时抛 <see cref="JsonException"/> 由调用方计数
    /// （蓝本 serde_json::from_str 失败 → continue）。
    /// </summary>
    public static (UsageRecord? Record, OpenCodeRowRejection Rejection) MapMessage(
        string data, string? columnId, string? columnSessionId)
    {
        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("OpenCode message payload is not a JSON object.");
        }

        return MapMessage(document.RootElement, columnId, columnSessionId);
    }

    /// <summary>映射 v2 `session_message` 载荷（蓝本 session_message_value_to_entry → into_legacy_message）：
    /// model 嵌套对象的 id/modelID/providerID 优先于顶层字段；载荷 time.created 缺失时回退列值 created。</summary>
    public static (UsageRecord? Record, OpenCodeRowRejection Rejection) MapSessionMessage(
        string data, string columnId, string columnSessionId, long created)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("OpenCode v2 message payload is not a JSON object.");
        }

        var model = LenientJson.GetObject(root, "model");
        var modelId = model is { } modelObject
            ? LenientJson.GetNonEmptyString(modelObject, "id")
                ?? LenientJson.GetNonEmptyString(modelObject, "modelID")
                ?? LenientJson.GetNonEmptyString(root, "modelID")
            : LenientJson.GetNonEmptyString(root, "modelID");
        var providerId = model is { } providerObject
            ? LenientJson.GetNonEmptyString(providerObject, "providerID")
                ?? LenientJson.GetNonEmptyString(root, "providerID")
            : LenientJson.GetNonEmptyString(root, "providerID");
        var createdFromPayload = GetTimeCreated(root);
        var effectiveCreated = createdFromPayload ?? created;

        return MapCore(
            tokens: LenientJson.GetObject(root, "tokens"),
            modelId: modelId,
            providerId: providerId,
            messageId: columnId,
            sessionId: columnSessionId,
            storedCost: LenientJson.GetF64(root, "cost"),
            created: effectiveCreated);
    }

    private static (UsageRecord? Record, OpenCodeRowRejection Rejection) MapMessage(
        JsonElement payload, string? columnId, string? columnSessionId)
    {
        return MapCore(
            tokens: LenientJson.GetObject(payload, "tokens"),
            modelId: LenientJson.GetNonEmptyString(payload, "modelID"),
            providerId: LenientJson.GetNonEmptyString(payload, "providerID"),
            messageId: columnId ?? LenientJson.GetNonEmptyString(payload, "id"),
            sessionId: columnSessionId ?? LenientJson.GetNonEmptyString(payload, "sessionID"),
            storedCost: LenientJson.GetF64(payload, "cost"),
            created: GetTimeCreated(payload) ?? 0);
    }

    /// <summary>载荷 time.created（lenient_i64）；缺失或非整数返回 null（蓝本 open_code_timestamp 的 None 分支）。</summary>
    private static long? GetTimeCreated(JsonElement payload)
    {
        return LenientJson.GetObject(payload, "time") is { } time
            ? LenientJson.GetI64(time, "created")
            : null;
    }

    private static (UsageRecord? Record, OpenCodeRowRejection Rejection) MapCore(
        JsonElement? tokens, string? modelId, string? providerId,
        string? messageId, string? sessionId, double? storedCost, long created)
    {
        if (tokens is not { } tokensElement)
        {
            return (null, OpenCodeRowRejection.NoUsagePayload);
        }

        var cache = LenientJson.GetObject(tokensElement, "cache");
        // 蓝本全程 u64 + saturating；读取层把 u64 溢出钳到 long.MaxValue（token 计数不可达域）。
        var inputTokens = LenientJson.GetU64(tokensElement, "input");
        var outputTokens = LenientJson.GetU64(tokensElement, "output");
        var cacheCreationTokens = cache is { } cacheElement ? LenientJson.GetU64(cacheElement, "write") : 0;
        var cacheReadTokens = cache is { } readElement ? LenientJson.GetU64(readElement, "read") : 0;
        var totalTokens = LenientJson.GetU64(tokensElement, "total");

        // apply_total_token_fallback：reasoning 先记入 extra，total 超出分量和的余量补入
        // （output 为 0 时优先补 output，否则累加到 extra）。
        var extraTotalTokens = LenientJson.GetU64(tokensElement, "reasoning");
        var knownTokens = SaturatingAdd(SaturatingAdd(SaturatingAdd(SaturatingAdd(inputTokens, outputTokens), cacheCreationTokens), cacheReadTokens), extraTotalTokens);
        var missingTokens = totalTokens > knownTokens ? SaturatingSubtract(totalTokens, knownTokens) : 0;
        if (missingTokens > 0)
        {
            if (outputTokens == 0)
            {
                outputTokens = missingTokens;
            }
            else
            {
                extraTotalTokens = SaturatingAdd(extraTotalTokens, missingTokens);
            }
        }

        if (inputTokens == 0 && outputTokens == 0 && cacheCreationTokens == 0 && cacheReadTokens == 0 && extraTotalTokens == 0)
        {
            return (null, OpenCodeRowRejection.NoUsagePayload);
        }

        if (string.IsNullOrEmpty(modelId) || string.IsNullOrEmpty(providerId))
        {
            return (null, OpenCodeRowRejection.MissingIdentity);
        }

        if (created > MaxUnixMilliseconds)
        {
            return (null, OpenCodeRowRejection.TimestampOutOfRange);
        }

        // 统一记录无 extra_total_tokens 字段：reasoning 与 total 余量并入 output（I-006 口径）。
        // 蓝本 cost_usage 同样把 extra 按输出价计费，成本一致；显示口径偏差见卡片 Dev Log。
        // 计价走蓝本 open_code_model_candidates 候选链（I-024 接线）：别名解析 → 归一化 →
        // provider 前缀变体，逐候选首个命中生效；分组/显示仍用原始 modelId。
        return (new UsageRecord
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(created > 0 ? created : 0),
            Agent = AgentName,
            ProjectPath = ProjectPath,
            SessionId = string.IsNullOrEmpty(sessionId) ? FallbackSessionId : sessionId,
            Model = modelId,
            Pricing = new UsagePricingPlan(BuildModelCandidates(modelId, providerId!)),
            // 消息自带正 cost 直接采用（蓝本 cost_usd.filter(> 0)），否则交定价引擎（null）。
            CostUsd = ToDecimalCost(storedCost),
            InputTokens = inputTokens,
            OutputTokens = SaturatingAdd(outputTokens, extraTotalTokens),
            CacheCreationTokens = cacheCreationTokens,
            CacheReadTokens = cacheReadTokens,
            MessageId = messageId,
        }, OpenCodeRowRejection.None);
    }

    /// <summary>消息自带成本 → decimal（正且有限才生效；蓝本 cost_usd.filter(|cost| *cost > 0.0)）。</summary>
    internal static decimal? ToDecimalCost(double? cost)
    {
        if (cost is not { } value || !double.IsFinite(value) || value <= 0)
        {
            return null;
        }

        // SQLite REAL（f64）→ decimal：用最短往返文本解析保持常见值（0.02）的精确十进制形态。
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

    /// <summary>
    /// 计价模型候选（蓝本 open_code_model_candidates）：别名解析 → 归一化 → [原名, 归一化名]
    /// 追加 provider 前缀变体（'-' → '_'），相邻去重。CLI 逐候选尝试计价，首个正成本生效。
    /// </summary>
    public static IReadOnlyList<string> BuildModelCandidates(string model, string provider)
    {
        var resolved = ResolveModelName(model);
        var normalized = NormalizeModelName(resolved);
        var baseCandidates = new List<string> { resolved };
        if (!string.Equals(normalized, resolved, StringComparison.Ordinal))
        {
            baseCandidates.Add(normalized);
        }

        var candidates = new List<string>(baseCandidates);
        if (!string.Equals(provider, "unknown", StringComparison.Ordinal))
        {
            var providerKey = provider.Replace('-', '_');
            foreach (var candidate in baseCandidates)
            {
                candidates.Add($"{providerKey}/{candidate}");
            }
        }

        for (var index = candidates.Count - 1; index > 0; index--)
        {
            if (string.Equals(candidates[index], candidates[index - 1], StringComparison.Ordinal))
            {
                candidates.RemoveAt(index);
            }
        }

        return candidates;
    }

    /// <summary>OpenCode 特有模型别名（蓝本 resolve_open_code_model_name）。</summary>
    public static string ResolveModelName(string model)
    {
        return model switch
        {
            "gemini-3-pro-high" => "gemini-3-pro-preview",
            "k2p6" => "kimi-k2.6",
            _ => model,
        };
    }

    /// <summary>
    /// Claude 家族点分版本号归一化（蓝本 normalize_open_code_model_name）：
    /// claude-{haiku,opus,sonnet}- 后的 "4.5" → "4-5"（含尾缀跟随），"45" → "4-5"（两位数字拆分）；
    /// 其余形态原样返回。
    /// </summary>
    public static string NormalizeModelName(string model)
    {
        foreach (var family in (string[])["claude-haiku-", "claude-opus-", "claude-sonnet-"])
        {
            if (!model.StartsWith(family, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = model[family.Length..];
            var dotIndex = rest.IndexOf('.');
            if (dotIndex > 0
                && IsAllAsciiDigits(rest[..dotIndex])
                && rest.Length > dotIndex + 1
                && IsAsciiDigit(rest[dotIndex + 1]))
            {
                return $"{family}{rest[..dotIndex]}-{rest[(dotIndex + 1)..]}";
            }

            if (rest.Length >= 2 && IsAsciiDigit(rest[0]) && IsAsciiDigit(rest[1]))
            {
                return $"{family}{rest[0]}-{rest[1..]}";
            }
        }

        return model;
    }

    private static bool IsAllAsciiDigits(string value)
    {
        return value.Length > 0 && value.All(IsAsciiDigit);
    }

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';

    private static long SaturatingAdd(long left, long right)
    {
        var sum = unchecked(left + right);
        return sum < 0 ? long.MaxValue : sum;
    }

    private static long SaturatingSubtract(long left, long right) => Math.Max(0, left - right);
}
