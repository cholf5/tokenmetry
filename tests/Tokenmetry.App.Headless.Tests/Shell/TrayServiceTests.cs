using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Tray;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Notifications;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// A-0 托盘常驻的 headless 验证（headless 宿主无桌面生命周期，App 启动的托盘接线块不执行，
/// 由本测试直接构造 TrayService 覆盖同一初始化路径）：
/// 1. Initialize 经 TrayIcon.SetIcons 注册托盘（唯一挂载路径，修复前图标永不出现）；
/// 2. 菜单规格 = 今日成本行 + 打开主窗 / 立即刷新 / 设置 / 退出（v0.5 规格docs/plans §5）；
/// 3. UpdateToolTip 回写 tooltip 与菜单首行（修复前为死方法，托盘永远不显示成本）；
/// 4. 用户关窗 = 隐藏到托盘（可再唤回），不真正销毁窗口（修复前关窗即退出）；
/// 5. D-014：KPI 更新即把状态栏图标替换成文字位图（非模板资产），刷新失败回落模板资产；
///    三态字形的分派逻辑在 App.Tests 的 TrayStatusTextTests 纯函数覆盖。
/// 真机托盘出现 / 退出进程 / 文字位图明暗与 DPI 画质不在 headless 能力内，留给发布文档人工核验项。
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
        Assert.Contains("退出", headers);
        Assert.NotNull(CostItem(icon)); // 今日成本常驻行（macOS 无 tooltip，此行是成本唯一出口）
    }

    [AvaloniaFact]
    public void UpdateToolTip_updates_both_tooltip_and_cost_menu_row()
    {
        var tray = new TrayService(_window);
        tray.Initialize();
        var icon = SingleRegisteredIcon();
        Assert.NotNull(icon);

        tray.UpdateToolTip("今日成本：$12.50");

        Assert.Equal("今日成本：$12.50", icon!.ToolTipText);
        Assert.Equal("今日成本：$12.50", CostItem(icon)!.Header);
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

    private static TrayIcon? SingleRegisteredIcon()
    {
        var icons = TrayIcon.GetIcons(Application.Current!);
        return icons.Count == 1 ? icons[0] : null;
    }

    private static NativeMenuItem? CostItem(TrayIcon icon) =>
        icon.Menu?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header?.StartsWith("今日成本") == true);

    // ── D-014：状态栏文字位图（TC-001 headless 可测部分）──
    // 注意：headless 宿主的 WindowIcon.Save 是 no-op（写出零字节），图标内容只能经
    // TrayService.LastStatusGlyph（internal 接缝）断言字形语义，图标实例换新另证重绘发生。

    [AvaloniaFact]
    public void Today_kpi_update_replaces_icon_with_text_bitmap_and_syncs_menu_row()
    {
        var dashboard = CreateDashboard();
        var tray = new TrayService(_window, dashboard, null, null);
        tray.Initialize();
        var icon = SingleRegisteredIcon();
        Assert.NotNull(icon);
        var noDataIcon = icon!.Icon;
        Assert.NotNull(noDataIcon);
        Assert.Equal("—", tray.LastStatusGlyph); // 无数据初态：诚实降级

        dashboard.TodayCostUsd = 12.46m;
        dashboard.TodayTokens = 42400;
        dashboard.HasData = true;

        var statusIcon = icon.Icon;
        Assert.NotSame(noDataIcon, statusIcon); // 图标被替换（非静止模板资产）
        Assert.Equal("42.4K · $12.46", tray.LastStatusGlyph);
        Assert.Equal("今日成本：$12.46", CostItem(icon)!.Header); // 菜单首行与位图同一信号同步

        // 有值扫描中（周期刷新）：字形不变则不重绘，状态栏不闪动
        dashboard.IsLoading = true;
        Assert.Same(statusIcon, icon.Icon);
        Assert.Equal("42.4K · $12.46", tray.LastStatusGlyph);
    }

    [AvaloniaFact]
    public void Refresh_failure_falls_back_to_template_asset_instead_of_stale_number()
    {
        var dashboard = CreateDashboard();
        var tray = new TrayService(_window, dashboard, null, null);
        tray.Initialize();
        var icon = SingleRegisteredIcon();
        Assert.NotNull(icon);

        dashboard.TodayCostUsd = 12.46m;
        dashboard.TodayTokens = 42400;
        dashboard.HasData = true;
        var statusIcon = icon.Icon!;
        Assert.NotEqual("—", tray.LastStatusGlyph);

        dashboard.Status = "error";
        dashboard.HasData = false;

        // 字形置 null = UpdateStatusImage 按 CreateIcon() 回落纯模板资产（同 Initialize 资产路径），
        // 不留陈旧数字；图标实例换新证明重绘确已发生
        Assert.Null(tray.LastStatusGlyph);
        Assert.NotSame(statusIcon, icon.Icon);
    }

    private static DashboardViewModel CreateDashboard()
    {
        // TrayService 只消费 PropertyChanged；VM 构造时机只热缓存（空 store 无副作用）
        return new DashboardViewModel(
            new DashboardService(new DateRangeService()),
            new CacheService(new EmptyUsageStore()),
            new MockUsageDataSource(),
            new AnomalyDetector(),
            new DateRangeService(),
            new NullSystemNotification(),
            new AppSettings());
    }

    /// <summary>空 facts store：仅满足 DashboardViewModel 构造的热缓存路径，返回空集。</summary>
    private sealed class EmptyUsageStore : IUsageStore
    {
        public Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => Task.CompletedTask;

        public Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UsageFact>>([]);

        public Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionFact>>([]);

        public Task DeleteDecompositionFactsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
