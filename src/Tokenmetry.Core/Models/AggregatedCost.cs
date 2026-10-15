namespace Tokenmetry.Core.Models;

/// <summary>
/// 聚合成本契约（I-028/I-029 统一口径，未知成本纪律的红线落地）：
/// <see cref="KnownCostUsd"/> 是"已知成本"之和，<see cref="HasUnknownCost"/> 标记聚合范围内
/// 是否存在成本未知的贡献（缺价模型）。未知成本绝不折算为 0 混入合计（对齐 I-024/I-025
/// 收口时确立的 UnpricedModels 纪律：Missing 定价 CostUsd 为 null，不是 0）。
/// 四种组合语义（UI 按此展示，不要只读 <see cref="KnownCostUsd"/>）：
/// <list type="bullet">
/// <item><c>(null, false)</c>——范围内没有任何成本数据（如补零空日、无 facts），既非 0 也非未知。</item>
/// <item><c>(null, true)</c>——全部成本未知（有用量但计价全部缺失）。</item>
/// <item><c>(0, false)</c>——已知成本恰为 0，与未知严格区分。<b>"已知为零"只能来自来源侧的
/// 显式声明</b>：如 ZCode 自定义 provider 行（账本不记逐请求成本，非 Z.ai 行按发布版 CLI 的
/// missingPricing→0 约定落账，属"来源声明零价"），并非我们判定其价格为 0。采集适配器自身的
/// "不可计价"一律走 null 缺价（A-0b），不得折 0 冒充本态。</item>
/// <item><c>(x, true)</c>——部分未知；x 是已知成本下限，实际成本大于等于 x。</item>
/// </list>
/// 边界（I-002 存储语义）：facts 行的 cost_usd 仅在该行全部记录成本未知时为 null，混合行存"已知部分
/// 之和"。因此 <see cref="HasUnknownCost"/> 是可证据化的未知下限——混合行内部隐藏的未知在 facts 层
/// 不可辨；采集侧的权威未知清单是 <c>PricingEngine.GetUnpricedModels()</c>（fail-loud）。
/// </summary>
public sealed class AggregatedCost
{
    /// <summary>空集：没有任何成本数据（既非 0 也非未知）。补零日 / 空范围使用。</summary>
    public static AggregatedCost Empty { get; } = new(null, hasUnknownCost: false);

    public AggregatedCost(decimal? knownCostUsd, bool hasUnknownCost)
    {
        KnownCostUsd = knownCostUsd;
        HasUnknownCost = hasUnknownCost;
    }

    /// <summary>已知成本之和；没有任何已知成本时为 null（不是 0——0 是"已知为零"，null 是"不知道"）。</summary>
    public decimal? KnownCostUsd { get; }

    /// <summary>聚合范围内是否存在成本未知的贡献（缺价模型）。true 时展示层应标注"含未计价模型"。</summary>
    public bool HasUnknownCost { get; }

    /// <summary>范围内既无已知成本也无未知标记（无任何成本数据）。</summary>
    public bool IsEmpty => KnownCostUsd is null && !HasUnknownCost;

    /// <summary>
    /// 从 facts 行的成本列聚合（行级语义：null = 该行全部成本未知）。
    /// 已知值求和（无一已知时 null），任一 null 记 <see cref="HasUnknownCost"/>。
    /// </summary>
    public static AggregatedCost FromRowCosts(IEnumerable<decimal?> rowCosts)
    {
        decimal? known = null;
        var unknown = false;
        foreach (var cost in rowCosts)
        {
            if (cost is { } value)
            {
                known = (known ?? 0m) + value;
            }
            else
            {
                unknown = true;
            }
        }

        return new AggregatedCost(known, unknown);
    }

    /// <summary>两份聚合合并：已知部分相加，未知标记按或合并（未知部分不折 0）。</summary>
    public AggregatedCost Add(AggregatedCost other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var known = KnownCostUsd is { } left && other.KnownCostUsd is { } right
            ? left + right
            : KnownCostUsd ?? other.KnownCostUsd;
        return new AggregatedCost(known, HasUnknownCost || other.HasUnknownCost);
    }

    /// <summary>按精度截断显示用的已知成本（未知标记不变）；百分比展示等场景使用。</summary>
    public AggregatedCost Round(int decimals) =>
        new(KnownCostUsd is { } value ? Math.Round(value, decimals) : null, HasUnknownCost);

    /// <inheritdoc />
    public override string ToString()
    {
        var known = KnownCostUsd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        return HasUnknownCost ? $"{known} + unknown" : known;
    }
}
