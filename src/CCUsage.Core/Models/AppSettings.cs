namespace CCUsage.Core.Models;

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
    /// 消费者=全部 UI 文案（CCUsage.App/Localization/Translations，启动时与设置页切换即时应用）。
    /// </summary>
    public string Language { get; set; } = "system";

    /// <summary>用户价格覆盖规则（I-003，exact 模型名匹配，优先级高于远程数据与内置快照）。</summary>
    public List<ModelPricingOverride> PricingOverrides { get; set; } = new();
}
