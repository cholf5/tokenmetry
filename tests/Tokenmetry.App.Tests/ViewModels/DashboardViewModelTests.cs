using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.Tests.ViewModels;

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
    public void Constructor_WarmsCacheOnly_DoesNotTriggerRefresh()
    {
        // 首轮采集刷新由 App 启动路径 BeginStartupDataRefreshAsync 唯一持有；构造时机只热缓存，
        // 不自触发扫描（旧版在缓存为空时于 UI 线程上内联整条采集管道，冻结首启窗口）。
        var dataSource = new FakeUsageDataSource(new List<DailyUsageSummary>());

        _ = CreateDashboardViewModel(
            new FakeUsageStore(),
            dataSource,
            new AppSettings(),
            new FixedDateRangeService("2026-06-19"));

        dataSource.CallCount.Should().Be(0);
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

    [Fact]
    public async Task RefreshAsync_SavesCache_WhenDataSourceDoesNotPersistFacts()
    {
        var store = new FakeUsageStore();
        var viewModel = CreateDashboardViewModel(
            store,
            new FakeUsageDataSource(new List<DailyUsageSummary> { new() { Date = "2026-06-19", TotalCostUsd = 1m } }),
            new AppSettings(),
            new FixedDateRangeService("2026-06-19"));

        await viewModel.RefreshAsync();

        store.ReplaceCalls.Should().Be(1);
    }

    [Fact]
    public async Task RefreshAsync_SkipsCacheSave_WhenDataSourcePersistsFacts()
    {
        // I-005：本地采集源已在数据源内部写 facts 并读回，重复落库会把 full 行有损重分解。
        var store = new FakeUsageStore();
        var viewModel = CreateDashboardViewModel(
            store,
            new FakeUsageDataSource(new List<DailyUsageSummary> { new() { Date = "2026-06-19", TotalCostUsd = 1m } }, persistsFacts: true),
            new AppSettings(),
            new FixedDateRangeService("2026-06-19"));

        await viewModel.RefreshAsync();

        store.ReplaceCalls.Should().Be(0);
        viewModel.HasData.Should().BeTrue();
    }

    private static DashboardViewModel CreateDashboardViewModel(
        ISystemNotification notification,
        AppSettings settings,
        DateRangeService dateRangeService,
        IEnumerable<DailyUsageSummary> rows)
    {
        return CreateDashboardViewModel(notification, new FakeUsageStore(), new FakeUsageDataSource(rows.ToList()), settings, dateRangeService);
    }

    private static DashboardViewModel CreateDashboardViewModel(
        FakeUsageStore store,
        FakeUsageDataSource dataSource,
        AppSettings settings,
        DateRangeService dateRangeService)
    {
        return CreateDashboardViewModel(new FakeSystemNotification(), store, dataSource, settings, dateRangeService);
    }

    private static DashboardViewModel CreateDashboardViewModel(
        ISystemNotification notification,
        FakeUsageStore store,
        FakeUsageDataSource dataSource,
        AppSettings settings,
        DateRangeService dateRangeService)
    {
        return new DashboardViewModel(
            new DashboardService(dateRangeService),
            new CacheService(store),
            dataSource,
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
        private readonly bool _persistsFacts;

        public FakeUsageDataSource(List<DailyUsageSummary> rows, bool persistsFacts = false)
        {
            _rows = rows;
            _persistsFacts = persistsFacts;
        }

        public bool PersistsFacts => _persistsFacts;

        public int CallCount { get; private set; }

        public Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default)
        {
            CallCount++;
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

    private sealed class FakeUsageStore : IUsageStore
    {
        private readonly List<UsageFact> _facts = new();

        public FakeUsageStore(params DailyUsageSummary[] seededRows)
        {
            _facts.AddRange(UsageFactsMapper.FromDailySummaries(seededRows));
        }

        public int ReplaceCalls { get; private set; }

        public Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
        {
            _facts.AddRange(facts);
            return Task.CompletedTask;
        }

        public Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default)
        {
            ReplaceCalls++;
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
