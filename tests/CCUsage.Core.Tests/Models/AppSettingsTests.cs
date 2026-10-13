using CCUsage.Core.Models;

namespace CCUsage.Core.Tests.Models;

public class AppSettingsTests
{
    [Fact]
    public void Default_values_are_sane()
    {
        var settings = new AppSettings();

        // A-11：无 UI 出口且无行为消费者的设置字段已删除；保留字段均有真实消费者。
        settings.CcusageCommand.Should().Be("ccusage");
        settings.AnomalyNotificationsEnabled.Should().BeTrue();
        settings.ShowDebugInfo.Should().BeTrue();
        settings.RefreshMinutes.Should().Be(15);
        settings.PricingOverrides.Should().BeEmpty();
    }
}
