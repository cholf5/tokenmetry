using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TokenPulse.App.DependencyInjection;
using TokenPulse.App.Services;
using TokenPulse.App.ViewModels;
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;
using TokenPulse.Core.Services;
using TokenPulse.Infrastructure.Cache;
using TokenPulse.Infrastructure.Notifications;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TokenPulse.App.Headless.Tests.Shell;

/// <summary>
/// 手动刷新接线（顶栏 ⟳）与"刷新轮次完成后页面重查"的回归锁：
/// 1. 顶栏 RefreshDataCommand 经与定时器/托盘/设置页重扫同一 BackgroundRefreshService 单飞槽
///    真实触发一轮，触发即回写"正在扫描本地数据…"扫描中反馈，命令完成后按钮复位；
/// 2. BackgroundRefreshWiring 一轮完成（mock 采集源 → facts 落库）后，当前可见页面与右栏最近
///    会话经 IDataRefreshAware 重查，外壳"最近更新"时间戳与数据源状态同步回写——不重查则
///    facts 已更新而可见页面停留在导航/筛选时的旧数据；
/// 3. 页面重查按"当前可见页"路由（导航到模型页后只重查模型页，不可见的总览页不动）。
/// </summary>
public class ManualRefreshTests
{
    // ── 记账 stub 查询服务 ──

    private sealed class CountingQueryService : IUsageQueryService
    {
        public int KpiCalls { get; private set; }

        public int DailyCalls { get; private set; }

        public int AgentCalls { get; private set; }

        public int ModelCalls { get; private set; }

        public int ProjectCalls { get; private set; }

        public int RecentTopNCalls { get; private set; }

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            KpiCalls++;
            return Task.FromResult(new UsageKpiResult());
        }

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            DailyCalls++;
            return Task.FromResult<IReadOnlyList<DailyUsagePoint>>(Array.Empty<DailyUsagePoint>());
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            AgentCalls++;
            return Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            ModelCalls++;
            return Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            ProjectCalls++;
            return Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());
        }

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default)
        {
            if (query.PageSize == SessionsPanelViewModel.RecentTopN)
            {
                RecentTopNCalls++;
            }

            return Task.FromResult(new SessionListResult());
        }

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult());
    }

    private sealed class StubToast : IToastService
    {
        public void Show(string message, Visual? host = null)
        {
        }
    }

    private static (MainWindowViewModel Shell, CountingQueryService Query) CreateShell(
        BackgroundRefreshService? refreshService = null)
    {
        var query = new CountingQueryService();
        var filters = new FilterStateService();
        var toast = new StubToast();
        var catalog = new AgentCatalogService(query);
        var panel = new SessionsPanelViewModel(toast, query, catalog, filters);
        // AppServices 由 headless 宿主初始化（同 DimensionPagesTests.CreateShell 的装配方式）
        var shell = new MainWindowViewModel(
            filters,
            AppServices.Get<SettingsViewModel>(),
            panel,
            query,
            catalog,
            toast,
            AppServices.Get<Core.Services.PricingEngine>(),
            refreshService);
        Dispatcher.UIThread.RunJobs();
        return (shell, query);
    }

    [AvaloniaFact]
    public async Task RefreshDataCommand_triggers_shared_refresh_service_with_scanning_feedback()
    {
        var triggers = 0;
        var refreshService = new BackgroundRefreshService(() =>
        {
            Interlocked.Increment(ref triggers);
            return Task.FromResult(true);
        });
        var (shell, query) = CreateShell(refreshService);
        var kpiBaseline = query.KpiCalls;

        await shell.RefreshDataCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, triggers);
        Assert.Equal("正在扫描本地数据…", shell.DataStatusText);
        Assert.False(shell.RefreshDataCommand.IsRunning, "命令完成后按钮复位可用");
        // 未挂载 BackgroundRefreshWiring（本测试无采集线）：轮次结束不会产生页面重查
        Assert.Equal(kpiBaseline, query.KpiCalls);
    }

    [AvaloniaFact]
    public async Task Refresh_round_completion_reloads_current_page_and_recent_sessions()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ccusage-manual-refresh-{Guid.NewGuid():N}.db");
        try
        {
            // mock 源（PersistsFacts=false → 轮内真实走 CacheService 落库）+ 临时 facts 库
            var dashboard = new DashboardViewModel(
                new DashboardService(new DateRangeService()),
                new CacheService(new SqliteUsageStore(dbPath)),
                new MockUsageDataSource(),
                new AnomalyDetector(),
                new DateRangeService(),
                new NullSystemNotification(),
                new AppSettings());
            var (shell, query) = CreateShell();
            var kpiBaseline = query.KpiCalls;
            var recentBaseline = query.RecentTopNCalls;

            var wiring = new BackgroundRefreshWiring();
            wiring.Attach(dashboard, shell);
            Assert.True(await wiring.RunRefreshAsync(), "mock 源返回非空行，本轮应如实上报成功");
            Dispatcher.UIThread.RunJobs();

            Assert.True(query.KpiCalls > kpiBaseline, "刷新轮完成后当前可见页（总览）重查");
            Assert.True(query.RecentTopNCalls > recentBaseline, "右栏最近会话同步重查");
            Assert.StartsWith("最近更新", shell.LastUpdatedText);
            Assert.False(shell.IsDataSourceError, "成功轮不点亮错误状态灯");
        }
        finally
        {
            // A-1（Windows）：清连接池再删临时库，POSIX 上无副作用
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [AvaloniaFact]
    public void RefreshCurrentPageData_reloads_whatever_page_is_visible()
    {
        var (shell, query) = CreateShell();
        var kpiBaseline = query.KpiCalls;
        shell.GoToPageCommand.Execute(PageId.Models);
        Dispatcher.UIThread.RunJobs();
        var modelBaseline = query.ModelCalls;

        shell.RefreshCurrentPageData();
        Dispatcher.UIThread.RunJobs();

        Assert.True(query.ModelCalls > modelBaseline, "当前可见页（模型统计）被重查");
        Assert.Equal(kpiBaseline, query.KpiCalls); // 不可见的总览页不被重查
    }
}
