namespace CCUsage.Core.Models;

public class DashboardSummary
{
    public decimal TodayCostUsd { get; set; }
    public decimal WeekCostUsd { get; set; }
    public decimal MonthCostUsd { get; set; }
    public long TodayTokens { get; set; }
    public string Status { get; set; } = "ok";
    public List<AnomalyNotice> Anomalies { get; set; } = new();
    public DateTime LastRefreshedAt { get; set; } = DateTime.UtcNow;
}
