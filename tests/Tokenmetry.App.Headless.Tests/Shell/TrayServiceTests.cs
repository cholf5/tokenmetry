using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Tray;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// A-0 托盘常驻的 headless 验证（headless 宿主无桌面生命周期，App 启动的托盘接线块不执行，
/// 由本测试直接构造 TrayService 覆盖同一初始化路径）：
/// 1. Initialize 经 TrayIcon.SetIcons 注册托盘（唯一挂载路径，修复前图标永不出现）；
/// 2. 菜单规格 = 打开主窗 / 立即刷新 / 设置 / 退出（v0.5 规格docs/plans §5；今日成本行与
///    快捷信息窗项已按用户要求移除——成本仅剩 tooltip 出口，弹层走托盘左键）；
/// 3. UpdateToolTip 回写 tooltip（修复前为死方法，托盘永远不显示成本）；
/// 4. 用户关窗 = 隐藏到托盘（可再唤回），不真正销毁窗口（修复前关窗即退出）。
/// D-014 状态栏文本直显经真机取证不可行（Avalonia 方形状态项裁剪宽位图，卡内 B1），
/// 已回落模板图标——相关接线与测试一并撤除，紧凑格式化器保留在 App.Tests（D-015 复用）。
/// 真机托盘出现 / 退出进程不在 headless 能力内，留给发布文档人工核验项。
/// </summary>
public class TrayServiceTests : IDisposable
{
    // SetIcons 会整集合替换 Application.Icons；每个用例自建图标并在收尾移除，避免会话级串扰
    private readonly MainWindow _window = new();

    public void Dispose()
    {
        // GetIcons 在从未 SetIcons 时返回 null（本类不注册托盘的用例）
        var icons = TrayIcon.GetIcons(Application.Current!);
        if (icons is not null)
        {
            foreach (var icon in icons.ToList())
            {
                icons.Remove(icon);
            }
        }

        _window.Close();
    }

    [AvaloniaFact]
    public void Initialize_registers_tray_icon_with_spec_menu()
    {
        new TrayService(_window).Initialize();

        var icon = SingleRegisteredIcon();
        Assert.NotNull(icon);
        Assert.True(icon!.IsVisible);
        Assert.NotNull(icon.Icon); // A-0 #2：图标资产已内嵌，不再是 null 占位

        var headers = icon.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
        Assert.Contains("打开主窗", headers);
        Assert.Contains("立即刷新", headers);
        Assert.Contains("设置", headers);
        Assert.Contains(Translations.Instance.TrayPriceCalculator, headers); // I-CALC 计价器（独立工具入口）
        Assert.Contains("退出", headers);
        Assert.Null(CostItem(icon)); // 今日成本行已移除：成本只走 tooltip（Avalonia 路径）
        Assert.DoesNotContain(headers, h => h as string == "快捷信息窗"); // 弹层入口只剩托盘左键
    }

    [AvaloniaFact]
    public void UpdateToolTip_updates_tooltip_only()
    {
        var tray = new TrayService(_window);
        tray.Initialize();
        var icon = SingleRegisteredIcon();
        Assert.NotNull(icon);

        tray.UpdateToolTip("今日成本：$12.50");

        Assert.Equal("今日成本：$12.50", icon!.ToolTipText);
        Assert.Null(CostItem(icon)); // 菜单里没有成本行可回写
    }

    [AvaloniaFact]
    public async Task UpdateToolTip_from_background_thread_is_marshaled_to_ui_thread()
    {
        var tray = new TrayService(_window);
        tray.Initialize();
        var icon = SingleRegisteredIcon();
        Assert.NotNull(icon);

        // 回归：后台刷新线程（BackgroundRefreshWiring 的 Task.Run）经 DashboardViewModel
        // 的 PropertyChanged 同步回调进 UpdateToolTip；直接改 ToolTipText 会抛跨线程异常，
        // 沿回调链冒进 RefreshAsync，整轮扫描被误判为失败（顶栏「数据源错误」）。
        await Task.Run(() => tray.UpdateToolTip("今日成本：$9.99"));

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("今日成本：$9.99", icon!.ToolTipText);
    }

