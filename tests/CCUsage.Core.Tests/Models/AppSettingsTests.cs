using CCUsage.Core.Models;

namespace CCUsage.Core.Tests.Models;

public class AppSettingsTests
{
    [Fact]
    public void Default_values_are_sane()
    {
        var settings = new AppSettings();

        settings.CcusageCommand.Should().Be("ccusage");
        settings.OfflinePricing.Should().BeTrue();
        settings.AnomalyNotificationsEnabled.Should().BeTrue();
        settings.BudgetNotificationsEnabled.Should().BeFalse();
        settings.ActiveSessionNotificationsEnabled.Should().BeFalse();
        settings.ShowDebugInfo.Should().BeTrue();
        settings.RefreshMinutes.Should().Be(15);
        settings.DailyBudgetUsd.Should().BeNull();
        settings.MonthlyBudgetUsd.Should().BeNull();
        settings.PricingOverrides.Should().BeEmpty();
    }
}
