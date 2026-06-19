using CCUsage.App.ViewModels;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

namespace CCUsage.App.Tests.ViewModels;

public class DashboardViewModelTests
{
    [Fact]
    public async Task RefreshAsync_DoesNotSendAnomalyNotifications_WhenDisabledInSettings()
    {
        var notification = new FakeSystemNotification();
        var viewModel = CreateDashboardViewModel(
            notification,
            new AppSettings { AnomalyNotificationsEnabled = false },
            new FixedDateRangeService("2026-06-19"),
            new[]
            {
                new DailyUsageSummary { Date = "2026-06-19", TotalCostUsd = 10m },
                new DailyUsageSummary { Date = "2026-06-18", TotalCostUsd = 1m },
                new DailyUsageSummary { Date = "2026-06-17", TotalCostUsd = 1m }
            });

        await viewModel.RefreshAsync();

        notification.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_SendsAnomalyNotifications_WhenEnabledInSettings()
    {
        var notification = new FakeSystemNotification();
        var viewModel = CreateDashboardViewModel(
            notification,
            new AppSettings { AnomalyNotificationsEnabled = true },
            new FixedDateRangeService("2026-06-19"),
            new[]
            {
                new DailyUsageSummary { Date = "2026-06-19", TotalCostUsd = 10m },
                new DailyUsageSummary { Date = "2026-06-18", TotalCostUsd = 1m },
                new DailyUsageSummary { Date = "2026-06-17", TotalCostUsd = 1m }
            });

        await viewModel.RefreshAsync();

        notification.Messages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_HidesDebugInfo_WhenDisabledInSettings()
    {
        var viewModel = CreateDashboardViewModel(
            new FakeSystemNotification(),
            new AppSettings { ShowDebugInfo = false },
            new FixedDateRangeService("2026-06-19"),
            new[] { new DailyUsageSummary { Date = "2026-06-19", TotalCostUsd = 1m } });

        await viewModel.RefreshAsync();

        viewModel.DebugInfo.Should().NotBeEmpty();
        viewModel.IsDebugInfoVisible.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_ShowsDebugInfo_WhenEnabledInSettings()
    {
        var viewModel = CreateDashboardViewModel(
            new FakeSystemNotification(),
            new AppSettings { ShowDebugInfo = true },
            new FixedDateRangeService("2026-06-19"),
            new[] { new DailyUsageSummary { Date = "2026-06-19", TotalCostUsd = 1m } });

        await viewModel.RefreshAsync();

        viewModel.DebugInfo.Should().NotBeEmpty();
        viewModel.IsDebugInfoVisible.Should().BeTrue();
    }

    [Fact]
    public void ShouldShowHistoricalOnlyHintFor_ReturnsTrue_WhenOnlyHistoricalDataExists()
    {
        var summary = new DashboardSummary
        {
            TodayCostUsd = 0,
            WeekCostUsd = 0,
            MonthCostUsd = 0,
            TodayTokens = 0,
            LatestDataDate = "2026-01-03",
            LatestDayCostUsd = 2.5m,
            LatestDayTokens = 250,
            AllTimeCostUsd = 4.0m,
            AllTimeTokens = 400
        };

        DashboardViewModel.ShouldShowHistoricalOnlyHintFor(summary).Should().BeTrue();
    }

    [Fact]
    public void ShouldShowHistoricalOnlyHintFor_ReturnsFalse_WhenCurrentPeriodDataExists()
    {
        var summary = new DashboardSummary
        {
            TodayCostUsd = 1.25m,
            WeekCostUsd = 1.25m,
            MonthCostUsd = 1.25m,
            TodayTokens = 125,
            LatestDataDate = "2026-01-10",
            LatestDayCostUsd = 1.25m,
            LatestDayTokens = 125,
            AllTimeCostUsd = 10.0m,
            AllTimeTokens = 1000
        };

        DashboardViewModel.ShouldShowHistoricalOnlyHintFor(summary).Should().BeFalse();
    }

    private static DashboardViewModel CreateDashboardViewModel(
        ISystemNotification notification,
        AppSettings settings,
        DateRangeService dateRangeService,
        IEnumerable<DailyUsageSummary> rows)
    {
        return new DashboardViewModel(
            new DashboardService(dateRangeService),
            new CacheService(new FakeUsageCache()),
            new FakeUsageDataSource(rows.ToList()),
            new AnomalyDetector(),
            dateRangeService,
            notification,
            settings);
    }

    private sealed class FixedDateRangeService : DateRangeService
    {
        private readonly string _today;

        public FixedDateRangeService(string today)
        {
            _today = today;
        }

        public override string Today() => _today;
    }

    private sealed class FakeUsageDataSource : IUsageDataSource
    {
        private readonly List<DailyUsageSummary> _rows;

        public FakeUsageDataSource(List<DailyUsageSummary> rows)
        {
            _rows = rows;
        }

        public Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_rows);
        }
    }

    private sealed class FakeSystemNotification : ISystemNotification
    {
        public List<string> Messages { get; } = new();

        public Task ShowAsync(string title, string message, CancellationToken ct = default)
        {
            Messages.Add($"{title}: {message}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUsageCache : IUsageCache
    {
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
            return Task.CompletedTask;
        }

        public Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new List<DailyUsageSummary>());
        }
    }
}
