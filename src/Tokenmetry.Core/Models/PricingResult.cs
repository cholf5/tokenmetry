namespace Tokenmetry.Core.Models;

/// <summary>
/// 计价结果来源（I-003）。Missing = 0，使默认值即"未计价"。
/// </summary>
public enum PricingSourceKind
{
    /// <summary>价格未命中：没有可用的模型单价，成本未知（不是 0）。</summary>
    Missing,

    /// <summary>日志自带 costUSD，直接采用（对齐蓝本 claude.rs price_entry 的 precomputed 分支）。</summary>
    LogCost,

    /// <summary>用户覆盖规则（exact 模型名）命中，优先级最高。</summary>
    UserOverride,

    /// <summary>models.dev 远程数据命中（覆盖快照）。</summary>
    Remote,

    /// <summary>内置价格快照命中（离线兜底）。</summary>
    Snapshot,
}

/// <summary>
/// 一次计价的结果。<see cref="CostUsd"/> 为 null 表示未计价（明确区分于 0），
/// 由 <see cref="PricingSourceKind"/> 说明原因；未命中模型同时会记入定价引擎的未命中清单。
/// </summary>
public sealed class PricingResult
{
    public PricingSourceKind Source { get; init; } = PricingSourceKind.Missing;

    /// <summary>成本（USD）；未计价时为 null。</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>计价输入的原始模型名。</summary>
    public string? Model { get; init; }

    /// <summary>命中的价格键（"provider/modelId" 或覆盖模型名）；未命中为 null。</summary>
    public string? MatchedKey { get; init; }

    /// <summary>是否完成计价（命中任一价格来源）。</summary>
    public bool IsPriced => Source != PricingSourceKind.Missing && CostUsd is not null;
}

/// <summary>未命中价格的模型汇总（供对账测试与诊断）：模型名 → 记录数与 token 合计。</summary>
public sealed class UnpricedModelInfo
{
    public UnpricedModelInfo(string model, long recordCount, long totalTokens)
    {
        Model = model;
        RecordCount = recordCount;
        TotalTokens = totalTokens;
    }

    public string Model { get; }

    /// <summary>未计价的记录条数。</summary>
    public long RecordCount { get; }

    /// <summary>未计价记录的 token 合计（与 ccusage 口径一致的总 token）。</summary>
    public long TotalTokens { get; }
}
