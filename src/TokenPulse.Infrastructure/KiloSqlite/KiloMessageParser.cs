using System.Globalization;
using System.Text.Json;
using TokenPulse.Core.Models;
using TokenPulse.Infrastructure.SqliteSource;

namespace TokenPulse.Infrastructure.KiloSqlite;

/// <summary>行级拒绝原因（对应蓝本 message_value_to_entry 返回 None 的各分支，用于诊断计数）。</summary>
public enum KiloRowRejection
{
    /// <summary>成功产出记录。</summary>
    None,

    /// <summary>非 assistant 行 / 无 tokens 载荷 / 四桶+extra 全零——非 usage 行，正常跳过。</summary>
    NoUsagePayload,

    /// <summary>有 usage 载荷但 modelID 缺失（蓝本静默丢弃，此处计 InvalidRecords）。</summary>
    MissingIdentity,

    /// <summary>有 usage 载荷但 time.created 缺失 / 非正 / 秒×1000 溢出（蓝本静默丢弃，此处计数）。</summary>
    UnparsableTimestamp,

    /// <summary>时间戳超出 DateTimeOffset 可表示范围——实际数据不可达的防御边界。</summary>
    TimestampOutOfRange,
}

/// <summary>
/// Kilo message 载荷 → 统一 usage 记录的映射（I-017，逐条移植蓝本
/// ../ccusage/rust/adapters/kilo/src/parser.rs 的 KiloMessage / message_value_to_entry /
/// normalize_timestamp / model_candidates；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。
/// JSON 字段读取为共享 <see cref="LenientJson"/>（蓝本 jsonl.rs lenient 助手等价移植）；
/// role 为普通字符串、与 "assistant" **原样精确比较**（不 trim，蓝本 Option&lt;String&gt; 同口径）。
/// 映射语义：tokens.cache.write/read → cache_creation/read；reasoning 与 tokens.total 超出分量和的余量
/// 按蓝本 apply_total_token_fallback 处理（output 为 0 时优先补入 output）后并入 OutputTokens
/// （I-006 系口径，成本一致）；四桶与 extra 全零的行跳过；有载荷但缺 modelID 或时间不可用 → 拒绝；
/// time.created ≤ 0 → 跳过（蓝本 normalize_timestamp 的 None 分支）、&lt;1e12 按秒 ×1000（checked）；
/// session id / message id 回退：载荷缺失时用列值，message id 最终回退 "&lt;db 路径&gt;:&lt;row id&gt;"
/// （蓝本同式，保证无内嵌 id 的行仍可去重）；成本（蓝本 CostMode::Auto 语义）：载荷 cost 存在即采用
/// （含 0——与 OpenCode 的 &gt;0 过滤不同，Kilo 的 Auto 对 Some(0.0) 记 0 不再计价），缺失才交定价引擎；
/// provider 归一化与计价候选（qualified 仅在 find_exact 命中时插入，unknown/kilo 兜底不限定）移植为
/// 纯函数，I-024 起接入映射产物 <see cref="UsageRecord.Pricing"/>（精确门槛由引擎匹配承载，见
/// MapMessage 内注释；分组/显示仍用裸 modelID）。
/// </summary>
public static class KiloMessageParser
{
    /// <summary>统一记录的采集来源名与项目路径（蓝本 UsageLoadAgent("Kilo") / project_path="Kilo"）。</summary>
    public const string AgentName = "Kilo";
    public const string ProjectPath = "Kilo";

    /// <summary>纯数字秒/毫秒判别阈值（蓝本 1_000_000_000_000：小于它按秒 ×1000）。</summary>
    internal const long MillisScaleThreshold = 1_000_000_000_000;

    /// <summary>DateTimeOffset.FromUnixTimeMilliseconds 的可表示上限（9999-12-31）；I-006 同款防御边界。</summary>
    internal const long MaxUnixMilliseconds = 253_402_300_799_999;

    /// <summary>
    /// 映射 message 表行（蓝本 message_value_to_entry：列值 id / session_id 为载荷缺失时的回退，
    /// db 路径参与 message id 回退）。data 载荷非法 JSON 或非对象时抛 <see cref="JsonException"/>
    /// 由调用方计数（蓝本 serde_json::from_str 失败 → continue）。
    /// </summary>
    public static (UsageRecord? Record, KiloRowRejection Rejection) MapMessage(
        string data, string rowId, string rowSessionId, string databasePath)
    {
        using var document = JsonDocument.Parse(data);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Kilo message payload is not a JSON object.");
        }

