namespace CCUsage.Core.Tests.Services;

using CCUsage.Core.Models;
using CCUsage.Core.Services;

public class DashboardServiceTests
{
    private readonly DashboardService _service;
    private readonly TestDateRangeService _dateService;

    public DashboardServiceTests()
    {
        _dateService = new TestDateRangeService { TodayValue = "2026-01-10" };
        _service = new DashboardService(_dateService);
    }

    [Fact]
    public void Empty_data_returns_zero_dashboard()
    {
        var rows = new List<DailyUsageSummary>();
        var dashboard = _service.BuildDashboard(rows);

        dashboard.TodayCostUsd.Should().Be(0);
        dashboard.WeekCostUsd.Should().Be(0);
        dashboard.MonthCostUsd.Should().Be(0);
        dashboard.TodayTokens.Should().Be(0);
        dashboard.Status.Should().Be("ok");
    }

    [Fact]
    public void Sums_today_week_and_month_cost_correctly()
    {
        var rows = new List<DailyUsageSummary>
        {
            new() { Date = "2026-01-09", TotalCostUsd = 1.0m, TotalTokens = 100 },
            new() { Date = "2026-01-10", TotalCostUsd = 2.0m, TotalTokens = 200 },
            new() { Date = "2026-01-08", TotalCostUsd = 0.5m, TotalTokens = 50 },
            new() { Date = "2025-12-31", TotalCostUsd = 100m, TotalTokens = 10000 }
        };

        var dashboard = _service.BuildDashboard(rows);

        dashboard.TodayCostUsd.Should().Be(2.0m);
        dashboard.MonthCostUsd.Should().Be(3.5m);
        dashboard.TodayTokens.Should().Be(200);
    }

    private class TestDateRangeService : DateRangeService
    {
        public string TodayValue { get; set; } = string.Empty;
        public override string Today() => TodayValue;
    }
}
