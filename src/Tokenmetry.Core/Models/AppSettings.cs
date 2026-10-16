namespace Tokenmetry.Core.Models;

/// <summary>
/// A-11 收口：仅保留有真实消费者（UI 出口或行为读取）的设置字段——OfflinePricing /
/// BudgetNotificationsEnabled / ActiveSessionNotificationsEnabled / DailyBudgetUsd /
/// MonthlyBudgetUsd 此前无任何出口，按"假开关/死配置"红线删除；旧 settings.json 中的
/// 多余字段反序列化时被忽略，不构成迁移问题。
/// </summary>
public class AppSettings
{
    public string CcusageCommand { get; set; } = "ccusage";
    public bool AnomalyNotificationsEnabled { get; set; } = true;
    public bool ShowDebugInfo { get; set; } = true;
    public int RefreshMinutes { get; set; } = 1;

    /// <summary>外观主题偏好：light（日间）/ dark（夜间）/ system（跟随系统）。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// 界面语言偏好：zh（简体中文）/ en（English）/ system（跟随系统 UI 语言）；缺省 system，
    /// 未知值在解析处（Localization/Translations.ApplyPreference）如实回落 system。
    /// 存字符串而非枚举：与 Theme 同风格，且未知值不会让 settings.json 整体反序列化失败。
    /// 消费者=全部 UI 文案（Tokenmetry.App/Localization/Translations，启动时与设置页切换即时应用）。
    /// </summary>
    public string Language { get; set; } = "system";

    /// <summary>
    /// 顶部栏时间范围档（Tokenmetry.App 的 TimeRange 枚举名，如 "Last7Days"）；未知值由
    /// FilterStatePersistence 解析处如实回落默认档。存字符串：枚举在 App 层，Core 不依赖 UI。
    /// </summary>
    public string TimeRange { get; set; } = "Last7Days";

    /// <summary>自定义档起始日（含，yyyy-MM-dd invariant；null = 未选，查询映射不设下界）。</summary>
    public string? CustomFromDay { get; set; }

    /// <summary>自定义档结束日（含，yyyy-MM-dd invariant；null = 未选，查询映射不设上界）。</summary>
    public string? CustomToDay { get; set; }

    /// <summary>
    /// <b>旧</b>用户价格覆盖规则（I-003 字段形状）：I-035 起用户配置统一走
    /// <see cref="ModelDefinitions"/>，本字段只读不写（装载时转换为模型目录条目，见
    /// <see cref="ModelDefinitionOverride.FromLegacy"/>）。保留是为了不丢老 settings.json 里已配的价，
    /// 待下个版本删除。
    /// </summary>
    public List<ModelPricingOverride> PricingOverrides { get; set; } = new();

    /// <summary>
    /// 用户模型目录条目（I-035）：按模型 ID 逐字段覆盖导入层（内置快照 / models.dev）的价格与元数据，
    /// ID 不在导入层时即用户自建的模型。价格、厂商、图标、聚合归一由此单点决定——设置页的模型目录区
    /// 即本列表的编辑器，保存后 <c>ModelCatalog.UpdateUserDefinitions</c> 热更新（无需重启）。
    /// </summary>
    public List<ModelDefinitionOverride> ModelDefinitions { get; set; } = new();

    /// <summary>
    /// 模型别名映射（原始名 → 模型 ID）：键 = 数据里出现的原始模型名（精确匹配，大小写敏感），
    /// 值 = 模型 ID（或用户自定的规范名）。动机：不同来源对同一模型命名不同
    /// （GLM-5.3-Flash / glm-flash-latest / ark/glm-5.3-flash），全靠内置规则无法覆盖，
    /// 交给用户在设置页维护；I-035 起同时被定价引擎消费（关联即对价格生效）。
    /// 空字典 = 不聚合（默认）。
    /// </summary>
    public Dictionary<string, string> ModelAliases { get; set; } = new();
}
