namespace CCUsage.Core.Models;

public class AppSettings
{
    public string CcusageCommand { get; set; } = "ccusage";
    public bool OfflinePricing { get; set; } = true;
    public bool AnomalyNotificationsEnabled { get; set; } = true;
    public bool BudgetNotificationsEnabled { get; set; } = false;
    public bool ActiveSessionNotificationsEnabled { get; set; } = false;
    public bool ShowDebugInfo { get; set; } = true;
    public int RefreshMinutes { get; set; } = 15;
    public decimal? DailyBudgetUsd { get; set; }
    public decimal? MonthlyBudgetUsd { get; set; }

    /// <summary>用户价格覆盖规则（I-003，exact 模型名匹配，优先级高于远程数据与内置快照）。</summary>
    public List<ModelPricingOverride> PricingOverrides { get; set; } = new();
}
