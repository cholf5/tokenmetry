namespace Tokenmetry.Core.Models;

/// <summary>
/// 用户价格覆盖规则（I-003）：exact 模型名匹配，优先级高于 models.dev 远程数据与内置快照。
/// 持久化在 <see cref="AppSettings.PricingOverrides"/>（settings.json）。
/// cache 单价缺省时按蓝本规则以 input 推导（× 1.25 写 / × 0.1 读）。
/// </summary>
public sealed class ModelPricingOverride
{
    /// <summary>覆盖的模型名（exact 匹配，忽略大小写；"-fast" 等采集标记后缀会被剥离后匹配）。</summary>
    public string Model { get; set; } = string.Empty;

    public decimal InputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }

    /// <summary>cache_creation（5m）单价；null 时按 input × 1.25 推导。</summary>
    public decimal? CacheWritePerMillion { get; set; }

    /// <summary>cache_read 单价；null 时按 input × 0.1 推导。</summary>
    public decimal? CacheReadPerMillion { get; set; }

    /// <summary>转换为计价条目（补齐 cache 单价缺省值）；无效规则（模型名空白或负价）返回 null。</summary>
    public ModelPriceEntry? ToPriceEntry()
    {
        var model = Model.Trim();
        if (model.Length == 0 || InputPerMillion < 0m || OutputPerMillion < 0m)
        {
            return null;
        }

        var cacheWrite = CacheWritePerMillion;
        var cacheRead = CacheReadPerMillion;
        if (cacheWrite is { } write && write < 0m)
        {
            return null;
        }

        if (cacheRead is { } read && read < 0m)
        {
            return null;
        }

        return new ModelPriceEntry
        {
            ModelId = model,
            Provider = "user",
            InputPerMillion = InputPerMillion,
            OutputPerMillion = OutputPerMillion,
            CacheWritePerMillion = cacheWrite ?? InputPerMillion * ModelPriceEntry.DefaultCacheWriteMultiplier,
            CacheReadPerMillion = cacheRead ?? InputPerMillion * ModelPriceEntry.DefaultCacheReadMultiplier,
        };
    }
}
