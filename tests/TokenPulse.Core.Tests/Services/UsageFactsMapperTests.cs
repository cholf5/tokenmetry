namespace TokenPulse.Core.Tests.Services;

using TokenPulse.Core.Models;
using TokenPulse.Core.Services;

public class UsageFactsMapperTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void ToUsageFacts_AggregatesSameDayAgentModelProject()
    {
        var facts = UsageFactsMapper.ToUsageFacts(new[]
        {
            CreateRecord(day: 19, hour: 8, inputTokens: 100, outputTokens: 50, costUsd: 0.5m, cacheCreation5m: 10),
            CreateRecord(day: 19, hour: 20, inputTokens: 200, outputTokens: 70, costUsd: 0.25m, cacheCreation5m: 15)
        }, Utc);

        facts.Should().ContainSingle();
        var fact = facts[0];
        fact.Day.Should().Be("2026-06-19");
        fact.Grain.Should().Be(UsageFactGrain.Full);
        fact.Agent.Should().Be("Claude Code");
        fact.Model.Should().Be("claude-sonnet-4");
        fact.Project.Should().Be("/Users/dev/project-a");
        fact.InputTokens.Should().Be(300);
        fact.OutputTokens.Should().Be(120);
        fact.CacheCreationTokens.Should().Be(25);
        fact.CacheCreationEphemeral5mTokens.Should().Be(25);
        fact.CacheCreationEphemeral1hTokens.Should().Be(0);
        fact.CacheReadTokens.Should().Be(0);
        fact.TotalTokens.Should().Be(445);
        fact.CostUsd.Should().Be(0.75m);
    }

    [Fact]
    public void ToUsageFacts_SeparatesByEveryDimension()
    {
        var facts = UsageFactsMapper.ToUsageFacts(new[]
        {
            CreateRecord(day: 19, hour: 8, model: "claude-sonnet-4"),
            CreateRecord(day: 19, hour: 9, model: "claude-opus-4"),
            CreateRecord(day: 20, hour: 8, model: "claude-sonnet-4"),
            CreateRecord(day: 19, hour: 8, model: "claude-sonnet-4", project: "/Users/dev/project-b")
        }, Utc);

        facts.Should().HaveCount(4);
        facts.Select(fact => (fact.Day, fact.Model, fact.Project)).Should().OnlyHaveUniqueItems();
        facts.Should().OnlyContain(fact => fact.Grain == UsageFactGrain.Full);
    }

    [Fact]
    public void ToUsageFacts_NullModelFallsBackToEmptyAndNullCostStaysNull()
    {
        var facts = UsageFactsMapper.ToUsageFacts(new[]
        {
            CreateRecord(day: 19, hour: 8, model: null, costUsd: null)
        }, Utc);

        var fact = facts.Single();
        fact.Model.Should().BeEmpty();
        fact.CostUsd.Should().BeNull();
    }

    [Fact]
    public void ToUsageFacts_MixedKnownCostsSumsKnownOnesOnly()
    {
        var facts = UsageFactsMapper.ToUsageFacts(new[]
        {
            CreateRecord(day: 19, hour: 8, costUsd: 0.5m),
            CreateRecord(day: 19, hour: 9, costUsd: null)
        }, Utc);

        facts.Single().CostUsd.Should().Be(0.5m);
    }

    [Fact]
    public void ToUsageFacts_BucketsDayInConfiguredTimeZone()
    {
        var record = CreateRecord(day: 19, hour: 23, minute: 30);
        var plusEight = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

        UsageFactsMapper.ToUsageFacts(new[] { record }, Utc).Single().Day.Should().Be("2026-06-19");
        UsageFactsMapper.ToUsageFacts(new[] { record }, plusEight).Single().Day.Should().Be("2026-06-20");
    }

    [Fact]
    public void ToSessionFacts_GroupsBySessionWithRangeDominantModelAndCost()
    {
        var facts = UsageFactsMapper.ToSessionFacts(new[]
        {
            CreateRecord(day: 19, hour: 8, sessionId: "session-1", model: "claude-sonnet-4", costUsd: 0.5m, inputTokens: 100),
            CreateRecord(day: 19, hour: 9, sessionId: "session-1", model: "claude-opus-4", costUsd: null, inputTokens: 300),
            CreateRecord(day: 19, hour: 8, sessionId: "session-2", model: "claude-sonnet-4", costUsd: 0.25m)
        });

        facts.Should().HaveCount(2);

        var session1 = facts.Single(fact => fact.SessionId == "session-1");
        session1.Agent.Should().Be("Claude Code");
        session1.Project.Should().Be("/Users/dev/project-a");
        session1.DominantModel.Should().Be("claude-opus-4");
        session1.FirstSeenAt.Should().Be(new DateTimeOffset(2026, 6, 19, 8, 0, 0, TimeSpan.Zero));
        session1.LastSeenAt.Should().Be(new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero));
        session1.InputTokens.Should().Be(400);
        session1.CostUsd.Should().Be(0.5m);

        var session2 = facts.Single(fact => fact.SessionId == "session-2");
        session2.DominantModel.Should().Be("claude-sonnet-4");
        session2.CostUsd.Should().Be(0.25m);
    }

    [Fact]
    public void ToSessionFacts_AllCostsUnknownStaysNull()
    {
        var facts = UsageFactsMapper.ToSessionFacts(new[]
        {
            CreateRecord(day: 19, hour: 8, sessionId: "session-1", costUsd: null)
        });

        facts.Single().CostUsd.Should().BeNull();
    }

    [Fact]
    public void ToUsageFacts_FillsProjectDisplayNameFromPathLastSegment()
    {
        var facts = UsageFactsMapper.ToUsageFacts(new[]
        {
            CreateRecord(day: 19, hour: 8, project: "/Users/dev/project-a"),
            CreateRecord(day: 19, hour: 9, project: "project-b\\sub"),
            CreateRecord(day: 19, hour: 10, project: "encoded-project")
        }, Utc);

        facts.Single(fact => fact.Project == "/Users/dev/project-a").ProjectDisplayName.Should().Be("project-a");
        facts.Single(fact => fact.Project == "project-b\\sub").ProjectDisplayName.Should().Be("sub");
        facts.Single(fact => fact.Project == "encoded-project").ProjectDisplayName.Should().Be("encoded-project");
    }

    [Fact]
    public void ToUsageFacts_BlankProjectKeepsEmptyDisplayName()
    {
        var facts = UsageFactsMapper.ToUsageFacts(new[]
        {
            CreateRecord(day: 19, hour: 8, project: "  ")
        }, Utc);

        facts.Single().ProjectDisplayName.Should().BeEmpty();
    }

    [Fact]
    public void ToSessionFacts_FillsProjectDisplayNameFromPathLastSegment()
    {
        var facts = UsageFactsMapper.ToSessionFacts(new[]
        {
            CreateRecord(day: 19, hour: 8, sessionId: "session-1", project: "/Users/dev/project-a")
        });

        facts.Single().ProjectDisplayName.Should().Be("project-a");
    }

    [Fact]
    public void FromDailySummaries_DecomposesToGrainFacts()
    {
        var facts = UsageFactsMapper.FromDailySummaries(new[]
        {
            new DailyUsageSummary
            {
                Date = "2026-06-19",
                TotalCostUsd = 3m,
                TotalTokens = 3000,
                InputTokens = 1500,
                OutputTokens = 700,
                CacheCreationTokens = 400,
                CacheReadTokens = 400,
                Agents = new List<AgentBreakdown> { new() { Agent = "Claude Code" } },
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "claude-sonnet-4", CostUsd = 1m, TotalTokens = 1000 },
                    new() { Model = "claude-opus-4", CostUsd = 2m, TotalTokens = 2000 }
                },
                Projects = new List<ProjectBreakdown>
                {
                    new() { Project = "/Users/dev/project-a", DisplayName = "project-a", CostUsd = 3m, TotalTokens = 3000 }
                }
            }
        });

        facts.Should().HaveCount(5); // 1 day + 1 agent + 2 models + 1 project

        var dayFact = facts.Single(fact => fact.Grain == UsageFactGrain.Day);
        dayFact.Day.Should().Be("2026-06-19");
        dayFact.InputTokens.Should().Be(1500);
        dayFact.OutputTokens.Should().Be(700);
        dayFact.CacheCreationTokens.Should().Be(400);
        dayFact.CacheReadTokens.Should().Be(400);
        dayFact.TotalTokens.Should().Be(3000);
        dayFact.CostUsd.Should().Be(3m);

        var agentFact = facts.Single(fact => fact.Grain == UsageFactGrain.Agent);
        agentFact.Agent.Should().Be("Claude Code");
        agentFact.TotalTokens.Should().Be(0);

        var modelFacts = facts.Where(fact => fact.Grain == UsageFactGrain.Model).ToList();
        modelFacts.Select(fact => fact.Model).Should().Equal("claude-sonnet-4", "claude-opus-4");
        modelFacts.Single(fact => fact.Model == "claude-opus-4").CostUsd.Should().Be(2m);
        modelFacts.Single(fact => fact.Model == "claude-opus-4").TotalTokens.Should().Be(2000);

        var projectFact = facts.Single(fact => fact.Grain == UsageFactGrain.Project);
        projectFact.Project.Should().Be("/Users/dev/project-a");
        projectFact.ProjectDisplayName.Should().Be("project-a");
        projectFact.CostUsd.Should().Be(3m);
    }

    [Fact]
    public void FromDailySummaries_SkipsBlankKeys()
    {
        var facts = UsageFactsMapper.FromDailySummaries(new[]
        {
            new DailyUsageSummary
            {
                Date = "2026-06-19",
                TotalCostUsd = 1m,
                TotalTokens = 100,
                Agents = new List<AgentBreakdown> { new() { Agent = " " } },
                Models = new List<ModelBreakdown> { new() { Model = "" } },
                Projects = new List<ProjectBreakdown> { new() { Project = "" } }
            }
        });

        facts.Select(fact => fact.Grain).Should().Equal(UsageFactGrain.Day);
    }

    [Fact]
    public void DailySummaryFacts_RebuildIsLossless()
    {
        var original = new List<DailyUsageSummary>
        {
            new()
            {
                Date = "2026-06-19",
                TotalCostUsd = 3m,
                TotalTokens = 3000,
                InputTokens = 1500,
                OutputTokens = 700,
                CacheCreationTokens = 400,
                CacheReadTokens = 400,
                Agents = new List<AgentBreakdown> { new() { Agent = "Claude Code" }, new() { Agent = "zcode" } },
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "claude-sonnet-4", CostUsd = 1m, TotalTokens = 1000 },
                    new() { Model = "claude-opus-4", CostUsd = 2m, TotalTokens = 2000 }
                },
                Projects = new List<ProjectBreakdown>
                {
                    new() { Project = "/Users/dev/project-a", DisplayName = "project-a", CostUsd = 3m, TotalTokens = 3000 }
                }
            }
        };

        var rebuilt = UsageFactsMapper.ToDailySummaries(UsageFactsMapper.FromDailySummaries(original));

        rebuilt.Should().HaveCount(1);
        var summary = rebuilt[0];
        summary.Date.Should().Be("2026-06-19");
        summary.TotalCostUsd.Should().Be(3m);
        summary.TotalTokens.Should().Be(3000);
        summary.InputTokens.Should().Be(1500);
        summary.OutputTokens.Should().Be(700);
        summary.CacheCreationTokens.Should().Be(400);
        summary.CacheReadTokens.Should().Be(400);
        summary.Agents.Select(agent => agent.Agent).Should().Equal("Claude Code", "zcode");
        summary.Models.Should().BeEquivalentTo(original[0].Models);
        summary.Projects.Should().BeEquivalentTo(original[0].Projects);
    }

    [Fact]
    public void ToDailySummaries_AggregatesFullGrainFactsIntoBreakdowns()
    {
        var facts = UsageFactsMapper.ToUsageFacts(new[]
        {
            CreateRecord(day: 19, hour: 8, model: "claude-sonnet-4", costUsd: 0.5m, inputTokens: 100),
            CreateRecord(day: 19, hour: 9, model: "claude-sonnet-4", costUsd: 0.25m, inputTokens: 100),
            CreateRecord(day: 19, hour: 9, model: "claude-opus-4", costUsd: 1m, inputTokens: 400, project: "/Users/dev/project-b")
        }, Utc);

        var summaries = UsageFactsMapper.ToDailySummaries(facts);

        summaries.Should().ContainSingle();
        var summary = summaries[0];
        summary.Date.Should().Be("2026-06-19");
        summary.TotalCostUsd.Should().Be(1.75m);
        summary.TotalTokens.Should().Be(675); // 2×(100+15+10) + (400+15+10)
        summary.InputTokens.Should().Be(600);
        summary.OutputTokens.Should().Be(45);
        summary.CacheCreationTokens.Should().Be(30);
        summary.Agents.Should().ContainSingle().Which.Agent.Should().Be("Claude Code");
        summary.Models.Select(model => (model.Model, model.CostUsd)).Should().Equal(
            ("claude-opus-4", 1m),
            ("claude-sonnet-4", 0.75m));
        summary.Projects.Select(project => project.Project).Should().Equal("/Users/dev/project-a", "/Users/dev/project-b");
    }

    [Fact]
    public void ToDailySummaries_OrdersByDateDescending()
    {
        var facts = UsageFactsMapper.ToUsageFacts(new[]
        {
            CreateRecord(day: 18, hour: 8),
            CreateRecord(day: 20, hour: 8),
            CreateRecord(day: 19, hour: 8)
        }, Utc);

        var summaries = UsageFactsMapper.ToDailySummaries(facts);

        summaries.Select(summary => summary.Date).Should().Equal("2026-06-20", "2026-06-19", "2026-06-18");
    }

    [Fact]
    public void ToDailySummaries_SkipsBlankDays()
    {
        var facts = new List<UsageFact> { new() { Day = "", TotalTokens = 100 } };

        var summaries = UsageFactsMapper.ToDailySummaries(facts);

        summaries.Should().BeEmpty();
    }

    [Fact]
    public void ToDailySummaries_MixedGrainSameDayPrefersFullGrainRows()
    {
        // 同日混源（full 粒度来自统一记录聚合，分解行来自 CLI 源）：full 优先，避免重复计数
        var facts = new List<UsageFact>
        {
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Day, TotalTokens = 1000, CostUsd = 1m },
            new() { Day = "2026-06-19", Grain = UsageFactGrain.Model, Model = "claude-opus-4", TotalTokens = 1000, CostUsd = 1m },
            new()
            {
                Day = "2026-06-19",
                Grain = UsageFactGrain.Full,
                Agent = "Claude Code",
                Model = "claude-opus-4",
                Project = "/Users/dev/project-a",
                TotalTokens = 700,
                CostUsd = 0.7m,
            },
        };

        var summaries = UsageFactsMapper.ToDailySummaries(facts);

        summaries.Should().ContainSingle();
        var summary = summaries[0];
        summary.TotalTokens.Should().Be(700);
        summary.TotalCostUsd.Should().Be(0.7m);
        summary.Models.Should().ContainSingle().Which.TotalTokens.Should().Be(700);
        summary.Agents.Should().ContainSingle().Which.Agent.Should().Be("Claude Code");
    }

    private static UsageRecord CreateRecord(
        int day,
        int hour,
        int minute = 0,
        string? model = "claude-sonnet-4",
        decimal? costUsd = 0.1m,
        long inputTokens = 100,
        long outputTokens = 15,
        long cacheCreation5m = 10,
        string project = "/Users/dev/project-a",
        string sessionId = "session-1")
    {
        return new UsageRecord
        {
            Timestamp = new DateTimeOffset(2026, 6, day, hour, minute, 0, TimeSpan.Zero),
            Agent = "Claude Code",
            ProjectPath = project,
            SessionId = sessionId,
            Model = model,
            CostUsd = costUsd,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheCreationTokens = cacheCreation5m,
            CacheCreationEphemeral5mTokens = cacheCreation5m,
            CacheReadTokens = 0,
        };
    }
}
