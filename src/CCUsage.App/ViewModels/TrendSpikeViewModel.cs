using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CCUsage.Core.Services;

namespace CCUsage.App.ViewModels;

/// <summary>
/// [SPIKE D-002] 30 天成本趋势 spike 的数据源：经 CacheService 读取真实 facts，取最近 30 天。
/// 临时页专用，正式集成（v0.4 Dashboard 趋势卡）时会被替换或删除。
/// </summary>
public class TrendSpikePoint
{
    public DateTime Date { get; init; }
    public double CostUsd { get; init; }
    public double Tokens { get; init; }
}

public partial class TrendSpikeViewModel : ViewModelBase
{
    public const int WindowDays = 30;

    private readonly CacheService _cacheService;
    private readonly Stopwatch _loadStopwatch = new();

    [ObservableProperty]
    private IReadOnlyList<TrendSpikePoint> _points = Array.Empty<TrendSpikePoint>();

    [ObservableProperty]
    private string _status = "loading";

    [ObservableProperty]
    private string _dataRangeText = string.Empty;

    public TrendSpikeViewModel(CacheService cacheService)
    {
        _cacheService = cacheService;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loadStopwatch.Restart();
        try
        {
            var rows = await _cacheService.GetDailyRowsAsync();
            var window = rows
                .OrderByDescending(r => r.Date, StringComparer.Ordinal) // yyyy-MM-dd 可按字符串排序
                .Take(WindowDays)
                .OrderBy(r => r.Date, StringComparer.Ordinal)
                .Select(r => new TrendSpikePoint
                {
                    Date = DateTime.ParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    CostUsd = (double)r.TotalCostUsd,
                    Tokens = r.TotalTokens,
                })
                .ToList();

            _loadStopwatch.Stop();
            Points = window;
            Status = window.Count == 0
                ? "no data（本地 facts 为空，可先在 Dashboard 点刷新）"
                : $"loaded {window.Count} rows in {_loadStopwatch.ElapsedMilliseconds} ms";

            if (window.Count > 0)
            {
                DataRangeText = $"{window[0].Date:yyyy-MM-dd} .. {window[^1].Date:yyyy-MM-dd} · " +
                                $"合计 ${window.Sum(p => p.CostUsd):F2} · 峰值 ${window.Max(p => p.CostUsd):F2}/day";
            }
        }
        catch (Exception ex)
        {
            Status = $"error: {ex.Message}";
        }
    }
}
