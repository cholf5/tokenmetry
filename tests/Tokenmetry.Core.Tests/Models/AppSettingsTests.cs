using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Tests.Models;

public class AppSettingsTests
{
    [Fact]
    public void Default_values_are_sane()
    {
        var settings = new AppSettings();

        // A-11：无 UI 出口且无行为消费者的设置字段已删除；保留字段均有真实消费者。
        settings.CcusageCommand.Should().Be("ccusage");
        settings.ShowDebugInfo.Should().BeTrue();
        settings.RefreshMinutes.Should().Be(1);
        settings.TimeRange.Should().Be("Last7Days");
        settings.CustomFromDay.Should().BeNull();
        settings.CustomToDay.Should().BeNull();
        settings.PricingOverrides.Should().BeEmpty();
    }
}
