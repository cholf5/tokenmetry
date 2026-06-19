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
    private readonly CacheService _cacheService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private string _status = "loading";

    public ObservableCollection<ReportsDailyRow> Rows { get; } = new();

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

            Rows.Clear();
            foreach (var row in reportRows)
            {
                Rows.Add(row);
            }

            HasData = Rows.Count > 0;
            Status = HasData ? $"loaded {Rows.Count} rows" : "no data";
        }
        catch (Exception ex)
        {
            Rows.Clear();
            HasData = false;
            Status = $"error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public static IEnumerable<ReportsDailyRow> MapRows(IEnumerable<DailyUsageSummary> rows)
    {
        return rows
            .OrderByDescending(row => GetSortDate(row.Date))
            .ThenByDescending(row => row.Date, StringComparer.Ordinal)
            .Select(ReportsDailyRow.FromSummary);
    }

    private static DateOnly GetSortDate(string date)
    {
        return DateOnly.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateOnly.MinValue;
    }
}

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