    [AvaloniaFact]
    public void User_close_hides_window_to_tray_and_allows_recall()
    {
        var window = new MainWindow();
        window.Show();
        Assert.True(window.IsVisible);

        window.Close(); // 拦截为隐藏：进程常驻（修复前关窗即退出）
        Assert.False(window.IsVisible);

        window.Show(); // 托盘「打开主窗」可唤回——窗口未被销毁
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Language_switch_updates_menu_headers_in_place_without_replacing_menu()
    {
        // 语言切换回归（真机实测）：macOS 的 NativeMenu 导出器缓存首个挂载实例，语言切换
        // 若整体替换 _trayIcon.Menu 会抛 ArgumentException("The menu being updated does not
        // match.") 且沿 PropertyChanged 同步链打断切换——正确做法是就地更新各菜单项 Header。
        var tray = new TrayService(_window);
        tray.Initialize();
        var icon = SingleRegisteredIcon();
        Assert.NotNull(icon);
        var menuBefore = icon!.Menu;

        var originalLanguage = Translations.Instance.CurrentLanguage;
        try
        {
            Translations.Instance.SetLanguage(AppLanguage.English);

            Assert.Same(menuBefore, icon.Menu); // 菜单实例不替换
            var headers = icon.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
            Assert.Contains("Open window", headers);
            Assert.Contains("Refresh now", headers);
            Assert.Contains("Quit", headers);

            Translations.Instance.SetLanguage(originalLanguage);
            var headersRestored = icon.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
            Assert.Contains("打开主窗", headersRestored);
            Assert.Contains("退出", headersRestored);
        }
        finally
        {
            Translations.Instance.SetLanguage(originalLanguage); // 进程级单例，恢复基线
        }
    }

    [AvaloniaFact]
    public void WriteMacItemTitles_skips_separator_items_with_null_title()
    {
        // interop 语言切换重译回归（真机必现 NRE）：分隔线项 Title 恒为 null（BuildMacTrayItems
        // 以 null! 占位），重写循环必须与 MacTrayInterop.Install 建项循环同纪律跳过分隔线；
        // headless 宿主 _macInterop 恒 false 不可达，由本测试直接钉住循环行为。
        var invoked = new List<int>();
        var items = new List<MacTrayInterop.MacTrayItem>
        {
            new(() => { invoked.Add(0); return "cost"; }, command: null, enabled: false),
            new(null!, isSeparator: true),
            new(() => { invoked.Add(2); return "open"; }),
        };

        TrayService.WriteMacItemTitles(items); // 修复前：items[1].Title() → NullReferenceException

        Assert.Equal(new[] { 0, 2 }, invoked); // 非分隔线项全量重写（保安装序号），分隔线跳过
    }

    [AvaloniaFact]
    public void Initialize_builds_today_summary_tooltip_from_dashboard()
    {
        // tooltip 两行：首行产品名（既有 TrayTooltip），次行今日摘要（tokens/成本/缓存命中）。
        var dashboard = CreateDashboard(todayTokens: 255_000_000, todayCostUsd: 8.8m, cacheShare: 0.983m);
        var tray = new TrayService(_window, dashboard, null, null);

        tray.Initialize();

        var tooltip = SingleRegisteredIcon()!.ToolTipText!;
        Assert.Equal(Translations.Instance.TrayTooltip, tooltip.Split('\n')[0]);
        var summary = tooltip.Split('\n')[1];
        Assert.Contains("8.80", summary);
        Assert.Contains("98.3%", summary);
        Assert.Contains(TrendChart.FormatCompactTokens(255_000_000), summary);
    }

    [AvaloniaFact]
    public void Initialize_falls_back_to_title_only_without_dashboard()
    {
        // 旧构造（headless/旧测试宿主）：无 dashboard 可读，tooltip 保持产品名单行
        new TrayService(_window).Initialize();

        Assert.DoesNotContain("\n", SingleRegisteredIcon()!.ToolTipText);
    }

    [AvaloniaFact]
    public void Initialize_shows_no_data_line_when_dashboard_has_no_data()
    {
        // 有 dashboard 但无数据（新装/错误轮）：不冒充 0，显诚实的无数据行（TrayCostNoData 口径平移）
        var dashboard = CreateDashboard(todayTokens: 0, todayCostUsd: 0m, cacheShare: null, hasData: false);
        var tray = new TrayService(_window, dashboard, null, null);

        tray.Initialize();

        var lines = SingleRegisteredIcon()!.ToolTipText!.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(Translations.Instance.TrayTodayNoData, lines[1]);
    }

    [AvaloniaFact]
    public async Task Dashboard_kpi_update_rewrites_today_summary_tooltip()
    {
        // 后台轮回写路径：PropertyChanged → UpdateToolTip，摘要行随 KPI 翻新（含新命中率）
        var dashboard = CreateDashboard(todayTokens: 1_000, todayCostUsd: 0.1m, cacheShare: null);
        var tray = new TrayService(_window, dashboard, null, null);
        tray.Initialize();

        dashboard.TodayTokens = 255_000_000;
        dashboard.TodayCostUsd = 8.8m;
        dashboard.TodayCacheReadShare = 0.983m;
        Dispatcher.UIThread.RunJobs();

        var summary = SingleRegisteredIcon()!.ToolTipText!.Split('\n')[1];
        Assert.Contains("8.80", summary);
        Assert.Contains("98.3%", summary);
    }

    [AvaloniaFact]
    public async Task Dashboard_kpi_update_rewrites_tooltip_from_background_thread()
    {
        // 跨线程回归（既有 UpdateToolTip 语义）：后台线程触发的 PropertyChanged 同步回调不得抛
        var dashboard = CreateDashboard(todayTokens: 0, todayCostUsd: 0m, cacheShare: null, hasData: false);
        var tray = new TrayService(_window, dashboard, null, null);
        tray.Initialize();

        await Task.Run(() =>
        {
            dashboard.HasData = true;
            dashboard.TodayTokens = 255_000_000;
            dashboard.TodayCostUsd = 8.8m;
            dashboard.TodayCacheReadShare = 0.983m;
        });
        Dispatcher.UIThread.RunJobs();

        var summary = SingleRegisteredIcon()!.ToolTipText!.Split('\n')[1];
        Assert.Contains("98.3%", summary);
    }

    private static DashboardViewModel CreateDashboard(long todayTokens, decimal todayCostUsd, decimal? cacheShare, bool hasData = true)
    {
        var rows = hasData
            ? new List<DailyUsageSummary>
            {
                new()
                {
                    Date = "2026-11-02",
                    TotalTokens = todayTokens,
                    CacheReadTokens = todayTokens,
                    TotalCostUsd = todayCostUsd
                }
            }
            : new List<DailyUsageSummary>();
        var dateService = new FixedTodayDateRangeService();
        var dashboard = new DashboardViewModel(
            new DashboardService(dateService),
            new CacheService(new NoopUsageStore()),
            new StaticUsageDataSource(rows),
            new AnomalyDetector(),
            dateService,
            new AppSettings());
        if (hasData)
        {
            // 模拟 UpdateFromDashboard 写入后的 KPI 快照（HasData 由刷新线回写）
            dashboard.HasData = true;
            dashboard.TodayTokens = todayTokens;
            dashboard.TodayCostUsd = todayCostUsd;
            dashboard.TodayCacheReadShare = cacheShare;
        }

        return dashboard;
    }

    private sealed class FixedTodayDateRangeService : DateRangeService
    {
        public override string Today() => "2026-11-02";
    }

    private sealed class StaticUsageDataSource(List<DailyUsageSummary> rows) : IUsageDataSource
    {
        public Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default) => Task.FromResult(rows);
    }