        return MapMessage(document.RootElement, rowId, rowSessionId, databasePath);
    }

    private static (UsageRecord? Record, KiloRowRejection Rejection) MapMessage(
        JsonElement payload, string rowId, string rowSessionId, string databasePath)
    {
        // 蓝本 role: Option<String> 原样比较（不 trim）。
        if (!TryGetString(payload, "role", out var role) || role != "assistant")
        {
            return (null, KiloRowRejection.NoUsagePayload);
        }

        if (LenientJson.GetObject(payload, "tokens") is not { } tokens)
        {
            return (null, KiloRowRejection.NoUsagePayload);
        }

        var cache = LenientJson.GetObject(tokens, "cache");
        // 蓝本全程 u64 + saturating；读取层把 u64 溢出钳到 long.MaxValue（token 计数不可达域）。
        var inputTokens = LenientJson.GetU64(tokens, "input");
        var outputTokens = LenientJson.GetU64(tokens, "output");
        var cacheCreationTokens = cache is { } cacheElement ? LenientJson.GetU64(cacheElement, "write") : 0;
        var cacheReadTokens = cache is { } readElement ? LenientJson.GetU64(readElement, "read") : 0;
        var totalTokens = LenientJson.GetU64(tokens, "total");

        // apply_total_token_fallback：reasoning 先记入 extra，total 超出分量和的余量补入
        // （output 为 0 时优先补 output，否则累加到 extra）。
        var extraTotalTokens = LenientJson.GetU64(tokens, "reasoning");
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
            return (null, KiloRowRejection.NoUsagePayload);
        }

        if (LenientJson.GetNonEmptyString(payload, "modelID") is not { } modelId)
        {
            return (null, KiloRowRejection.MissingIdentity);
        }

        // 蓝本 normalize_timestamp：≤ 0 / 缺失 → None（静默跳过）；<1e12 按秒 ×1000（checked）。
        var created = LenientJson.GetObject(payload, "time") is { } timeElement
            ? LenientJson.GetI64(timeElement, "created")
            : null;
        if (!TryNormalizeTimestamp(created, out var timestamp))
        {
            return (null, KiloRowRejection.UnparsableTimestamp);
        }

        if (timestamp > MaxUnixMilliseconds)
        {
            return (null, KiloRowRejection.TimestampOutOfRange);
        }

        var sessionId = LenientJson.GetNonEmptyString(payload, "session_id") ?? rowSessionId;
        var messageId = LenientJson.GetNonEmptyString(payload, "id") ?? $"{databasePath}:{rowId}";
        var storedCost = LenientJson.GetF64(payload, "cost");
        // 蓝本 message_value_to_entry 的 provider = value.provider_id（Option<String>），I-024 起参与
        // 计价候选链（qualified 仅在精确命中时可能生效，门槛由引擎匹配承载）；分组/显示仍用 modelID。
        var providerId = LenientJson.GetNonEmptyString(payload, "providerID");

        return (new UsageRecord
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
            Agent = AgentName,
            ProjectPath = ProjectPath,
            SessionId = sessionId,
            Model = modelId,
            // 计价走蓝本 model_candidates 候选链（I-024 接线）：provider 限定名（仅精确门槛）+ 裸名。
            Pricing = new UsagePricingPlan(BuildModelCandidates(modelId, providerId, _ => true)),
            // 蓝本 CostMode::Auto：cost 存在即采用（含 0），缺失才交定价引擎。
            CostUsd = ToStoredCost(storedCost),
            InputTokens = inputTokens,
            // 统一记录无 extra_total_tokens 字段：reasoning 与 total 余量并入 output（I-006 系口径，
            // 蓝本计价同样把 extra 按输出价计费，成本一致；显示口径偏差见卡片 Dev Log）。
            OutputTokens = SaturatingAdd(outputTokens, extraTotalTokens),
            CacheCreationTokens = cacheCreationTokens,
            CacheReadTokens = cacheReadTokens,
            MessageId = messageId,
        }, KiloRowRejection.None);
    }

    /// <summary>蓝本 normalize_timestamp：≤ 0 / 缺失无效；&lt;1e12 按秒 ×1000（checked 溢出无效）；否则原样毫秒。</summary>
    public static bool TryNormalizeTimestamp(long? created, out long millis)
    {
        millis = 0;
        if (created is not { } value || value <= 0)
        {
            return false;
        }

        try
        {
            millis = value < MillisScaleThreshold ? checked(value * 1000) : value;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>载荷 cost → decimal（蓝本 Auto 语义：存在即采用，含 0；非有限按缺失）。</summary>
    internal static decimal? ToStoredCost(double? cost)
    {
        if (cost is not { } value || !double.IsFinite(value))
        {
            return null;
        }

        // SQLite REAL（f64）→ decimal：用最短往返文本解析保持常见值（0.02）的精确十进制形态。
        if (decimal.TryParse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
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
    /// 计价模型候选（蓝本 model_candidates）：provider 归一化（'-'→'_'）后非 unknown/kilo 且限定名能
    /// find_exact 命中（由 <paramref name="hasExactPrice"/> 判定，**无**点分归一化——比 Hermes 的
    /// exact-with-fallback 更严）时先插限定名，再插裸模型名，去重保序。
    /// </summary>
    public static IReadOnlyList<string> BuildModelCandidates(
        string model, string? provider, Func<string, bool> hasExactPrice)
    {
        var candidates = new List<string>();
        var normalizedProvider = provider is null ? null : provider.Replace('-', '_');
        if (normalizedProvider is { } providerKey
            && providerKey != "unknown"
            && providerKey != "kilo")
        {
            var qualified = $"{providerKey}/{model}";
            if (hasExactPrice(qualified))
            {
                candidates.Add(qualified);
            }
        }

        candidates.Add(model);
        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>role 读取（蓝本 Option&lt;String&gt;：字符串原样，非字符串/缺失 → null）。</summary>
    private static bool TryGetString(JsonElement parent, string name, out string? value)
    {
        if (parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        value = null;
        return false;
    }

    private static long SaturatingAdd(long left, long right)
    {
        var sum = unchecked(left + right);
        return sum < 0 ? long.MaxValue : sum;
    }

    private static long SaturatingSubtract(long left, long right) => Math.Max(0, left - right);
}
