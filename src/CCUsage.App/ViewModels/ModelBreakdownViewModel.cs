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

public partial class ModelBreakdownViewModel : ViewModelBase
{
    private readonly CacheService _cacheService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private string _status = "loading";

    [ObservableProperty]
    private int _modelCount;

    [ObservableProperty]
    private decimal _totalCost;

    [ObservableProperty]
    private string _totalCostText = "$0.00";

    [ObservableProperty]
    private long _totalTokens;

    [ObservableProperty]
    private string _totalTokensText = "0";

    public ObservableCollection<ModelBreakdownRow> Rows { get; } = new();

    public ModelBreakdownViewModel(CacheService cacheService)
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
            var dailyRows = await _cacheService.GetDailyRowsAsync();
            var rows = MapRows(dailyRows).ToList();
            ReplaceRows(rows);
            Status = HasData ? $"loaded {Rows.Count} models" : "no model data";
        }
        catch (Exception ex)
        {
            Rows.Clear();
            UpdateSummary();
            HasData = false;
            Status = $"error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public static IEnumerable<ModelBreakdownRow> MapRows(IEnumerable<DailyUsageSummary> rows)
    {
        var groupedModels = rows
            .SelectMany(row => row.Models)
            .Where(model => !string.IsNullOrWhiteSpace(model.Model))
            .GroupBy(model => model.Model.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Model = group.First().Model.Trim(),
                Cost = group.Sum(model => model.CostUsd),
                TotalTokens = group.Sum(model => model.TotalTokens)
            })
            .ToList();

        var totalCost = groupedModels.Sum(model => model.Cost);
        var totalTokens = groupedModels.Sum(model => model.TotalTokens);

        return groupedModels
            .Select(model => ModelBreakdownRow.FromValues(
                model.Model,
                model.Cost,
                totalCost == 0m ? 0m : model.Cost / totalCost,
                model.TotalTokens,
                totalTokens == 0 ? 0m : (decimal)model.TotalTokens / totalTokens))
            .OrderByDescending(row => row.Cost)
            .ThenByDescending(row => row.TotalTokens)
            .ThenBy(row => row.Model, StringComparer.OrdinalIgnoreCase);
    }

    private void ReplaceRows(List<ModelBreakdownRow> rows)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        HasData = Rows.Count > 0;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        ModelCount = Rows.Count;
        TotalCost = Rows.Sum(row => row.Cost);
        TotalCostText = TotalCost.ToString("$0.00", CultureInfo.InvariantCulture);
        TotalTokens = Rows.Sum(row => row.TotalTokens);
        TotalTokensText = FormatTokens(TotalTokens);
    }

    private static string FormatTokens(long value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }
}

public sealed class ModelBreakdownRow
{
    public string Model { get; init; } = string.Empty;
    public decimal Cost { get; init; }
    public string CostText { get; init; } = string.Empty;
    public decimal CostShare { get; init; }
    public string CostShareText { get; init; } = string.Empty;
    public long TotalTokens { get; init; }
    public string TotalTokensText { get; init; } = string.Empty;
    public decimal TokensShare { get; init; }
    public string TokensShareText { get; init; } = string.Empty;

    public static ModelBreakdownRow FromValues(string model, decimal cost, decimal costShare, long totalTokens, decimal tokensShare)
    {
        return new ModelBreakdownRow
        {
            Model = model,
            Cost = cost,
            CostText = cost.ToString("$0.00", CultureInfo.InvariantCulture),
            CostShare = costShare,
            CostShareText = FormatShare(costShare),
            TotalTokens = totalTokens,
            TotalTokensText = totalTokens.ToString("N0", CultureInfo.InvariantCulture),
            TokensShare = tokensShare,
            TokensShareText = FormatShare(tokensShare)
        };
    }

    private static string FormatShare(decimal value)
    {
        return (value * 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }
}
