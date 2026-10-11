using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.ZCodeSqlite;

/// <summary>
/// ZCode 账本行 → 统一 usage 记录的映射（I-006，逐条移植蓝本 zcode/src/parser.rs row_to_entry）：
/// - 身份与时间门槛：id / session_id / model_id 去除空白后非空、started_at &gt; 0，否则整行作废；
/// - token 分量由调用方（读取层）保证非负（蓝本 read_token_column 钳 0 后的 u64，解析层无负值分支）；
/// - computed_total_tokens 超出分量和的余量并入 OutputTokens（蓝本把余量记入 extra_total_tokens
///   单独参与计价与总额；统一记录无该字段，并入 output 使聚合总额与蓝本一致——已记录的口径偏差）；
/// - 全零行（分量和与余量均为 0）作废；
/// - 计价门槛（蓝本 pricing_model / is_zai_provider）：Z.ai 限定 provider（或无 provider 的 glm-*
///   行）走定价引擎（CostUsd=null），且因 Z.ai 把新内容以 cache_creation 桶展示但按标准 input 价
///   计费，映射时把 cache_creation 并入 input（总量不变）；其余 provider（内部 UUID 等自定义源）
///   CostUsd 记 0，与发布版 ccusage CLI 的 missingPricing / totalCost 0 口径一致。
/// </summary>
public static class ZCodeRecordMapper
{
    /// <summary>ZCode 无项目目录时的项目回退名（蓝本 row_to_entry）。</summary>
    public const string FallbackProjectPath = "ZCode";

    /// <summary>统一记录的采集来源名（蓝本进度标签 UsageLoadAgent("ZCode")）。</summary>
    public const string AgentName = "ZCode";

    /// <summary>
    /// DateTimeOffset.FromUnixTimeMilliseconds 的可表示上限（9999-12-31）。
    /// 蓝本把任意 i64 交给 jiff（年域更宽）；超出部分按行级无效处理——实际数据不可达的防御边界。
    /// </summary>
    internal const long MaxUnixMilliseconds = 253_402_300_799_999;

    public static UsageRecord? Map(ZCodeUsageRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Id)
            || string.IsNullOrWhiteSpace(row.SessionId)
            || string.IsNullOrWhiteSpace(row.ModelId)
            || row.StartedAt <= 0)
        {
            return null;
        }

        var cacheRead = Math.Min(row.CacheReadInputTokens, row.InputTokens);
        var cacheCreation = Math.Min(row.CacheCreationInputTokens, SaturatingSubtract(row.InputTokens, cacheRead));
        var inputTokens = SaturatingSubtract(SaturatingSubtract(row.InputTokens, cacheRead), cacheCreation);
        var outputTokens = row.OutputTokens;
        var componentSum = SaturatingAdd(SaturatingAdd(SaturatingAdd(inputTokens, outputTokens), cacheCreation), cacheRead);
        var extraTotalTokens = row.ComputedTotalTokens > componentSum
            ? SaturatingSubtract(row.ComputedTotalTokens, componentSum)
            : 0;
        if (componentSum == 0 && extraTotalTokens == 0)
        {
            return null;
        }

        if (row.StartedAt > MaxUnixMilliseconds)
        {
            return null;
        }

        var rawModel = row.ModelId.Trim();
        var providerId = row.ProviderId;
        var isZai = IsZaiProvider(providerId)
                    || (providerId is null && IsZaiModel(rawModel));
        // 统一记录无 extra_total_tokens 字段：余量并入 output，聚合总额（input+output+cache 两桶）
        // 与蓝本 display 总额一致；蓝本计价同样把余量计入 output 价。
        outputTokens = SaturatingAdd(outputTokens, extraTotalTokens);
        if (isZai)
        {
            // Z.ai：cache_creation 展示为独立桶但按标准 input 价计费——并入 input 保证与
            // 蓝本 cost_usage 公式逐 token 一致（显示桶让位，代价为 0 差异已并入）。
            inputTokens = SaturatingAdd(inputTokens, cacheCreation);
            cacheCreation = 0;
        }

        return new UsageRecord
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(row.StartedAt),
            Agent = AgentName,
            ProjectPath = string.IsNullOrWhiteSpace(row.Directory)
                ? FallbackProjectPath
                : row.Directory,
            SessionId = row.SessionId,
            Model = rawModel,
            // ZCode 账本不记逐请求成本：Z.ai 限定行走定价引擎（I-003），自定义 provider 行
            // 记 0（蓝本 pricing_model 对非 Z.ai provider 返回 None → 成本 0 + 缺价警告）。
            CostUsd = isZai ? null : 0m,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheCreationTokens = cacheCreation,
            CacheReadTokens = cacheRead,
            MessageId = row.Id,
        };
    }

    /// <summary>
    /// Z.ai 限定 provider 判定（蓝本 is_zai_provider：显式枚举 + account:zai- / builtin:zai- 前缀）。
    /// 内部 UUID 等自定义 provider 不视为 Z.ai。
    /// </summary>
    public static bool IsZaiProvider(string? providerId)
    {
        if (providerId is null)
        {
            return false;
        }

        var normalized = providerId.Trim().ToLowerInvariant();
        return normalized is "zai" or "z.ai" or "zai-coding-plan" or "builtin:zai-coding-plan" or "builtin:bigmodel-coding-plan"
            || normalized.StartsWith("account:zai-", StringComparison.Ordinal)
            || normalized.StartsWith("builtin:zai-", StringComparison.Ordinal);
    }

    /// <summary>无 provider 的 legacy 行按模型名识别 Z.ai（蓝本 is_zai_model：glm- / glm/ 前缀）。</summary>
    public static bool IsZaiModel(string model)
    {
        return model.StartsWith("glm-", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("glm/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>非负 long 饱和加法（蓝本全程 saturating_add；溢出钳到 long.MaxValue）。</summary>
    private static long SaturatingAdd(long left, long right)
    {
        var sum = unchecked(left + right);
        return sum < 0 ? long.MaxValue : sum;
    }

    private static long SaturatingSubtract(long left, long right) => Math.Max(0, left - right);
}
