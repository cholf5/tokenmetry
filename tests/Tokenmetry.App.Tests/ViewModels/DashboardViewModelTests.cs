using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.Tests.ViewModels;

public class DashboardViewModelTests
{
    [Fact]
    public async Task RefreshAsync_HidesDebugInfo_WhenDisabledInSettings()
    {
        var viewModel = CreateDashboardViewModel(
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
            new AppSettings { ShowDebugInfo = true },
            new FixedDateRangeService("2026-06-19"),
            new[] { new DailyUsageSummary { Date = "2026-06-19", TotalCostUsd = 1m } });

        await viewModel.RefreshAsync();

        viewModel.DebugInfo.Should().NotBeEmpty();
        viewModel.IsDebugInfoVisible.Should().BeTrue();
    }

    // spike 夹具行（今日 10 / 历史 1、1 → 均值 4、倍率 2.5）供异常事件用例共享
    private static List<DailyUsageSummary> SpikeRows() => new()
    {
        new DailyUsageSummary { Date = "2026-06-19", TotalCostUsd = 10m },
        new DailyUsageSummary { Date = "2026-06-18", TotalCostUsd = 1m },
        new DailyUsageSummary { Date = "2026-06-17", TotalCostUsd = 1m },
    };

    // ---- 异常检测（Q-016：in-app toast 通知通道删除，AnomalyDetected 事件是异常唯一出口）----

    [Fact]
    public async Task RefreshAsync_AnomalyDetected_CarriesFormattedMessageAndRatio()
    {
        // 载荷契约：文案走 FormatAnomalyMessage（按 Kind 取串、语言随 UI）+ 检测器原始倍率
        //（弹层今日花费 KPI 徽标 ↑{ratio}× 均值 文案依赖）。断言与共享格式化器输出比对，语言无关。
        DashboardViewModel.AnomalyRaised? raised = null;
        var viewModel = CreateDashboardViewModel(
            new FakeUsageStore(),
            new FakeUsageDataSource(SpikeRows()),
            new AppSettings(),
            new FixedDateRangeService("2026-06-19"));
        viewModel.AnomalyDetected += (_, a) => raised = a;

        await viewModel.RefreshAsync();

        raised.Should().NotBeNull();
        raised!.Ratio.Should().Be(2.5m);
        raised.Message.Should().Be(DashboardViewModel.FormatAnomalyMessage(
            new AnomalyNotice { Kind = "daily_cost_spike", Ratio = 2.5m }, 3));
    }

    [Fact]
    public async Task RefreshAsync_AnomalyDetected_RaisedEveryRound_WhileAnomalyHolds()
    {
        // 弹层徽标契约（Q-011）：徽标靠"异常轮次每轮置回、无异常轮次清空"维持——
        // 日累计异常全天为真，事件必须每轮照发（检测持续存在）。
        var raised = 0;
        var viewModel = CreateDashboardViewModel(
            new FakeUsageStore(),
            new FakeUsageDataSource(SpikeRows()),
            new AppSettings(),
            new FixedDateRangeService("2026-06-19"));
        viewModel.AnomalyDetected += (_, _) => raised++;

        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();
        await viewModel.RefreshAsync();

        raised.Should().Be(3);
    }

    [Fact]
    public async Task RefreshAsync_NoAnomalyRaised_WhenCostIsNormal()
    {
        // 正常态（倍率未达阈值）不 raise——弹层徽标清除路径的前提。
        var raised = 0;
        var viewModel = CreateDashboardViewModel(
            new FakeUsageStore(),
            new FakeUsageDataSource(new List<DailyUsageSummary>
            {
                new() { Date = "2026-06-19", TotalCostUsd = 1m },
                new() { Date = "2026-06-18", TotalCostUsd = 1m },
                new() { Date = "2026-06-17", TotalCostUsd = 1m },
            }),
            new AppSettings(),
            new FixedDateRangeService("2026-06-19"));
        viewModel.AnomalyDetected += (_, _) => raised++;

        await viewModel.RefreshAsync();

        raised.Should().Be(0);
    }

    [Fact]
    public void Constructor_WarmsCacheOnly_DoesNotTriggerRefresh()
    {
        // 首轮采集刷新由 App 启动路径 BeginStartupDataRefreshAsync 唯一持有；构造时机只热缓存，
        // 不自触发扫描（旧版在 UI 线程上内联整条采集管道，冻结首启窗口）。
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
        AppSettings settings,
        DateRangeService dateRangeService,
        IEnumerable<DailyUsageSummary> rows)
    {
        return CreateDashboardViewModel(
            new FakeUsageStore(), new FakeUsageDataSource(rows.ToList()), settings, dateRangeService);
    }

    private static DashboardViewModel CreateDashboardViewModel(
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
