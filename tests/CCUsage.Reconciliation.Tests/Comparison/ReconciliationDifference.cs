namespace CCUsage.Reconciliation.Tests.Comparison;

public enum ReconciliationDifferenceKind
{
    /// <summary>golden（CLI）有而我们的链路没有的 日期 × 模型 条目。</summary>
    MissingInOurs,

    /// <summary>我们的链路有而 golden 没有的条目。</summary>
    MissingInGolden,

    /// <summary>某 token 分量不一致（tokens 要求精确一致）。</summary>
    TokenMismatch,

    /// <summary>超出容差的成本差异（token 完全一致时即为定价差异）。</summary>
    CostMismatch,
}

public enum ReconciliationCause
{
    /// <summary>token 口径不一致：解析遗漏/重复、去重规则、记录被对方丢弃等。</summary>
    Parsing,

    /// <summary>token 完全一致但成本超容差：价格表（LiteLLM vs models.dev）或计价规则差异。</summary>
    Pricing,
}

/// <summary>一条对账差异明细：定位到 日期 × 模型 × 字段，并给出差异量与相对偏差。</summary>
public sealed record ReconciliationDifference(
    ReconciliationDifferenceKind Kind,
    ReconciliationCause Cause,
    string Date,
    string? Model,
    string? Field,
    string GoldenValue,
    string OursValue,
    string Delta,
    decimal? RelativeDeviation)
{
    public string DisplayName => Model is null ? Date : $"{Date} {Model}";

    public string KindLabel => Kind switch
    {
        ReconciliationDifferenceKind.MissingInOurs => "缺失·我们",
        ReconciliationDifferenceKind.MissingInGolden => "缺失·CLI",
        ReconciliationDifferenceKind.TokenMismatch => $"token·{Field}",
        ReconciliationDifferenceKind.CostMismatch => "cost",
        _ => Kind.ToString(),
    };

    public string Hint => Cause switch
    {
        ReconciliationCause.Parsing
            => "解析/覆盖差异：token 口径不一致，排查解析器、消息级去重或被 CLI 丢弃的记录形态（如 message.id 缺失、显式 costUSD:null）",
        ReconciliationCause.Pricing
            => "定价差异：成本口径或价格表差异，优先排查价格表（LiteLLM vs models.dev 快照）与计价规则（cache write 倍率等）；若 token 也有差异，先解决解析差异再复核成本",
        _ => string.Empty,
    };
}
