namespace Tokenmetry.Core.Models;

/// <summary>
/// 用户模型目录条目（I-035，持久化在 <see cref="AppSettings.ModelDefinitions"/>）：按
/// <see cref="Id"/> 对导入层（内置快照 / models.dev）条目做<b>逐字段覆盖</b>；<see cref="Id"/>
/// 不在导入层时即用户自建模型（新模型 / 自定义源）。
/// 所有字段可空 = 不覆盖（或未提供）：<see cref="HasPriceOverride"/> 决定价格是否覆盖，
/// 元数据（厂商 / 显示名 / 图标）各自独立判定——刷新只更新未被覆盖的字段。
/// </summary>
public sealed class ModelDefinitionOverride
{
    /// <summary>模型 ID（合约定的稳定主键，见 <see cref="ModelId"/>）；无效条目在装订时整条剔除。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Provider 前缀作用域（P-001 T4，可空）：非空时本条目只对"原始名最后一个 '/' 之前的
    /// 完整 provider 路径与此值（忽略大小写）相等"的用量行生效——Lua 局部变量语义，用于给
    /// 非官方渠道（openrouter/z-ai 等中转路径）单独特价；null / 空白 = 裸名条目，命中一切
    /// 归一同名。作用域条目不进全局定义表，绝不冒充裸名 / 官方价。
    /// </summary>
    public string? ProviderScope { get; set; }

    /// <summary>厂商 / 服务商；null / 空白 = 不覆盖。</summary>
    public string? Vendor { get; set; }

    /// <summary>显示名；null / 空白 = 不覆盖。</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// 图标变体（约束到 <c>ModelVisualMap.Vendors</c> 的既有变体：claude / openai / gemini / glm /
    /// deepseek / qwen / kimi / llama / grok / mistral / doubao / minimax / cohere / ernie / hunyuan /
    /// phi / generic）；null = 走内置名推断。用户可把内置推断认不出的模型名指到某个厂商标。
    /// </summary>
    public string? LogoVendor { get; set; }

    public decimal? InputPerMillion { get; set; }

    public decimal? OutputPerMillion { get; set; }

    /// <summary>缓存写入（5m）单价；null 时按 input × <see cref="ModelPriceEntry.DefaultCacheWriteMultiplier"/> 推导。</summary>
    public decimal? CacheWritePerMillion { get; set; }

    /// <summary>缓存读取单价；null 时按 input × <see cref="ModelPriceEntry.DefaultCacheReadMultiplier"/> 推导。</summary>
    public decimal? CacheReadPerMillion { get; set; }

    public long? LongContextThreshold { get; set; }

    public decimal? InputAboveThresholdPerMillion { get; set; }

    public decimal? OutputAboveThresholdPerMillion { get; set; }

    public decimal? CacheWriteAboveThresholdPerMillion { get; set; }

    public decimal? CacheReadAboveThresholdPerMillion { get; set; }

    /// <summary>价格是否构成完整覆盖：input / output 均有值且非负（缺一不算覆盖，避免半截价格冒充已知）。</summary>
    public bool HasPriceOverride =>
        InputPerMillion is { } input && input >= 0m
        && OutputPerMillion is { } output && output >= 0m
        && (CacheWritePerMillion is not { } write || write >= 0m)
        && (CacheReadPerMillion is not { } read || read >= 0m);

    /// <summary>元数据是否构成覆盖（厂商 / 显示名 / 图标任一非空白）。</summary>
    public bool HasMetadataOverride =>
        !string.IsNullOrWhiteSpace(Vendor)
        || !string.IsNullOrWhiteSpace(DisplayName)
        || !string.IsNullOrWhiteSpace(LogoVendor);

    /// <summary>条目是否有效：ID 合约定即可（价格与元数据全空也有效——用户先登记身份、价格后补）。</summary>
    public bool IsValid => ModelId.IsValid(ModelId.Normalize(Id));

