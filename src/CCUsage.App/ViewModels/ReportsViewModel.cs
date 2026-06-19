using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

namespace CCUsage.App.ViewModels;

public partial class ReportsViewModel : ViewModelBase
{
    public const string SortByDate = "Date";
    public const string SortByCost = "Cost";
    public const string SortByTotalTokens = "Total Tokens";

    private readonly CacheService _cacheService;
    private readonly List<ReportsDailyRow> _allRows = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private string _status = "loading";

    [ObservableProperty]
    private string _startDateText = string.Empty;

    [ObservableProperty]
    private string _endDateText = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedSort = SortByDate;

    [ObservableProperty]
    private bool _isDescending = true;

    [ObservableProperty]
    private int _filteredCount;

    [ObservableProperty]
    private decimal _filteredTotalCost;

    [ObservableProperty]
    private string _filteredTotalCostText = "$0.00";

    public ObservableCollection<ReportsDailyRow> Rows { get; } = new();
    public IReadOnlyList<string> SortOptions { get; } = new[] { SortByDate, SortByCost, SortByTotalTokens };

    public ReportsViewModel(CacheService cacheService)
    {
        _cacheService = cacheService;
        _ = LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        Status = "loading";

        try
        {
            var rows = await _cacheService.GetDailyRowsAsync();
            var reportRows = MapRows(rows).ToList();

            _allRows.Clear();
            _allRows.AddRange(reportRows);
            ApplyFilters();

            Status = HasData ? $"loaded {Rows.Count} rows" : "no data";
        }
        catch (Exception ex)
        {
            _allRows.Clear();
            Rows.Clear();
            UpdateFilteredSummary();
            HasData = false;
            Status = $"error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void ApplyFilters()
    {
        if (!TryCreateQuery(out var query, out var error))
        {
            Status = error;
            return;
        }

        var filteredRows = ApplyReportQuery(_allRows, query).ToList();
        ReplaceRows(filteredRows);
        Status = HasData ? $"filtered {Rows.Count} of {_allRows.Count} rows" : "no data";
    }

    [RelayCommand]
    public void ClearFilters()
    {
        StartDateText = string.Empty;
        EndDateText = string.Empty;
        SearchText = string.Empty;
        SelectedSort = SortByDate;
        IsDescending = true;
        ApplyFilters();
    }

    public static IEnumerable<ReportsDailyRow> MapRows(IEnumerable<DailyUsageSummary> rows)
    {
        return rows
            .OrderByDescending(row => GetSortDate(row.Date))
            .ThenByDescending(row => row.Date, StringComparer.Ordinal)
            .Select(ReportsDailyRow.FromSummary);
    }

    public static IEnumerable<ReportsDailyRow> ApplyReportQuery(IEnumerable<ReportsDailyRow> rows, ReportsQuery query)
    {
        var result = rows;

        if (query.StartDate.HasValue)
        {
            result = result.Where(row => GetSortDate(row.Date) >= query.StartDate.Value);
        }

        if (query.EndDate.HasValue)
        {
            result = result.Where(row => GetSortDate(row.Date) <= query.EndDate.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            result = result.Where(row =>
                row.AgentsText.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase) ||
                row.ModelsText.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase));
        }

        return SortRows(result, query.SortBy, query.IsDescending);
    }

    public static bool TryParseReportDate(string value, out DateOnly? date)
    {
        date = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }

    private bool TryCreateQuery(out ReportsQuery query, out string error)
    {
        query = new ReportsQuery(null, null, SearchText, SelectedSort, IsDescending);
        error = string.Empty;

        if (!TryParseReportDate(StartDateText, out var startDate))
        {
            error = "invalid start date, use yyyy-MM-dd";
            return false;
        }

        if (!TryParseReportDate(EndDateText, out var endDate))
        {
            error = "invalid end date, use yyyy-MM-dd";
            return false;
        }

        if (startDate.HasValue && endDate.HasValue && startDate.Value > endDate.Value)
        {
            error = "start date must be before end date";
            return false;
        }

        query = new ReportsQuery(startDate, endDate, SearchText, SelectedSort, IsDescending);
        return true;
    }

    private void ReplaceRows(List<ReportsDailyRow> rows)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        HasData = Rows.Count > 0;
        UpdateFilteredSummary();
    }

