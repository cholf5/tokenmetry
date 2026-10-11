using CCUsage.App.ViewModels;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

namespace CCUsage.App.Tests.ViewModels;

public class ModelBreakdownViewModelTests
{
    [Fact]
    public void MapRows_WithSingleModel_ReturnsFullShares()
    {
        var rows = new[]
        {
            new DailyUsageSummary
            {
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "claude-sonnet-4", CostUsd = 2.5m, TotalTokens = 1000 }
                }
            }
        };

        var result = ModelBreakdownViewModel.MapRows(rows).Single();

        result.Model.Should().Be("claude-sonnet-4");
        result.Cost.Should().Be(2.5m);
        result.CostText.Should().Be("$2.50");
        result.CostShare.Should().Be(1m);
        result.CostShareText.Should().Be("100.0%");
        result.TotalTokens.Should().Be(1000);
        result.TotalTokensText.Should().Be("1,000");
        result.TokensShare.Should().Be(1m);
        result.TokensShareText.Should().Be("100.0%");
    }

    [Fact]
    public void MapRows_AggregatesSameModelAcrossDays()
    {
        var rows = new[]
        {
            new DailyUsageSummary
            {
                Date = "2026-06-18",
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "claude-sonnet-4", CostUsd = 1m, TotalTokens = 100 }
                }
            },
            new DailyUsageSummary
            {
                Date = "2026-06-19",
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "claude-sonnet-4", CostUsd = 2m, TotalTokens = 200 }
                }
            }
        };

        var result = ModelBreakdownViewModel.MapRows(rows).Single();

        result.Cost.Should().Be(3m);
        result.TotalTokens.Should().Be(300);
    }

    [Fact]
    public void MapRows_ComputesSharesFromModelTotalsAndSortsByCost()
    {
        var rows = new[]
        {
            new DailyUsageSummary
            {
                TotalCostUsd = 999m,
                TotalTokens = 999999,
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "opus", CostUsd = 3m, TotalTokens = 300 },
                    new() { Model = "sonnet", CostUsd = 1m, TotalTokens = 100 }
                }
            }
        };

        var result = ModelBreakdownViewModel.MapRows(rows).ToList();

        result.Select(row => row.Model).Should().Equal("opus", "sonnet");
        result[0].CostShare.Should().Be(0.75m);
        result[0].CostShareText.Should().Be("75.0%");
        result[0].TokensShare.Should().Be(0.75m);
        result[1].CostShare.Should().Be(0.25m);
    }

    [Fact]
    public void MapRows_IgnoresBlankModelNames()
    {
        var rows = new[]
        {
            new DailyUsageSummary
            {
                Models = new List<ModelBreakdown>
                {
                    new() { Model = " ", CostUsd = 10m, TotalTokens = 1000 },
                    new() { Model = "sonnet", CostUsd = 1m, TotalTokens = 100 }
                }
            }
        };

        var result = ModelBreakdownViewModel.MapRows(rows).ToList();

        result.Should().ContainSingle();
        result.Single().Model.Should().Be("sonnet");
        result.Single().CostShare.Should().Be(1m);
    }

    [Fact]
    public void MapRows_WithZeroTotals_DoesNotDivideByZero()
    {
        var rows = new[]
        {
            new DailyUsageSummary
            {
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "sonnet", CostUsd = 0m, TotalTokens = 0 }
                }
            }
        };

        var result = ModelBreakdownViewModel.MapRows(rows).Single();

        result.CostShare.Should().Be(0m);
        result.CostShareText.Should().Be("0.0%");
        result.TokensShare.Should().Be(0m);
        result.TokensShareText.Should().Be("0.0%");
    }

    [Fact]
    public async Task LoadAsync_WithNoModelData_ShowsEmptyState()
    {
        var viewModel = new ModelBreakdownViewModel(new CacheService(new FakeUsageStore(new List<DailyUsageSummary>
        {
            new() { Date = "2026-06-19", TotalCostUsd = 1m, TotalTokens = 1000 }
        })));

        await viewModel.LoadAsync();

        viewModel.Rows.Should().BeEmpty();
        viewModel.HasData.Should().BeFalse();
        viewModel.Status.Should().Be("no model data");
        viewModel.ModelCount.Should().Be(0);
        viewModel.TotalCost.Should().Be(0m);
        viewModel.TotalCostText.Should().Be("$0.00");
        viewModel.TotalTokens.Should().Be(0);
        viewModel.TotalTokensText.Should().Be("0");
    }

    [Fact]
    public async Task LoadAsync_WithModelData_UpdatesSummary()
    {
        var viewModel = new ModelBreakdownViewModel(new CacheService(new FakeUsageStore(new List<DailyUsageSummary>
        {
            new()
            {
                Date = "2026-06-19",
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "sonnet", CostUsd = 1.5m, TotalTokens = 1000 },
                    new() { Model = "opus", CostUsd = 2m, TotalTokens = 2000 }
                }
            }
        })));

        await viewModel.LoadAsync();

        viewModel.Rows.Should().HaveCount(2);
        viewModel.HasData.Should().BeTrue();
        viewModel.Status.Should().Be("loaded 2 models");
        viewModel.ModelCount.Should().Be(2);
        viewModel.TotalCost.Should().Be(3.5m);
        viewModel.TotalCostText.Should().Be("$3.50");
        viewModel.TotalTokens.Should().Be(3000);
        viewModel.TotalTokensText.Should().Be("3,000");
    }

    private sealed class FakeUsageStore : IUsageStore
    {
        private readonly List<UsageFact> _facts;

        public FakeUsageStore(List<DailyUsageSummary> rows)
        {
            _facts = UsageFactsMapper.FromDailySummaries(rows);
        }

        public Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
        {
            _facts.AddRange(facts);
            return Task.CompletedTask;
        }

        public Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
        {
            return UpsertUsageFactsAsync(facts, ct);
        }

        public Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<UsageFact>>(_facts.ToList());
        }

        public Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<SessionFact>>(new List<SessionFact>());
        }
    }
}
