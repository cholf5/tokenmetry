namespace CCUsage.Core.Models;

/// <summary>
/// 统一 usage 记录：各 agent 数据源适配器（如 Claude Code JSONL 直读）的标准产出，
/// 供 facts 存储（I-002）与定价引擎（I-003）消费。
/// </summary>
public class UsageRecord
{
    /// <summary>usage 事件时间戳（Claude 日志为 RFC3339，统一解析归一化，未知时区按 UTC 处理）。</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>采集来源 agent 名称（如 "Claude Code"）。</summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>项目路径（按日志目录推导，无法推导时为 "Unknown Project"）。</summary>
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>会话 id（日志体缺失时回退到文件名推导值）。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>模型名（已归一化，含 -fast 后缀标记；&lt;synthetic&gt; 等无模型记录为 null）。</summary>
    public string? Model { get; set; }

    /// <summary>
    /// 计价用侧数据（I-024，方案 A）：源适配器按蓝本候选链填充的有序候选 + 匹配走向 +
    /// 计价 output 覆盖，仅供定价引擎使用；null 时按 <see cref="Model"/> 单候选计价（接线前现状）。
    /// 不影响分组 / 显示 / facts 落库（展示口径始终是 <see cref="Model"/>）。
    /// </summary>
    public UsagePricingPlan? Pricing { get; set; }

    /// <summary>日志内建成本（costUSD 字段）；为 null 时由定价引擎（I-003）计算。</summary>
    public decimal? CostUsd { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>cache_creation 总 token 数（有 5m/1h 细分时为两者之和）。</summary>
    public long CacheCreationTokens { get; set; }

    /// <summary>cache_creation 的 5 分钟 ephemeral 细分，I-003 计价标记的输入。</summary>
    public long CacheCreationEphemeral5mTokens { get; set; }

    /// <summary>cache_creation 的 1 小时 ephemeral 细分（I-003 按 1h=2×input 计价的前提）。</summary>
    public long CacheCreationEphemeral1hTokens { get; set; }

    public long CacheReadTokens { get; set; }

    /// <summary>总 token 数（input + output + cache_creation + cache_read，与 ccusage 口径一致）。</summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    /// <summary>是否为 subagent sidechain 会话记录。</summary>
    public bool IsSidechain { get; set; }

    /// <summary>消息级去重标识：assistant message id（部分真实日志为 null）。</summary>
    public string? MessageId { get; set; }

    /// <summary>消息级去重标识：request id（auto 模式下可能为 null）。</summary>
    public string? RequestId { get; set; }

    /// <summary>诊断字段：记录来源的 JSONL 文件完整路径。</summary>
    public string SourceFile { get; set; } = string.Empty;
}
