using CCUsage.App.ViewModels;
using CCUsage.Core.Models;

namespace CCUsage.App.Tests.ViewModels;

public class DashboardViewModelTests
{
    [Fact]
    public void ShouldShowHistoricalOnlyHintFor_ReturnsTrue_WhenOnlyHistoricalDataExists()
    {
        var summary = new DashboardSummary
        {
            TodayCostUsd = 0,
            WeekCostUsd = 0,
            MonthCostUsd = 0,
            TodayTokens = 0,
            LatestDataDate = "2026-01-03",
            LatestDayCostUsd = 2.5m,
            LatestDayTokens = 250,
            AllTimeCostUsd = 4.0m,
            AllTimeTokens = 400
        };

        DashboardViewModel.ShouldShowHistoricalOnlyHintFor(summary).Should().BeTrue();
    }

    [Fact]
    public void ShouldShowHistoricalOnlyHintFor_ReturnsFalse_WhenCurrentPeriodDataExists()
    {
        var summary = new DashboardSummary
        {
            TodayCostUsd = 1.25m,
            WeekCostUsd = 1.25m,
            MonthCostUsd = 1.25m,
            TodayTokens = 125,
            LatestDataDate = "2026-01-10",
            LatestDayCostUsd = 1.25m,
            LatestDayTokens = 125,
            AllTimeCostUsd = 10.0m,
            AllTimeTokens = 1000
        };

        DashboardViewModel.ShouldShowHistoricalOnlyHintFor(summary).Should().BeFalse();
    }
}
