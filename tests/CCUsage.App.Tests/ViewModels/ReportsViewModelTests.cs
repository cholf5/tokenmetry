using CCUsage.App.ViewModels;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

namespace CCUsage.App.Tests.ViewModels;

public class ReportsViewModelTests
{
    [Fact]
    public void MapRows_SortsByDateDescending()
    {
        var rows = new[]
        {
            new DailyUsageSummary { Date = "2026-06-17", TotalCostUsd = 1m },
            new DailyUsageSummary { Date = "2026-06-19", TotalCostUsd = 3m },
            new DailyUsageSummary { Date = "2026-06-18", TotalCostUsd = 2m }
        };

        var result = ReportsViewModel.MapRows(rows).ToList();

        result.Select(row => row.Date).Should().Equal("2026-06-19", "2026-06-18", "2026-06-17");
    }

    [Fact]
    public void MapRows_FormatsCostTokensAgentsAndModels()
    {
        var rows = new[]
        {
            new DailyUsageSummary
            {
                Date = "2026-06-19",
                TotalCostUsd = 12.345m,
                TotalTokens = 1234567,
                InputTokens = 1000,
                OutputTokens = 2000,
                CacheCreationTokens = 3000,
                CacheReadTokens = 4000,
                Agents = new List<AgentBreakdown>
                {
                    new() { Agent = "claude-code" },
                    new() { Agent = "zcode" }
                },
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "claude-sonnet-4" },
                    new() { Model = "claude-opus-4" }
                }
            }
        };

        var result = ReportsViewModel.MapRows(rows).Single();

        result.Cost.Should().Be(12.345m);
        result.CostText.Should().Be("$12.35");
        result.TotalTokens.Should().Be(1234567);
        result.TotalTokensText.Should().Be("1,234,567");
        result.InputTokensText.Should().Be("1,000");
        result.OutputTokensText.Should().Be("2,000");
        result.CacheCreationTokensText.Should().Be("3,000");
        result.CacheReadTokensText.Should().Be("4,000");
        result.AgentsText.Should().Be("claude-code, zcode");
        result.ModelsText.Should().Be("claude-sonnet-4, claude-opus-4");
    }

    [Fact]
    public async Task LoadAsync_WithEmptyCache_ShowsEmptyState()
    {
        var viewModel = new ReportsViewModel(new CacheService(new FakeUsageCache(new List<DailyUsageSummary>())));

        await viewModel.LoadAsync();

        viewModel.Rows.Should().BeEmpty();
        viewModel.HasData.Should().BeFalse();
        viewModel.Status.Should().Be("no data");
    }

    private sealed class FakeUsageCache : IUsageCache
    {
        private readonly List<DailyUsageSummary> _rows;

        public FakeUsageCache(List<DailyUsageSummary> rows)
        {
            _rows = rows;
        }

        public Task SaveDashboardAsync(DashboardSummary summary, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<DashboardSummary?> GetLatestDashboardAsync(CancellationToken ct = default)
        {
            return Task.FromResult<DashboardSummary?>(null);
        }

        public Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default)
        {
            _rows.Clear();
            _rows.AddRange(rows);
            return Task.CompletedTask;
        }

        public Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_rows);
        }
    }
}
