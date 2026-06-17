namespace CCUsage.Core.Models;

public class AppSettings
{
    public bool OfflinePricing { get; set; } = true;
    public bool AnomalyNotificationsEnabled { get; set; } = true;
    public bool BudgetNotificationsEnabled { get; set; } = false;
    public bool ActiveSessionNotificationsEnabled { get; set; } = false;
    public int RefreshMinutes { get; set; } = 15;
    public decimal? DailyBudgetUsd { get; set; }
    public decimal? MonthlyBudgetUsd { get; set; }
}