    private sealed class NoopUsageStore : IUsageStore
    {
        public Task<string> GetLocalDeviceIdAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task<string> GetLocalDeviceNameAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task SetLocalDeviceNameAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertRemoteUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertRemoteSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UsageFact>> GetLocalUsageFactsSinceAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UsageFact>>(Array.Empty<UsageFact>());
        public Task<IReadOnlyList<SessionFact>> GetLocalSessionFactsSinceAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionFact>>(Array.Empty<SessionFact>());
        public Task<(DateTimeOffset? LastPushedAt, long LastPullSeq)> GetSyncCursorAsync(CancellationToken ct = default) => Task.FromResult<(DateTimeOffset?, long)>((null, 0L));
        public Task SetSyncCursorAsync(DateTimeOffset? lastPushedAt, long? lastPullSeq, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Tokenmetry.Core.Sync.SyncDeviceInfo>> GetRemoteDevicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Tokenmetry.Core.Sync.SyncDeviceInfo>>(Array.Empty<Tokenmetry.Core.Sync.SyncDeviceInfo>());
        public Task UpsertRemoteDevicesAsync(IReadOnlyList<Tokenmetry.Core.Sync.SyncDeviceInfo> devices, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteRemoteDeviceDataAsync(string deviceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UsageFact>>([]);
        public Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionFact>>([]);
        public Task DeleteDecompositionFactsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static TrayIcon? SingleRegisteredIcon()
    {
        var icons = TrayIcon.GetIcons(Application.Current!);
        return icons is { Count: 1 } ? icons[0] : null;
    }

    private static NativeMenuItem? CostItem(TrayIcon icon) =>
        icon.Menu?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header?.StartsWith("今日成本") == true);
}
