using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.ZCodeSqlite;

/// <summary>
/// ZCode 账本行 → 统一 usage 记录的映射（I-006，逐条移植蓝本 zcode/src/parser.rs row_to_entry）：
/// - 身份与时间门槛：id / session_id / model_id 去除空白后非空、started_at &gt; 0，否则整行作废；
/// - token 归一化：ZCode 的 input_tokens 含 cache 读写两个桶，cache_read 截断至 input、
///   cache_creation 截断至剩余量，input 报告扣除两桶后的新内容量（负值钳到 0）；
/// - computed_total_tokens 超出分量和的余量并入 OutputTokens（蓝本把余量记入 extra_total_tokens
///   单独参与计价与总额；统一记录无该字段，并入 output 使聚合总额与蓝本一致，逐桶 output 显示
///   在出现余量的行上会包含余量——已记录的口径偏差）；
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

    public static UsageRecord? Map(ZCodeUsageRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Id)
            || string.IsNullOrWhiteSpace(row.SessionId)
            || string.IsNullOrWhiteSpace(row.ModelId)
            || row.StartedAt <= 0)
        {
            return null;
        }

        // token 计数按蓝本为非负；负值视为读取异常行（蓝本 i64→u64 转换失败整行跳过）。
        if (row.InputTokens < 0
            || row.OutputTokens < 0
            || row.CacheCreationInputTokens < 0
            || row.CacheReadInputTokens < 0
            || row.ComputedTotalTokens < 0)
        {
            return null;
        }

        var cacheRead = Math.Min(row.CacheReadInputTokens, row.InputTokens);
        var cacheCreation = Math.Min(row.CacheCreationInputTokens, row.InputTokens - cacheRead);
        var inputTokens = row.InputTokens - cacheRead - cacheCreation;
        var outputTokens = row.OutputTokens;
        var componentSum = inputTokens + outputTokens + cacheCreation + cacheRead;
        var extraTotalTokens = Math.Max(0, row.ComputedTotalTokens - componentSum);
        if (componentSum == 0 && extraTotalTokens == 0)
        {
            return null;
        }

        var rawModel = row.ModelId.Trim();
        var providerId = row.ProviderId;
        var isZai = IsZaiProvider(providerId)
                    || (providerId is null && IsZaiModel(rawModel));
        // 统一记录无 extra_total_tokens 字段：余量并入 output，聚合总额（input+output+cache 两桶）
        // 与蓝本 display 总额一致；蓝本计价同样把余量计入 output 价。
        outputTokens += extraTotalTokens;
        if (isZai)
        {
            // Z.ai：cache_creation 展示为独立桶但按标准 input 价计费——并入 input 保证与
            // 蓝本 cost_usage 公式逐 token 一致（显示桶让位，代价为 0 差异已并入）。
            inputTokens += cacheCreation;
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
}
