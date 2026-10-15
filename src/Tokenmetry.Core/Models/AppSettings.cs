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
    public int RefreshMinutes { get; set; } = 15;

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

    /// <summary>用户价格覆盖规则（I-003，exact 模型名匹配，优先级高于远程数据与内置快照）。</summary>
    public List<ModelPricingOverride> PricingOverrides { get; set; } = new();

    /// <summary>
    /// 模型别名映射（用户配置的模型聚合）：键 = 数据里出现的原始模型名（精确匹配，大小写敏感），
    /// 值 = 规范名（统计显示与聚合的键）。动机：不同来源对同一模型命名不同（GLM-5.3-Flash /
    /// glm-5.3-flash / glm-flash-latest / ark/glm-5.3-flash），全靠内置规则无法覆盖，
    /// 交给用户在设置页维护；查询服务在模型维度聚合与会话筛选时经
    /// <see cref="Abstractions.IModelNameResolver"/> 消费。空字典 = 不聚合（默认）。
    /// </summary>
    public Dictionary<string, string> ModelAliases { get; set; } = new();
}
