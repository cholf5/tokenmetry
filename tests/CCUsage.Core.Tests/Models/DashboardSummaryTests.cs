namespace CCUsage.Core.Tests.Models;

using CCUsage.Core.Models;

public class DashboardSummaryTests
{
    [Fact]
    public void Default_status_is_ok()
    {
        var dashboard = new DashboardSummary();
        dashboard.Status.Should().Be("ok");
        dashboard.TodayCostUsd.Should().Be(0);
        dashboard.MonthCostUsd.Should().Be(0);
    }

    [Fact]
    public void Anomaly_notice_has_required_properties()
    {
        var notice = new AnomalyNotice
        {
            Kind = "daily_cost_spike",
            Message = "今日成本是过去 7 日均值的 2.3 倍",
            Ratio = 2.3m
        };

        notice.Kind.Should().Be("daily_cost_spike");
        notice.Ratio.Should().Be(2.3m);
    }

    [Fact]
    public void AppSettings_defaults_are_quiet()
    {
        var settings = new AppSettings();
        settings.OfflinePricing.Should().BeTrue();
        settings.AnomalyNotificationsEnabled.Should().BeTrue();
        settings.BudgetNotificationsEnabled.Should().BeFalse();
        settings.RefreshMinutes.Should().Be(15);
    }
}
