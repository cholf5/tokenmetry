namespace CCUsage.Reconciliation.Tests.Comparison;

/// <summary>
/// 对账比较的单条聚合口径：一个 日期 × 模型 组合（Model 为 null 表示该日的总计行）。
/// tokens 五个分量逐项比较（精确一致）；CostUsd 允许为 null 表示"对方不提供该口径的成本"
/// （如 ccusage codex daily 的 per-model 输出没有成本），比较器对 null 成本跳过比较。
/// </summary>
public sealed record ReconciliationEntry(
    string Date,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long TotalTokens,
    decimal? CostUsd)
{
    /// <summary>比较用键：无模型记录（&lt;synthetic&gt; 等）归入空串键。</summary>
    public string ModelKey => Model ?? string.Empty;

    public string DisplayName => Model is null ? Date : $"{Date} {Model}";
}

/// <summary>
/// 对账容差（Q-001 验收口径）：tokens 精确一致；cost 相对误差 ≤ 0.5%，
/// 且绝对误差 &lt; $0.01 时视为通过（避免小额日期的相对误差噪声）。
/// </summary>
public sealed record ReconciliationTolerance(decimal CostRelativeTolerance, decimal CostAbsoluteToleranceUsd)
{
    public static readonly ReconciliationTolerance Default = new(CostRelativeTolerance: 0.005m, CostAbsoluteToleranceUsd: 0.01m);
}