    /// <summary>
    /// 装订到导入层定义（无导入条目时传 null 即自建条目）：逐字段取用户值，缺省回落导入值。
    /// 价格覆盖按 <see cref="HasPriceOverride"/> 判定（半截价格整组忽略，不留"部分覆盖"的中间态）。
    /// </summary>
    public ModelDefinition Bind(ModelDefinition? imported)
    {
        var id = ModelId.Normalize(Id);
        var priceOverridden = HasPriceOverride;
        var hasPrice = priceOverridden || imported is { HasPrice: true };

        var input = priceOverridden ? InputPerMillion!.Value : imported?.InputPerMillion ?? 0m;
        var output = priceOverridden ? OutputPerMillion!.Value : imported?.OutputPerMillion ?? 0m;
        // 价格被覆盖且未给 cache 价 → 按既有推导规则（input × 1.25 / × 0.1）补齐，
        // 不用导入层的 cache 价（那是另一个价目体系的数，混用会让同一行的单价来源不可解释）。
        var cacheWrite = priceOverridden
            ? CacheWritePerMillion ?? input * ModelPriceEntry.DefaultCacheWriteMultiplier
            : imported?.CacheWritePerMillion ?? 0m;
        var cacheRead = priceOverridden
            ? CacheReadPerMillion ?? input * ModelPriceEntry.DefaultCacheReadMultiplier
            : imported?.CacheReadPerMillion ?? 0m;

        return new ModelDefinition
        {
            Id = id,
            Vendor = FirstNonBlank(Vendor, imported?.Vendor) ?? string.Empty,
            DisplayName = FirstNonBlank(DisplayName, imported?.DisplayName),
            LogoVendor = FirstNonBlank(LogoVendor, imported?.LogoVendor),
            SourceKey = imported?.SourceKey,
            Origin = imported is null ? ModelDefinitionOrigin.User : imported.Origin,
            IsPriceOverridden = priceOverridden,
            IsMetadataOverridden = HasMetadataOverride,
            HasPrice = hasPrice,
            InputPerMillion = input,
            OutputPerMillion = output,
            CacheWritePerMillion = cacheWrite,
            CacheReadPerMillion = cacheRead,
            LongContextThreshold = priceOverridden
                ? LongContextThreshold
                : imported?.LongContextThreshold,
            InputAboveThresholdPerMillion = priceOverridden
                ? InputAboveThresholdPerMillion
                : imported?.InputAboveThresholdPerMillion,
            OutputAboveThresholdPerMillion = priceOverridden
                ? OutputAboveThresholdPerMillion
                : imported?.OutputAboveThresholdPerMillion,
            CacheWriteAboveThresholdPerMillion = priceOverridden
                ? CacheWriteAboveThresholdPerMillion
                : imported?.CacheWriteAboveThresholdPerMillion,
            CacheReadAboveThresholdPerMillion = priceOverridden
                ? CacheReadAboveThresholdPerMillion
                : imported?.CacheReadAboveThresholdPerMillion,
            // 家族模式是导入层的匹配规则，用户条目不自带；覆盖价时保留导入层的规则，
            // 否则用户改了价反而丢了家族命中的历史名（对账回归）。
            MatchPattern = imported?.MatchPattern,
        };
    }

    /// <summary>
    /// 旧字段桥（I-038 兼容）：<see cref="AppSettings.PricingOverrides"/> 的规则转成本层条目。
    /// 无效规则（模型名空白 / 任一单价为负）返回 null（沿用旧 <c>ToPriceEntry</c> 的整条剔除口径，
    /// 而不是转成"只有身份没有价"的条目——那是新模型才有的形态）。
    /// 该字段在新版只读不写——用户在设置页的编辑一律落 <see cref="AppSettings.ModelDefinitions"/>。
    /// </summary>
    public static ModelDefinitionOverride? FromLegacy(ModelPricingOverride legacy)
    {
        if (legacy.ToPriceEntry() is null)
        {
            return null;
        }

        return new ModelDefinitionOverride
        {
            Id = ModelId.Normalize(legacy.Model),
            InputPerMillion = legacy.InputPerMillion,
            OutputPerMillion = legacy.OutputPerMillion,
            CacheWritePerMillion = legacy.CacheWritePerMillion,
            CacheReadPerMillion = legacy.CacheReadPerMillion,
        };
    }

    private static string? FirstNonBlank(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }
}
