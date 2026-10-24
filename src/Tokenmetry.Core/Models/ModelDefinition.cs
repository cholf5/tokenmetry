namespace Tokenmetry.Core.Models;

/// <summary>模型目录条目的来源层（I-035）。用户层优先级最高，逐字段覆盖导入层。</summary>
public enum ModelDefinitionOrigin
{
    /// <summary>构建期内嵌价格快照（离线兜底，对账锚所在层）。</summary>
    Snapshot,

    /// <summary>models.dev 远程刷新（TTL 24h，后台增强）。</summary>
    Remote,

    /// <summary>用户自建 / 覆盖条目（settings.json）。</summary>
    User,
}

/// <summary>一条原始模型名与目录条目的关联方式（I-035，设置页与成本页据此如实展示"怎么关联上的"）。</summary>
public enum ModelMatchKind
{
    /// <summary>未关联：目录里找不到对应条目（成本按未知口径处理，等待用户手动关联）。</summary>
    None,

    /// <summary>用户显式别名映射命中（<c>AppSettings.ModelAliases</c>，精确匹配）。</summary>
    Alias,

    /// <summary>ID / 来源键 / 归一化 ID 精确命中。</summary>
    Exact,

    /// <summary>家族模式命中（快照条目上的 match pattern，如 <c>gpt-5.6</c> 命中带后缀的历史名）。</summary>
    Family,
}

/// <summary>
/// 模型目录条目（I-035）：一个模型的身份与其全部静态参数——
/// <see cref="Id"/>（稳定主键，见 <see cref="ModelId"/>）、厂商、显示名、来源键、价格。
/// 价格、厂商、图标、聚合归一的唯一权威出处；由 <c>ModelCatalog</c> 把三层
/// （内置快照 / models.dev / 用户配置）合成这一种形状。
/// 不可变（用户覆盖经 <see cref="ModelDefinitionOverride"/> 合成，不改写导入层对象）。
/// </summary>
public sealed class ModelDefinition
{
    /// <summary>模型 ID（合约定的稳定主键）。</summary>
    public required string Id { get; init; }

    /// <summary>厂商 / 服务商 id（如 anthropic / openai / zai；无导入来源的用户条目为自定义串）。</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>展示名（如 "Claude Opus 4.8"）；空则界面回落到 <see cref="Id"/>。</summary>
    public string? DisplayName { get; init; }

    /// <summary>用户显式指定的图标变体（约束见 <c>ModelVisualMap.Vendors</c>）；null = 走内置推断。
    /// AvatarKind ∈ {Auto, Vendor} 时的载荷（Vendor 显式指定 / Auto + 本字段 = 旧 settings.json
    /// 手写 LogoVendor 的兼容语义）；Letter/Color/Image 头像模式下被解析层忽略。</summary>
    public string? LogoVendor { get; init; }

    /// <summary>头像方式（图标选择器四模式 + 自动缺省）；决定图标槽渲染哪一层，见 <see cref="ModelAvatarKind"/>。</summary>
    public ModelAvatarKind AvatarKind { get; init; } = ModelAvatarKind.Auto;

    /// <summary>单字母头像的字母（AvatarKind = Letter 时有效；1 个字符）。</summary>
    public string? AvatarLetter { get; init; }

    /// <summary>纯色头像的颜色（AvatarKind = Color 时有效；#RRGGBB）。</summary>
    public string? AvatarColor { get; init; }

    /// <summary>上传图片头像（AvatarKind = Image 时有效；data:image/png;base64 data URI，
    /// 采集侧已居中裁方压缩到 64×64 PNG）。</summary>
    public string? AvatarImage { get; init; }

    /// <summary>导入来源键（models.dev 的 <c>provider/model-id</c> 原样）；用户自建条目为 null。</summary>
    public string? SourceKey { get; init; }

    /// <summary>本条目有效来源层。</summary>
    public ModelDefinitionOrigin Origin { get; init; }

    /// <summary>价格是否来自用户覆盖（刷新不得冲掉已覆盖的价格）。</summary>
    public bool IsPriceOverridden { get; init; }

    /// <summary>厂商 / 显示名 / 图标是否被用户覆盖（刷新时逐字段跳过）。</summary>
    public bool IsMetadataOverridden { get; init; }

    /// <summary>是否有可用价格。false = 该模型已入目录但尚无价（成本走未知口径，绝不折 0）。</summary>
    public bool HasPrice { get; init; }

