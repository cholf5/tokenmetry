namespace Tokenmetry.Core.Models;

public class DashboardSummary
{
    public decimal TodayCostUsd { get; set; }
    public decimal WeekCostUsd { get; set; }
    public decimal MonthCostUsd { get; set; }
    public long TodayTokens { get; set; }

    /// <summary>今日缓存读取占比（缓存读 / 总 token，0~1，4 位舍入；口径同
    /// <see cref="UsageKpiResult.CacheReadShare"/>）。无今日行或总量为 0 时为 null（不冒充 0）。</summary>
    public decimal? TodayCacheReadShare { get; set; }

    /// <summary>最新有数据的一天（可能不是今天）</summary>
    public string LatestDataDate { get; set; } = string.Empty;
    public decimal LatestDayCostUsd { get; set; }
    public long LatestDayTokens { get; set; }

    /// <summary>历史累计</summary>
    public decimal AllTimeCostUsd { get; set; }
    public long AllTimeTokens { get; set; }

    public string Status { get; set; } = "ok";
    public List<AnomalyNotice> Anomalies { get; set; } = new();
    public DateTime LastRefreshedAt { get; set; } = DateTime.UtcNow;
}