    private void UpdateFilteredSummary()
    {
        FilteredCount = Rows.Count;
        FilteredTotalCost = Rows.Sum(row => row.Cost);
        FilteredTotalCostText = FilteredTotalCost.ToString("$0.00", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<ReportsDailyRow> SortRows(IEnumerable<ReportsDailyRow> rows, string sortBy, bool isDescending)
    {
        return sortBy switch
        {
            SortByCost => isDescending
                ? rows.OrderByDescending(row => row.Cost).ThenByDescending(row => GetSortDate(row.Date))
                : rows.OrderBy(row => row.Cost).ThenByDescending(row => GetSortDate(row.Date)),
            SortByTotalTokens => isDescending
                ? rows.OrderByDescending(row => row.TotalTokens).ThenByDescending(row => GetSortDate(row.Date))
                : rows.OrderBy(row => row.TotalTokens).ThenByDescending(row => GetSortDate(row.Date)),
            _ => isDescending
                ? rows.OrderByDescending(row => GetSortDate(row.Date)).ThenByDescending(row => row.Date, StringComparer.Ordinal)
                : rows.OrderBy(row => GetSortDate(row.Date)).ThenBy(row => row.Date, StringComparer.Ordinal)
        };
    }

    private static DateOnly GetSortDate(string date)
    {
        return DateOnly.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateOnly.MinValue;
    }
}

public sealed record ReportsQuery(
    DateOnly? StartDate,
    DateOnly? EndDate,
    string SearchText,
    string SortBy,
    bool IsDescending);

public sealed class ReportsDailyRow
{
    public string Date { get; init; } = string.Empty;
    public decimal Cost { get; init; }
    public string CostText { get; init; } = string.Empty;
    public long TotalTokens { get; init; }
    public string TotalTokensText { get; init; } = string.Empty;
    public long InputTokens { get; init; }
    public string InputTokensText { get; init; } = string.Empty;
    public long OutputTokens { get; init; }
    public string OutputTokensText { get; init; } = string.Empty;
    public long CacheCreationTokens { get; init; }
    public string CacheCreationTokensText { get; init; } = string.Empty;
    public long CacheReadTokens { get; init; }
    public string CacheReadTokensText { get; init; } = string.Empty;
    public string AgentsText { get; init; } = string.Empty;
    public string ModelsText { get; init; } = string.Empty;

    public static ReportsDailyRow FromSummary(DailyUsageSummary summary)
    {
        return new ReportsDailyRow
        {
            Date = summary.Date,
            Cost = summary.TotalCostUsd,
            CostText = summary.TotalCostUsd.ToString("$0.00", CultureInfo.InvariantCulture),
            TotalTokens = summary.TotalTokens,
            TotalTokensText = FormatTokens(summary.TotalTokens),
            InputTokens = summary.InputTokens,
            InputTokensText = FormatTokens(summary.InputTokens),
            OutputTokens = summary.OutputTokens,
            OutputTokensText = FormatTokens(summary.OutputTokens),
            CacheCreationTokens = summary.CacheCreationTokens,
            CacheCreationTokensText = FormatTokens(summary.CacheCreationTokens),
            CacheReadTokens = summary.CacheReadTokens,
            CacheReadTokensText = FormatTokens(summary.CacheReadTokens),
            AgentsText = string.Join(", ", summary.Agents.Select(agent => agent.Agent).Where(value => !string.IsNullOrWhiteSpace(value))),
            ModelsText = string.Join(", ", summary.Models.Select(model => model.Model).Where(value => !string.IsNullOrWhiteSpace(value)))
        };
    }

    private static string FormatTokens(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
}
