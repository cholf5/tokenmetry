using CCUsage.App.Services;
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
        var viewModel = new ReportsViewModel(new CacheService(new FakeUsageStore(new List<DailyUsageSummary>())));

        await viewModel.LoadAsync();

        viewModel.Rows.Should().BeEmpty();
        viewModel.HasData.Should().BeFalse();
        viewModel.Status.Should().Be("no data");
    }

    [Fact]
    public async Task ExportCsvAsync_ExportsCurrentFilteredRows()
    {
        var exporter = new FakeReportCsvExporter(ReportCsvExportResult.Exported(2));
        var viewModel = new ReportsViewModel(new CacheService(new FakeUsageStore(CreateDailyRows())), exporter);
        await viewModel.LoadAsync();
        viewModel.SearchText = "sonnet";
        viewModel.ApplyFilters();

        await viewModel.ExportCsvAsync();

        exporter.ExportCalls.Should().Be(1);
        exporter.ExportedRows.Select(row => row.Date).Should().Equal("2026-06-19", "2026-06-18");
        viewModel.Status.Should().Be("exported 2 rows");
        viewModel.IsExporting.Should().BeFalse();
    }

    [Fact]
    public async Task ExportCsvAsync_WithNoRows_DoesNotCallExporter()
    {
        var exporter = new FakeReportCsvExporter(ReportCsvExportResult.Exported(0));
        var viewModel = new ReportsViewModel(new CacheService(new FakeUsageStore(new List<DailyUsageSummary>())), exporter);
        await viewModel.LoadAsync();

        await viewModel.ExportCsvAsync();

        exporter.ExportCalls.Should().Be(0);
        viewModel.Status.Should().Be("no rows to export");
    }

    [Fact]
    public async Task ExportCsvAsync_WhenExporterFails_PreservesRowsAndShowsError()
    {
        var exporter = new FakeReportCsvExporter(ReportCsvExportResult.Failed("disk full"));
        var viewModel = new ReportsViewModel(new CacheService(new FakeUsageStore(CreateDailyRows())), exporter);
        await viewModel.LoadAsync();
        var originalDates = viewModel.Rows.Select(row => row.Date).ToList();

        await viewModel.ExportCsvAsync();

        viewModel.Rows.Select(row => row.Date).Should().Equal(originalDates);
        viewModel.Status.Should().Be("error: disk full");
        viewModel.IsExporting.Should().BeFalse();
    }

    [Fact]
    public async Task ExportCsvAsync_WhenUserCancels_ShowsCanceledStatus()
    {
        var exporter = new FakeReportCsvExporter(ReportCsvExportResult.CanceledByUser());
        var viewModel = new ReportsViewModel(new CacheService(new FakeUsageStore(CreateDailyRows())), exporter);
        await viewModel.LoadAsync();

        await viewModel.ExportCsvAsync();

        viewModel.Status.Should().Be("export canceled");
    }

    [Fact]
    public void ApplyReportQuery_SortsByCostDescending()
    {
        var result = ReportsViewModel.ApplyReportQuery(CreateReportRows(), new ReportsQuery(null, null, string.Empty, ReportsViewModel.SortByCost, true)).ToList();

        result.Select(row => row.Cost).Should().Equal(5m, 3m, 1m);
    }

    [Fact]
    public void ApplyReportQuery_SortsByTotalTokensDescending()
    {
        var result = ReportsViewModel.ApplyReportQuery(CreateReportRows(), new ReportsQuery(null, null, string.Empty, ReportsViewModel.SortByTotalTokens, true)).ToList();

        result.Select(row => row.TotalTokens).Should().Equal(500, 300, 100);
    }

    [Fact]
    public void ApplyReportQuery_SortsByDateAscendingAndDescending()
    {
        var ascending = ReportsViewModel.ApplyReportQuery(CreateReportRows(), new ReportsQuery(null, null, string.Empty, ReportsViewModel.SortByDate, false)).ToList();
        var descending = ReportsViewModel.ApplyReportQuery(CreateReportRows(), new ReportsQuery(null, null, string.Empty, ReportsViewModel.SortByDate, true)).ToList();

        ascending.Select(row => row.Date).Should().Equal("2026-06-17", "2026-06-18", "2026-06-19");
        descending.Select(row => row.Date).Should().Equal("2026-06-19", "2026-06-18", "2026-06-17");
    }

    [Fact]
    public void ApplyReportQuery_FiltersByDateRange()
    {
        var result = ReportsViewModel.ApplyReportQuery(
            CreateReportRows(),
            new ReportsQuery(new DateOnly(2026, 6, 18), new DateOnly(2026, 6, 19), string.Empty, ReportsViewModel.SortByDate, true)).ToList();

        result.Select(row => row.Date).Should().Equal("2026-06-19", "2026-06-18");
        result.Sum(row => row.Cost).Should().Be(8m);
    }

    [Fact]
    public void ApplyReportQuery_FiltersByAgentOrModelTextCaseInsensitive()
    {
        var agentResult = ReportsViewModel.ApplyReportQuery(CreateReportRows(), new ReportsQuery(null, null, "ZCODE", ReportsViewModel.SortByDate, true)).ToList();
        var modelResult = ReportsViewModel.ApplyReportQuery(CreateReportRows(), new ReportsQuery(null, null, "opus", ReportsViewModel.SortByDate, true)).ToList();

        agentResult.Select(row => row.Date).Should().Equal("2026-06-18");
        modelResult.Select(row => row.Date).Should().Equal("2026-06-17");
    }

    [Fact]
    public async Task ApplyFilters_UpdatesFilteredCountAndTotalCost()
    {
        var viewModel = new ReportsViewModel(new CacheService(new FakeUsageStore(CreateDailyRows())));
        await viewModel.LoadAsync();

        viewModel.SearchText = "sonnet";
        viewModel.ApplyFilters();

        viewModel.Rows.Should().HaveCount(2);
        viewModel.FilteredCount.Should().Be(2);
        viewModel.FilteredTotalCost.Should().Be(8m);
        viewModel.FilteredTotalCostText.Should().Be("$8.00");
    }

    [Fact]
    public async Task ClearFilters_RestoresFullListAndDefaultDateDescendingSort()
    {
        var viewModel = new ReportsViewModel(new CacheService(new FakeUsageStore(CreateDailyRows())));
        await viewModel.LoadAsync();

        viewModel.SearchText = "opus";
        viewModel.SelectedSort = ReportsViewModel.SortByCost;
        viewModel.IsDescending = false;
        viewModel.ApplyFilters();

        viewModel.ClearFilters();

        viewModel.Rows.Select(row => row.Date).Should().Equal("2026-06-19", "2026-06-18", "2026-06-17");
        viewModel.SearchText.Should().BeEmpty();
        viewModel.SelectedSort.Should().Be(ReportsViewModel.SortByDate);
        viewModel.IsDescending.Should().BeTrue();
        viewModel.FilteredCount.Should().Be(3);
    }

    [Fact]
    public async Task ApplyFilters_WithInvalidDate_DoesNotThrowAndPreservesRows()
    {
        var viewModel = new ReportsViewModel(new CacheService(new FakeUsageStore(CreateDailyRows())));
        await viewModel.LoadAsync();

        var originalDates = viewModel.Rows.Select(row => row.Date).ToList();
        viewModel.StartDateText = "not-a-date";
        viewModel.ApplyFilters();

        viewModel.Rows.Select(row => row.Date).Should().Equal(originalDates);
        viewModel.Status.Should().Be("invalid start date, use yyyy-MM-dd");
    }

    private static List<DailyUsageSummary> CreateDailyRows()
    {
        return new List<DailyUsageSummary>
        {
            new()
            {
                Date = "2026-06-17",
                TotalCostUsd = 1m,
                TotalTokens = 100,
                Agents = new List<AgentBreakdown> { new() { Agent = "claude-code" } },
                Models = new List<ModelBreakdown> { new() { Model = "claude-opus-4" } }
            },
            new()
            {
                Date = "2026-06-19",
                TotalCostUsd = 5m,
                TotalTokens = 500,
                Agents = new List<AgentBreakdown> { new() { Agent = "claude-code" } },
                Models = new List<ModelBreakdown> { new() { Model = "claude-sonnet-4" } }
            },
            new()
            {
                Date = "2026-06-18",
                TotalCostUsd = 3m,
                TotalTokens = 300,
                Agents = new List<AgentBreakdown> { new() { Agent = "zcode" } },
                Models = new List<ModelBreakdown> { new() { Model = "claude-sonnet-4" } }
            }
        };
    }

    private static List<ReportsDailyRow> CreateReportRows()
    {
        return ReportsViewModel.MapRows(CreateDailyRows()).ToList();
    }

    private sealed class FakeReportCsvExporter : IReportCsvExporter
    {
        private readonly ReportCsvExportResult _result;

        public FakeReportCsvExporter(ReportCsvExportResult result)
        {
            _result = result;
        }

        public int ExportCalls { get; private set; }
        public List<ReportsDailyRow> ExportedRows { get; } = new();

        public Task<ReportCsvExportResult> ExportAsync(IReadOnlyList<ReportsDailyRow> rows, CancellationToken ct = default)
        {
            ExportCalls++;
            ExportedRows.Clear();
            ExportedRows.AddRange(rows);
            return Task.FromResult(_result);
        }
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

        public Task DeleteDecompositionFactsAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