    public decimal InputPerMillion { get; init; }

    public decimal OutputPerMillion { get; init; }

    public decimal CacheWritePerMillion { get; init; }

    public decimal CacheReadPerMillion { get; init; }

    /// <summary>长上下文分档阈值（models.dev cost.tiers 的 context 档）；null = 无分档。</summary>
    public long? LongContextThreshold { get; init; }

    public decimal? InputAboveThresholdPerMillion { get; init; }

    public decimal? OutputAboveThresholdPerMillion { get; init; }

    public decimal? CacheWriteAboveThresholdPerMillion { get; init; }

    public decimal? CacheReadAboveThresholdPerMillion { get; init; }

    /// <summary>
    /// 家族匹配模式（仅内置快照条目可能携带）：候选名包含该串即视为命中。语义是<b>匹配规则</b>
    /// 而非身份——保留在条目上是为了不动对账口径（I-025 锚点纪律），但新增条目不应使用它。
    /// </summary>
    public string? MatchPattern { get; init; }

    /// <summary>展示名兜底（<see cref="DisplayName"/> 为空时用 ID）。</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName!;

    /// <summary>价格是否为零价（有价但四桶全零：合法的"免费模型"，与"缺价"区分）。</summary>
    public bool IsFreeModel => HasPrice
                               && InputPerMillion == 0m
                               && OutputPerMillion == 0m
                               && CacheWritePerMillion == 0m
                               && CacheReadPerMillion == 0m;

    /// <summary>是否有长上下文分档。</summary>
    public bool HasLongContextTier => LongContextThreshold is not null;

    /// <summary>
    /// 投影为计价条目（无价条目返回 null——"没填价"不是"价格为零"）。
    /// <paramref name="includeMatchPattern"/> = false 时清空家族模式：<see cref="ModelPriceEntry.MatchPattern"/>
    /// 非空会让条目<b>只</b>参与家族包含匹配（精确档直接跳过它），用户条目必须走精确档。
    /// </summary>
    public ModelPriceEntry? ToPriceEntry(bool includeMatchPattern = true)
    {
        if (!HasPrice)
        {
            return null;
        }

        return new ModelPriceEntry
        {
            ModelId = Id,
            Provider = Vendor,
            DisplayName = DisplayName,
            MatchPattern = includeMatchPattern ? MatchPattern : null,
            InputPerMillion = InputPerMillion,
            OutputPerMillion = OutputPerMillion,
            CacheWritePerMillion = CacheWritePerMillion,
            CacheReadPerMillion = CacheReadPerMillion,
            LongContextThreshold = LongContextThreshold,
            InputAboveThresholdPerMillion = InputAboveThresholdPerMillion,
            OutputAboveThresholdPerMillion = OutputAboveThresholdPerMillion,
            CacheWriteAboveThresholdPerMillion = CacheWriteAboveThresholdPerMillion,
            CacheReadAboveThresholdPerMillion = CacheReadAboveThresholdPerMillion,
        };
    }

    /// <summary>从导入层价格条目建条目（快照 / models.dev 同源）：ID 取 <see cref="ModelId.Normalize"/> 后的模型 id。</summary>
    public static ModelDefinition FromPriceEntry(ModelPriceEntry entry, ModelDefinitionOrigin origin, string? sourceKey)
    {
        return new ModelDefinition
        {
            Id = ModelId.Normalize(entry.ModelId),
            Vendor = entry.Provider,
            DisplayName = entry.DisplayName,
            SourceKey = sourceKey ?? $"{entry.Provider}/{entry.ModelId}",
            Origin = origin,
            HasPrice = true,
            InputPerMillion = entry.InputPerMillion,
            OutputPerMillion = entry.OutputPerMillion,
            CacheWritePerMillion = entry.CacheWritePerMillion,
            CacheReadPerMillion = entry.CacheReadPerMillion,
            LongContextThreshold = entry.LongContextThreshold,
            InputAboveThresholdPerMillion = entry.InputAboveThresholdPerMillion,
            OutputAboveThresholdPerMillion = entry.OutputAboveThresholdPerMillion,
            CacheWriteAboveThresholdPerMillion = entry.CacheWriteAboveThresholdPerMillion,
            CacheReadAboveThresholdPerMillion = entry.CacheReadAboveThresholdPerMillion,
            MatchPattern = entry.MatchPattern,
        };
    }
}
