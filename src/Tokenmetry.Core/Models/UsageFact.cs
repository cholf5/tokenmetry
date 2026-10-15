namespace Tokenmetry.Core.Models;

/// <summary>
/// usage_facts 行的聚合粒度标记。
/// 统一记录（UsageRecord）聚合出的行是 Full 粒度；CLI 每日汇总（DailyUsageSummary）分解出的行
/// 只知道单一维度的拆分，分别落 Day / Agent / Model / Project 粒度。
/// </summary>
public static class UsageFactGrain
{
    /// <summary>当日合计（agent / model / project 维度均为空）。</summary>
    public const string Day = "day";

    /// <summary>仅 agent 维度已知（来自 CLI 的 agent 名单，无 token / 成本拆分）。</summary>
    public const string Agent = "agent";

    /// <summary>仅 model 维度已知（来自 CLI 的 model breakdown）。</summary>
    public const string Model = "model";

    /// <summary>仅 project 维度已知。</summary>
    public const string Project = "project";

    /// <summary>完整维度组合（day × agent × model × project，来自统一记录聚合）。</summary>
    public const string Full = "full";
}

/// <summary>
/// usage_facts 表的一行：按 day × grain × agent × model × project 聚合的用量事实（I-002）。
/// 同一维度组合重复写入按 upsert 更新数值而非插新行；cost_usd 仅在日志自带 costUSD 时有值，
/// 缺口由定价引擎（I-003）补全。
/// </summary>
public class UsageFact
{
    /// <summary>所在自然日（本地时区分桶，yyyy-MM-dd）。</summary>
    public string Day { get; set; } = string.Empty;

    /// <summary>聚合粒度（见 <see cref="UsageFactGrain"/>）。</summary>
    public string Grain { get; set; } = UsageFactGrain.Full;

    /// <summary>agent 名称；该行不含 agent 维度时为空串。</summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>模型名（已归一化）；无模型（&lt;synthetic&gt; 等）或该行不含 model 维度时为空串。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// 服务商（I-036）：这次请求由谁服务——空串 = 未检测。三元组 = agent × model × provider，
    /// 同一天内同一模型经不同 provider 使用会分成不同的行（不同 provider 价目可能不同）。
    /// 只有 full 粒度行可能带值；CLI 分解行（day / agent / model / project）恒为空串。
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>项目路径；该行不含 project 维度时为空串。</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>项目展示名（可选来源提供；未提供时为空串）。</summary>
    public string ProjectDisplayName { get; set; } = string.Empty;

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long CacheCreationTokens { get; set; }

    /// <summary>cache_creation 的 5 分钟 ephemeral 细分，I-003 计价标记的输入。</summary>
    public long CacheCreationEphemeral5mTokens { get; set; }

    /// <summary>cache_creation 的 1 小时 ephemeral 细分。</summary>
    public long CacheCreationEphemeral1hTokens { get; set; }

    public long CacheReadTokens { get; set; }

    /// <summary>总 token 数（来源口径，与 ccusage 一致）。</summary>
    public long TotalTokens { get; set; }

    /// <summary>已知成本合计（日志自带 costUSD 之和）；全部未知时为 null。</summary>
    public decimal? CostUsd { get; set; }
}
