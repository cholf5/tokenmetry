using TokenPulse.Core.Models;

namespace TokenPulse.Core.Services;

public class DashboardService
{
    private readonly DateRangeService _dateService;

    public DashboardService(DateRangeService dateService)
    {
        _dateService = dateService;
    }

    public DashboardSummary BuildDashboard(List<DailyUsageSummary> rows)
    {
        var today = _dateService.Today();
        var monthPrefix = _dateService.GetMonthPrefix(today);
        var last7Days = _dateService.GetLastNDays(7).ToHashSet();

        var todayRow = rows.FirstOrDefault(r => r.Date == today);
        var latestRow = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Date))
            .OrderByDescending(r => r.Date)
            .FirstOrDefault();

        return new DashboardSummary
        {
            TodayCostUsd = todayRow?.TotalCostUsd ?? 0,
            TodayTokens = todayRow?.TotalTokens ?? 0,
            WeekCostUsd = rows.Where(r => last7Days.Contains(r.Date)).Sum(r => r.TotalCostUsd),
            MonthCostUsd = rows.Where(r => r.Date.StartsWith(monthPrefix)).Sum(r => r.TotalCostUsd),
            LatestDataDate = latestRow?.Date ?? string.Empty,
            LatestDayCostUsd = latestRow?.TotalCostUsd ?? 0,
            LatestDayTokens = latestRow?.TotalTokens ?? 0,
            AllTimeCostUsd = rows.Sum(r => r.TotalCostUsd),
            AllTimeTokens = rows.Sum(r => r.TotalTokens),
            Status = "ok",
            LastRefreshedAt = DateTime.UtcNow
        };
    }
}
