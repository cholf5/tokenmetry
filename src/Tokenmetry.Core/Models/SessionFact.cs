namespace Tokenmetry.Core.Models;

/// <summary>
/// session_facts 表的一行：会话级用量事实（I-002），为 v0.4 Monthly / Session tab 预留。
/// 以 session_id 为主键 upsert：同一会话重复扫描更新数值而非插新行。
/// </summary>
public class SessionFact
{
    /// <summary>会话 id（统一记录模型保证非空，日志体缺失时为文件名推导值）。</summary>
    public string SessionId { get; set; } = string.Empty;

    public string Agent { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string ProjectDisplayName { get; set; } = string.Empty;

    /// <summary>会话内 token 占比最高的模型；全部无模型时为空串。</summary>
    public string DominantModel { get; set; } = string.Empty;

    /// <summary>会话内最早 usage 事件时间。</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>会话内最晚 usage 事件时间。</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long CacheCreationTokens { get; set; }

    public long CacheCreationEphemeral5mTokens { get; set; }

    public long CacheCreationEphemeral1hTokens { get; set; }

    public long CacheReadTokens { get; set; }

    public long TotalTokens { get; set; }

    /// <summary>已知成本合计（日志自带 costUSD 之和）；全部未知时为 null。</summary>
    public decimal? CostUsd { get; set; }
}
