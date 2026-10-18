using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// 启动数据初始化（BeginStartupDataRefreshAsync）与首次初始化遮罩的回归锁：
/// 1. 进程打开即触发一轮刷新（与定时器/托盘/设置页重扫同一 BackgroundRefreshService 单飞槽），
///    顶栏即时切到"正在扫描"反馈——首轮不再静默等用户手动点刷新；
/// 2. facts 库完全无数据（首次使用）→ 遮罩挂起（IsInitializingData），本轮结束（成败均然）摘除，
///    顶栏状态灯如实呈现 no data / error 终态，遮罩不掩盖结果；
/// 3. 库内有数据（回访用户）→ 不挂遮罩，照常触发启动轮（顶栏可见扫描态）；
/// 4. 探测本身失败 → 不挂遮罩但照常刷新（宁可不遮罩不误挡）；
/// 5. 未注入刷新服务（测试 7 参构造）→ 如实 no-op，状态停留启动态；
/// 6. MainWindow 的 InitializingOverlay 可见性随 IsInitializingData 翻转（XAML 绑定锁）。
/// </summary>
public class StartupInitializationTests
{
    // ── 可控 stub 查询服务：日序列返回物可设置 / 可抛错，探测调用可计数 ──

    private sealed class StubQueryService : IUsageQueryService
    {
        public int DailyCalls { get; private set; }

        public IReadOnlyList<DailyUsagePoint>? DailySeries { get; init; }

        public bool ThrowOnDailySeries { get; init; }

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            DailyCalls++;
            if (ThrowOnDailySeries)
            {
                throw new InvalidOperationException("probe boom");
            }

            return Task.FromResult(DailySeries ?? (IReadOnlyList<DailyUsagePoint>)Array.Empty<DailyUsagePoint>());
        }

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new UsageKpiResult());

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            Task.FromResult(new SessionListResult());

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult());
    }

    private sealed class StubToast : IToastService
    {
        public void Show(string message, Visual? host = null)
        {
        }
    }

    /// <summary>轮询等待条件成立（探测在 Task.Run 上执行，完成时刻非确定；RunJobs 泵 UI 续体）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindowViewModel Shell, StubQueryService Query) CreateShell(
        StubQueryService? query = null,
        BackgroundRefreshService? refreshService = null)
    {
        var stubQuery = query ?? new StubQueryService();
        var filters = new FilterStateService();
        var toast = new StubToast();
        var catalog = new AgentCatalogService(stubQuery);
        // AppServices 由 headless 宿主初始化（同 ManualRefreshTests.CreateShell 的装配方式）
        var shell = new MainWindowViewModel(
            filters,
            AppServices.Get<SettingsViewModel>(),
            AppServices.Get<DataPricingViewModel>(),
            stubQuery,
            catalog,
            toast,
            AppServices.Get<Core.Services.PricingEngine>(),
            refreshService);
        Dispatcher.UIThread.RunJobs();
        return (shell, stubQuery);
    }

    private static (BackgroundRefreshService Service, TaskCompletionSource Gate, Func<int> Runs) GatedService()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var service = new BackgroundRefreshService(async () =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
            return true;
        });
        return (service, gate, () => Volatile.Read(ref runs));
    }

    [AvaloniaFact]
    public async Task Empty_store_shows_initializing_overlay_during_startup_round_then_clears()
    {
        var (service, gate, runs) = GatedService();
        var (shell, _) = CreateShell(refreshService: service);

        var startup = shell.BeginStartupDataRefreshAsync();
        // 等待条件 = 断言目标本身：IsInitializingData 置位即蕴含探测已返回"空库"且触发已发生
        await WaitUntilAsync(() => shell.IsInitializingData);

        // 首次使用（库内无数据）：遮罩挂起 + 顶栏即时进入扫描态（不再停留"启动中…"）
        Assert.True(shell.IsInitializingData, "库内无数据时应挂初始化遮罩");
        Assert.Equal("正在扫描本地数据…", shell.DataStatusText);
        Assert.True(runs() >= 1, "启动路径应立即触发一轮刷新");

        gate.SetResult();
        await startup;
        Dispatcher.UIThread.RunJobs();

        // 轮次结束（本轮成功）遮罩摘除
        Assert.False(shell.IsInitializingData, "启动轮结束后遮罩应摘除");
    }

    [AvaloniaFact]
    public async Task Empty_store_round_failing_still_clears_overlay()
    {
        // 成败均摘：动作上报失败（对应采集线 Status="error"）时遮罩同样摘除，
        // 终态交由顶栏状态灯如实呈现——遮罩不得挡住错误态
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new BackgroundRefreshService(async () =>
        {
            await gate.Task;
            return false;
        });
        var (shell, _) = CreateShell(refreshService: service);

        var startup = shell.BeginStartupDataRefreshAsync();
        await WaitUntilAsync(() => shell.IsInitializingData);
        Assert.True(shell.IsInitializingData);

        gate.SetResult();
        await startup;
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.IsInitializingData, "失败轮也应摘除遮罩（不掩盖错误终态）");
    }

    [AvaloniaFact]
    public async Task Nonempty_store_skips_overlay_but_startup_round_still_runs()
    {
        var (service, gate, runs) = GatedService();
        var (shell, _) = CreateShell(
            query: new StubQueryService
            {
                DailySeries = new[] { new DailyUsagePoint { Day = "2026-10-15" } },
            },
            refreshService: service);

        var startup = shell.BeginStartupDataRefreshAsync();
        // 等待触发发生（蕴含探测已完成且返回"有数据"）
        await WaitUntilAsync(() => runs() >= 1);

        // 回访用户：库内有数据 → 不挂遮罩，但启动轮照常触发（顶栏可见扫描态）
        Assert.False(shell.IsInitializingData, "库内有数据时不挂初始化遮罩");
        Assert.Equal("正在扫描本地数据…", shell.DataStatusText);

        gate.SetResult();
        await startup;
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.IsInitializingData);
    }

    [AvaloniaFact]
    public async Task Probe_failure_skips_overlay_but_still_triggers_refresh()
    {
        var (service, gate, runs) = GatedService();
        var (shell, _) = CreateShell(
            query: new StubQueryService { ThrowOnDailySeries = true },
            refreshService: service);

        var startup = shell.BeginStartupDataRefreshAsync();
        // 异常路径的续体比正常路径多一跳，直接等待触发发生（蕴含探测已失败返回）
        await WaitUntilAsync(() => runs() >= 1);

        Assert.False(shell.IsInitializingData, "探测失败按未知处理，不挂遮罩");

        gate.SetResult();
        await startup;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Without_refresh_service_startup_initialization_is_noop()
    {
        // 测试 7 参构造（未注入刷新服务）：如实 no-op，状态停留启动态
        var (shell, _) = CreateShell();

        await shell.BeginStartupDataRefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.IsInitializingData);
        Assert.Equal("启动中…", shell.DataStatusText);
    }

    [AvaloniaFact]
    public void Initializing_overlay_visibility_follows_view_model()
    {
        var (shell, _) = CreateShell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var overlay = window.GetVisualDescendants()
            .OfType<Panel>()
            .First(p => p.Name == "InitializingOverlay");
        Assert.False(overlay.IsVisible, "默认（有数据/未启动初始化）遮罩不可见");

        shell.IsInitializingData = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(overlay.IsVisible, "IsInitializingData=true 时全窗遮罩可见");

        shell.IsInitializingData = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(overlay.IsVisible);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
