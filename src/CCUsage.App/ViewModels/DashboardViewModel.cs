using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Core.Abstractions;

namespace CCUsage.App.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly DashboardService _dashboardService;
    private readonly CacheService _cacheService;
    private readonly IUsageDataSource _dataSource;
    private readonly AnomalyDetector _anomalyDetector;
    private readonly DateRangeService _dateService;
    private readonly ISystemNotification _notification;

    [ObservableProperty]
    private decimal _todayCostUsd;

    [ObservableProperty]
    private decimal _weekCostUsd;

    [ObservableProperty]
    private decimal _monthCostUsd;

    [ObservableProperty]
    private long _todayTokens;

    [ObservableProperty]
    private string _latestDataDate = string.Empty;

    [ObservableProperty]
    private decimal _latestDayCostUsd;

    [ObservableProperty]
    private long _latestDayTokens;

    [ObservableProperty]
    private decimal _allTimeCostUsd;

    [ObservableProperty]
    private long _allTimeTokens;

    [ObservableProperty]
    private string _status = "loading";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private bool _hasHistoricalData;

    [ObservableProperty]
    private bool _hasCurrentPeriodData;

    [ObservableProperty]
    private bool _shouldShowHistoricalOnlyHint;

    [ObservableProperty]
    private string _historicalOnlyHint = string.Empty;

    [ObservableProperty]
    private string _debugInfo = string.Empty;

    public DashboardViewModel(
        DashboardService dashboardService,
        CacheService cacheService,
        IUsageDataSource dataSource,
        AnomalyDetector anomalyDetector,
        DateRangeService dateService,
        ISystemNotification notification)
    {
        _dashboardService = dashboardService;
        _cacheService = cacheService;
        _dataSource = dataSource;
        _anomalyDetector = anomalyDetector;
        _dateService = dateService;
        _notification = notification;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await LoadFromCacheAsync();

        if (!HasData)
        {
            await RefreshAsync();
        }
    }

    private async Task LoadFromCacheAsync()
    {
        var dashboard = await _cacheService.GetLatestDashboardAsync();
        if (dashboard != null)
        {
            UpdateFromDashboard(dashboard);
        }
    }

    private void UpdateFromDashboard(DashboardSummary summary)
    {
        TodayCostUsd = summary.TodayCostUsd;
        WeekCostUsd = summary.WeekCostUsd;
        MonthCostUsd = summary.MonthCostUsd;
        TodayTokens = summary.TodayTokens;
        LatestDataDate = summary.LatestDataDate;
        LatestDayCostUsd = summary.LatestDayCostUsd;
        LatestDayTokens = summary.LatestDayTokens;
        AllTimeCostUsd = summary.AllTimeCostUsd;
        AllTimeTokens = summary.AllTimeTokens;
        Status = summary.Status;
        HasHistoricalData = HasHistoricalDataAvailable(summary);
        HasCurrentPeriodData = HasCurrentPeriodDataAvailable(summary);
        ShouldShowHistoricalOnlyHint = ShouldShowHistoricalOnlyHintFor(summary);
        HistoricalOnlyHint = ShouldShowHistoricalOnlyHint
            ? "当前周期暂无用量，但缓存中存在历史记录。请查看下方最近一天和历史累计数据。"
            : string.Empty;
        HasData = HasHistoricalData || HasCurrentPeriodData;
    }

    public static bool HasHistoricalDataAvailable(DashboardSummary summary)
    {
        return !string.IsNullOrWhiteSpace(summary.LatestDataDate) || summary.AllTimeCostUsd > 0 || summary.AllTimeTokens > 0;
    }

    public static bool HasCurrentPeriodDataAvailable(DashboardSummary summary)
    {
        return summary.TodayCostUsd > 0 || summary.WeekCostUsd > 0 || summary.MonthCostUsd > 0 || summary.TodayTokens > 0;
    }

    public static bool ShouldShowHistoricalOnlyHintFor(DashboardSummary summary)
    {
        return HasHistoricalDataAvailable(summary) && !HasCurrentPeriodDataAvailable(summary);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "refreshing";
        DebugInfo = string.Empty;
        try
        {
            var rows = await _dataSource.GetDailyUsageAsync();
            DebugInfo = $"获取到 {rows.Count} 条数据记录";

            if (rows.Any())
            {
                var dashboard = _dashboardService.BuildDashboard(rows);
                await _cacheService.SaveDashboardAsync(dashboard);
                await _cacheService.SaveDailyRowsAsync(rows);
                UpdateFromDashboard(dashboard);

                // 检测异常
                var today = rows.FirstOrDefault(r => r.Date == _dateService.Today()) ?? new DailyUsageSummary();
                var anomalies = _anomalyDetector.DetectAnomalies(today, rows.Take(7).ToList(), minAbsoluteCost: 0.05m);

                if (anomalies.Any())
                {
                    foreach (var anomaly in anomalies)
                    {
                        await _notification.ShowAsync("AI 成本异常提醒", anomaly.Message);
                    }
                }
            }
            else
            {
                Status = "no data";
                HasData = false;
            }
        }
        catch (Exception ex)
        {
            Status = "error";
            DebugInfo = $"错误: {ex.Message}";
            HasData = false;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
