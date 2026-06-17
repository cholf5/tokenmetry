namespace CCUsage.Core.Tests.Models;

using CCUsage.Core.Models;

public class DailyUsageSummaryTests
{
    [Fact]
    public void Default_values_are_sane()
    {
        var summary = new DailyUsageSummary();

        summary.Date.Should().BeEmpty();
        summary.TotalCostUsd.Should().Be(0);
        summary.TotalTokens.Should().Be(0);
        summary.InputTokens.Should().Be(0);
        summary.OutputTokens.Should().Be(0);
        summary.CacheCreationTokens.Should().Be(0);
        summary.CacheReadTokens.Should().Be(0);
        summary.Agents.Should().BeEmpty();
        summary.Models.Should().BeEmpty();
        summary.Projects.Should().BeEmpty();
    }

    [Fact]
    public void Can_set_all_properties()
    {
        var agentBreakdown = new AgentBreakdown { Agent = "claude", CostUsd = 1.23m, TotalTokens = 1000 };
        var modelBreakdown = new ModelBreakdown { Model = "claude-sonnet-4-20250514", CostUsd = 1.23m, TotalTokens = 1000 };
        var projectBreakdown = new ProjectBreakdown { Project = "project-alpha", DisplayName = "project-alpha", CostUsd = 1.23m, TotalTokens = 1000 };

        var summary = new DailyUsageSummary
        {
            Date = "2026-01-09",
            TotalCostUsd = 1.23m,
            TotalTokens = 1000,
            InputTokens = 500,
            OutputTokens = 300,
            CacheCreationTokens = 50,
            CacheReadTokens = 150,
            Agents = new List<AgentBreakdown> { agentBreakdown },
            Models = new List<ModelBreakdown> { modelBreakdown },
            Projects = new List<ProjectBreakdown> { projectBreakdown }
        };

        summary.Date.Should().Be("2026-01-09");
        summary.TotalCostUsd.Should().Be(1.23m);
        summary.TotalTokens.Should().Be(1000);
        summary.Agents.Single().Agent.Should().Be("claude");
    }
}
