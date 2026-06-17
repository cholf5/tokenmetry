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
    private string _status = "loading";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private string _latestDataDate = string.Empty;

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
        _ = LoadFromCacheAsync();
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
        Status = summary.Status;
        // 只要 Dashboard 被正常创建就认为有数据（即使都是历史数据）
        HasData = true;
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
