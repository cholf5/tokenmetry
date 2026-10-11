namespace CCUsage.Core.Tests.Services;

using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

public class CacheServiceTests
{
    private readonly InMemoryUsageStore _store;
    private readonly CacheService _service;

    public CacheServiceTests()
    {
        _store = new InMemoryUsageStore();
        _service = new CacheService(_store);
    }

    [Fact]
    public async Task Empty_store_returns_no_daily_rows()
    {
        var rows = await _service.GetDailyRowsAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_then_get_daily_rows_roundtrips_through_facts()
    {
        var rows = new List<DailyUsageSummary>
        {
            new()
            {
                Date = "2026-06-19",
                TotalCostUsd = 1.23m,
                TotalTokens = 1000,
                InputTokens = 500,
                OutputTokens = 200,
                CacheCreationTokens = 100,
                CacheReadTokens = 200,
                Agents = new List<AgentBreakdown> { new() { Agent = "Claude Code" } },
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "claude-sonnet-4", CostUsd = 1.23m, TotalTokens = 1000 }
                }
            }
        };

        await _service.SaveDailyRowsAsync(rows);
        var result = await _service.GetDailyRowsAsync();

        result.Should().ContainSingle();
        var row = result[0];
        row.Date.Should().Be("2026-06-19");
        row.TotalCostUsd.Should().Be(1.23m);
        row.TotalTokens.Should().Be(1000);
        row.InputTokens.Should().Be(500);
        row.OutputTokens.Should().Be(200);
        row.CacheCreationTokens.Should().Be(100);
        row.CacheReadTokens.Should().Be(200);
        row.Agents.Should().ContainSingle().Which.Agent.Should().Be("Claude Code");
        row.Models.Should().BeEquivalentTo(rows[0].Models);
    }

    [Fact]
    public async Task Saving_the_same_rows_twice_does_not_duplicate_facts()
    {
        var rows = new List<DailyUsageSummary>
        {
            new() { Date = "2026-06-19", TotalCostUsd = 1m, TotalTokens = 100 }
        };

        await _service.SaveDailyRowsAsync(rows);
        await _service.SaveDailyRowsAsync(rows);

        _store.Facts.Should().HaveCount(1);
    }

    [Fact]
    public async Task Save_daily_rows_removes_decomposition_rows_that_disappeared()
    {
        await _service.SaveDailyRowsAsync(new List<DailyUsageSummary>
        {
            new()
            {
                Date = "2026-06-19",
                TotalCostUsd = 1m,
                TotalTokens = 1000,
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "claude-opus-4", CostUsd = 0.4m, TotalTokens = 500 },
                    new() { Model = "claude-sonnet-4", CostUsd = 0.6m, TotalTokens = 500 }
                }
            }
        });

        // 同日再写，model 列表收缩
        await _service.SaveDailyRowsAsync(new List<DailyUsageSummary>
        {
            new()
            {
                Date = "2026-06-19",
                TotalCostUsd = 0.4m,
                TotalTokens = 500,
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "claude-opus-4", CostUsd = 0.4m, TotalTokens = 500 }
                }
            }
        });

        var result = await _service.GetDailyRowsAsync();
        result.Should().ContainSingle();
        result[0].TotalTokens.Should().Be(500);
        result[0].Models.Select(model => model.Model).Should().BeEquivalentTo("claude-opus-4");
    }
}

sealed class InMemoryUsageStore : IUsageStore
{
    public List<UsageFact> Facts { get; } = new();
    public List<SessionFact> SessionFacts { get; } = new();

    public Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
    {
        foreach (var fact in facts)
        {
            Facts.RemoveAll(existing =>
                existing.Day == fact.Day &&
                existing.Grain == fact.Grain &&
                existing.Agent == fact.Agent &&
                existing.Model == fact.Model &&
                existing.Project == fact.Project);
            Facts.Add(fact);
        }

        return Task.CompletedTask;
    }

    public Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
    {
        var days = facts.Select(fact => fact.Day).Distinct().ToList();
        Facts.RemoveAll(existing => days.Contains(existing.Day) && existing.Grain != UsageFactGrain.Full);
        return UpsertUsageFactsAsync(facts, ct);
    }

    public Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<UsageFact> facts = Facts.ToList();
        return Task.FromResult(facts);
    }

    public Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default)
    {
        foreach (var fact in facts)
        {
            SessionFacts.RemoveAll(existing => existing.SessionId == fact.SessionId);
            SessionFacts.Add(fact);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<SessionFact> facts = SessionFacts.ToList();
        return Task.FromResult(facts);
    }
}
