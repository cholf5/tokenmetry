using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TokenPulse.App.Localization;
using TokenPulse.Core.Models;
using TokenPulse.Core.Services;
using TokenPulse.Core.Abstractions;

namespace TokenPulse.App.ViewModels;

/// <summary>
/// 后台采集刷新锚点（Q-002 定案）：旧 DashboardView 已删除，本 VM <b>不再挂载任何视图</b>，
/// 以纯 headless VM 保留——它是刷新线的行为锚点，删除会改变采集线语义：
/// <list type="bullet">
/// <item>BackgroundRefreshWiring.Attach(dashboard, shell) → RefreshCommand：定时器 / 启动首刷 /
///       设置页「重新扫描」三条触发共用同一轮（P0 回归锁 Di_settings_rescan_drives_real_dashboard_refresh_line）；</item>
/// <item>RefreshAsync 内 = 本地采集 facts 直解析（GetDailyUsageAsync）+ 非 facts 持久化源的落库 +
///       成本异常提醒（AnomalyDetector + ISystemNotification，设置可关）；</item>
/// <item>成败口径：Status == "error" 即本轮失败（接线层据此向设置页如实上报）。</item>
/// </list>
/// KPI 状态属性（TodayCostUsd 等）仅被本类 Status/HasData 计算与测试消费，无 UI 展示；
/// 若未来需要拆轻量 RefreshCoordinator，须先迁移上述三点并保住 BackgroundRefreshServiceTests
/// 与 SettingsPageTests 的 P0 回归锁。
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private readonly DashboardService _dashboardService;
    private readonly CacheService _cacheService;
    private readonly IUsageDataSource _dataSource;
    private readonly AnomalyDetector _anomalyDetector;
    private readonly DateRangeService _dateService;
    private readonly ISystemNotification _notification;
    private readonly AppSettings _settings;

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
    [NotifyPropertyChangedFor(nameof(IsDebugInfoVisible))]
    private string _debugInfo = string.Empty;

    public bool IsDebugInfoVisible => _settings.ShowDebugInfo && !string.IsNullOrWhiteSpace(DebugInfo);

    public DashboardViewModel(
        DashboardService dashboardService,
        CacheService cacheService,
        IUsageDataSource dataSource,
        AnomalyDetector anomalyDetector,
        DateRangeService dateService,
        ISystemNotification notification,
        AppSettings settings)
    {
        _dashboardService = dashboardService;
        _cacheService = cacheService;
        _dataSource = dataSource;
        _anomalyDetector = anomalyDetector;
        _dateService = dateService;
        _notification = notification;
        _settings = settings;
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
        var rows = await _cacheService.GetDailyRowsAsync();
        if (rows.Count > 0)
        {
            UpdateFromDashboard(_dashboardService.BuildDashboard(rows));
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

    /// <summary>异常通知用户文案（internal 供测试对账）：daily_cost_spike → Translations 取串
    /// （{0}=统计天数，{1}=倍数，InvariantCulture 与全应用数值口径一致）；未知 Kind 回落数据层原文。</summary>
    internal static string FormatAnomalyMessage(AnomalyNotice anomaly, int historyDays) => anomaly.Kind switch
    {
        "daily_cost_spike" => string.Format(
            CultureInfo.InvariantCulture,
            Translations.Instance.AnomalyCostSpikeFormat,
            historyDays,
            anomaly.Ratio),
        _ => anomaly.Message,
    };

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
                // 本地采集源（I-005）已在数据源内部写 facts 并读回，再走 SaveDailyRowsAsync 会把
                // full 粒度行有损重分解为 CLI 形状的分解行；仅自持持久化的源需要调用方落库。
                if (!_dataSource.PersistsFacts)
                {
                    await _cacheService.SaveDailyRowsAsync(rows);
                }

                var dashboard = _dashboardService.BuildDashboard(rows);
                UpdateFromDashboard(dashboard);

                // 检测异常
                var today = rows.FirstOrDefault(r => r.Date == _dateService.Today()) ?? new DailyUsageSummary();
                var recentHistory = rows.Take(7).ToList();
                var anomalies = _anomalyDetector.DetectAnomalies(today, recentHistory, minAbsoluteCost: 0.05m);

                if (_settings.AnomalyNotificationsEnabled && anomalies.Any())
                {
                    foreach (var anomaly in anomalies)
                    {
                        // 用户可见文案在 App 层按 Kind 实时取串（Core 的 AnomalyNotice.Message 是
                        // 数据层细节，不直接展示——语言跟随 UI）；未知 Kind 如实回落原文。
                        await _notification.ShowAsync(
                            Translations.Instance.AnomalyToastTitle,
                            FormatAnomalyMessage(anomaly, recentHistory.Count));
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
