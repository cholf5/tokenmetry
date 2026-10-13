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

    /// <summary>用户价格覆盖规则（I-003，exact 模型名匹配，优先级高于远程数据与内置快照）。</summary>
    public List<ModelPricingOverride> PricingOverrides { get; set; } = new();
}
