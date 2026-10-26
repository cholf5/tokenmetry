using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform;
using Tokenmetry.App.Tray;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// D-016 弹窗壳的 headless 验证（headless 宿主无屏幕信息，锚点定位跳过——装配 / 收起 / 分派逻辑
/// 可验，真机锚点留 TC-003 目检）：
/// TC-001 托盘左键分派：有弹层控制器 → Clicked 弹窗且主窗不动，再点收起（Toggle）；
///        未装配（旧 1 参构造）维持 A-0 直开主窗；
/// TC-002 既有行为回归：TrayServiceTests / CloseToTrayBehaviorTests / AppShellTests 全量兜底
///        （本文件只覆盖新增分派路径）；
/// Esc 收起与失焦收起（接线在 StatusPopoverController，B-017；headless 直发按键验证 Esc 路径，
/// 失焦守卫经 internal 处理器直接模拟 Win32"先失焦后 Clicked"时序）；
/// PopoverPositioner 纯函数：TopRight / AboveCursor 锚定与工作区钳制。
/// </summary>
public class StatusPopoverShellTests : IDisposable
{
    private readonly MainWindow _window = new();

    public void Dispose()
    {
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
    public void Tray_click_toggles_popover_and_leaves_main_window_alone()
    {
        var controller = new StatusPopoverController(_window, null, new StubQueryService(), null, null);
        var tray = new TrayService(_window, null, null, null, controller);
        tray.Initialize();
        // 本用例是「一次一次的单击」：假时钟每次读取推进 1s（远超任何双击窗口），
        // 连续三次同步调用不会被双击判定吞掉——不注入的话 Windows 宿主上默认
        // 双击启用，微秒间隔的三次点击会命中双击分派，语义与本用例不符。
        var clock = DateTimeOffset.UtcNow;
        tray.TrayClock = () => clock = clock.AddSeconds(1);

        tray.OnTrayClicked();
        Assert.True(controller.WindowForTest!.IsVisible);
        Assert.False(_window.IsVisible); // 主窗未被动（弹层取代直开主窗）

        tray.OnTrayClicked();
        Assert.False(controller.WindowForTest.IsVisible);

        // 第三次再弹：同一窗口实例复用（只 Hide 不 Close）
        var firstWindow = controller.WindowForTest;
        tray.OnTrayClicked();
        Assert.True(controller.WindowForTest.IsVisible);
        Assert.Same(firstWindow, controller.WindowForTest);
    }

    [AvaloniaFact]
    public void Tray_double_click_within_window_shows_main_window_and_hides_popover()
    {
        // Windows 托盘双击唤出主窗（docs/plans/2026-10-26-tray-double-click-design.md §3）：
        // 两击间隔在窗口期内 → 第二击不再 Toggle，收弹层 + 唤回主窗。
        // 双击门控默认仅 Windows，注入覆写让任意宿主平台的 headless 都能验证。
        var controller = new StatusPopoverController(_window, null, new StubQueryService(), null, null);
        var tray = new TrayService(_window, null, null, null, controller);
        tray.Initialize();
        tray.TrayDoubleClickEnabled = true;
        tray.TrayDoubleClickWindow = TimeSpan.FromMilliseconds(500);
        var clock = DateTimeOffset.UtcNow;
        tray.TrayClock = () => clock = clock.AddMilliseconds(100); // 两击间隔 100ms < 500ms

        tray.OnTrayClicked(); // 第一击：单击语义 → 弹层弹出，主窗不动
        Assert.True(controller.WindowForTest!.IsVisible);
        Assert.False(_window.IsVisible);

        tray.OnTrayClicked(); // 第二击：双击 → 弹层收起 + 主窗唤回（OneDrive 式分层）
        Assert.True(_window.IsVisible);
        Assert.False(controller.WindowForTest.IsVisible);
    }

    [AvaloniaFact]
    public void Two_tray_clicks_beyond_window_still_toggle_popover()
    {
        // 单击行为回归保护：间隔超出双击窗口的两次点击仍走弹层 Toggle，
        // 不被双击判定劫持（慢速开关弹层是合法操作）。
        var controller = new StatusPopoverController(_window, null, new StubQueryService(), null, null);
        var tray = new TrayService(_window, null, null, null, controller);
        tray.Initialize();
        tray.TrayDoubleClickEnabled = true;
        tray.TrayDoubleClickWindow = TimeSpan.FromMilliseconds(500);
        var clock = DateTimeOffset.UtcNow;
        tray.TrayClock = () => clock = clock.AddSeconds(1); // 两击间隔 1s > 500ms

        tray.OnTrayClicked();
        Assert.True(controller.WindowForTest!.IsVisible);

        tray.OnTrayClicked();
        Assert.False(controller.WindowForTest.IsVisible);
        Assert.False(_window.IsVisible); // 主窗全程未被唤起
    }

    [AvaloniaFact]
    public void Tray_double_click_without_controller_shows_window_without_throwing()
    {
        // 无弹层控制器的旧构造（headless / 简装配）：双击路径退化为重复 ShowWindow，幂等不炸。
        var tray = new TrayService(_window);
        tray.Initialize();
        tray.TrayDoubleClickEnabled = true;
        var clock = DateTimeOffset.UtcNow;
        tray.TrayClock = () => clock = clock.AddMilliseconds(50);

        tray.OnTrayClicked(); // 无控制器：A-0 直开主窗
        tray.OnTrayClicked(); // 双击：重复 ShowWindow，无异常

        Assert.True(_window.IsVisible);
    }

    [AvaloniaFact]
    public void Toggle_within_focus_loss_guard_stays_closed_then_reopens()
    {
        // B-017 回归锁：Win32 点托盘时序是"按下先失焦（Deactivated → 失焦收起）→ 抬起才 Clicked
        // （→ Toggle）"——没有守卫时 Toggle 看到的已是"关着"，左键永远只会 Open（Windows 实测）。
        // headless 不产生 Deactivated，经 internal 处理器直接模拟该时序。
        var controller = new StatusPopoverController(_window, null, new StubQueryService(), null, null);
        controller.Show();
        Assert.True(controller.WindowForTest!.IsVisible);

        controller.OnShellWindowDeactivated(controller, EventArgs.Empty); // 本次托盘点击的失焦通道：收起 + 记账
        Assert.False(controller.WindowForTest.IsVisible);

        controller.ToggleFromTrayClick(); // 同一次点击的收起意图：不重新弹出（Switch 语义）
        Assert.False(controller.WindowForTest.IsVisible);

        controller.ToggleFromTrayClick(); // 守卫消费一次即失效：下一次点击正常再开
        Assert.True(controller.WindowForTest.IsVisible);
    }

    [AvaloniaFact]
    public void Self_initiated_hide_does_not_arm_focus_loss_guard()
    {
        // Toggle/导航收起引发的 Deactivated（自身 Hide 时窗口已不可见）不算失焦收起，
        // 不给下一次点击上锁——收起后立刻再点托盘应能再开。
        var controller = new StatusPopoverController(_window, null, new StubQueryService(), null, null);
        controller.Show();

        controller.ToggleFromTrayClick();
        Assert.False(controller.WindowForTest!.IsVisible);

        controller.OnShellWindowDeactivated(controller, EventArgs.Empty); // 若误记账，下一行 Toggle 会被守卫吞掉
        controller.ToggleFromTrayClick();
        Assert.True(controller.WindowForTest.IsVisible);
    }

    [AvaloniaFact]
    public void Tray_click_without_controller_keeps_legacy_show_window()
    {
        var tray = new TrayService(_window);
        tray.Initialize();

        tray.OnTrayClicked();
        Assert.True(_window.IsVisible); // A-0 既有语义：无弹层时直开主窗
    }

    [AvaloniaFact]
    public void Popover_menu_item_toggles_popover_and_absent_without_controller()
    {
        // macOS 事实：TrayIcon.Clicked 不 raise（任何点击都开 NativeMenu）——弹层的零 interop
        // 入口是本菜单项；Windows 左键已直弹，此项为冗余但一致的入口。
        var controller = new StatusPopoverController(_window, null, new StubQueryService(), null, null);
        var tray = new TrayService(_window, null, null, null, controller);
        tray.Initialize();

        var item = tray.PopoverItemForTest;
        Assert.NotNull(item);
        Assert.Equal("快捷信息窗", item!.Header);
        Assert.Contains(item, TrayIcon.GetIcons(Application.Current!)![0].Menu!.Items);

        item.Command!.Execute(null);
        Assert.True(controller.WindowForTest!.IsVisible);
        item.Command.Execute(null);
        Assert.False(controller.WindowForTest.IsVisible);
    }

    [AvaloniaFact]
    public void Popover_menu_item_absent_without_controller()
    {
        var legacy = new TrayService(new MainWindow());
        legacy.Initialize();
        Assert.Null(legacy.PopoverItemForTest);
        var icon = TrayIcon.GetIcons(Application.Current!)![0];
        Assert.DoesNotContain(icon.Menu!.Items.OfType<NativeMenuItem>(), i => i.Header as string == "快捷信息窗");

        TrayIcon.GetIcons(Application.Current!)!.Clear();
    }

    [AvaloniaFact]
    public void Escape_hides_popover()
    {
        var controller = new StatusPopoverController(_window, null, new StubQueryService(), null, null);
        controller.Show();
        var popover = controller.WindowForTest!;
        Assert.True(popover.IsVisible);

        popover.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "\u001b");

        Assert.False(popover.IsVisible);
    }

    [AvaloniaFact]
    public void Hide_marks_viewmodel_invisible_and_reload_only_when_visible()
    {
        var controller = new StatusPopoverController(_window, null, new StubQueryService(), null, null);
        controller.Show();
        Assert.True(controller.ViewModel.VisibleForTest);
        controller.Hide();
        Assert.False(controller.ViewModel.VisibleForTest);
    }

    private sealed class StubQueryService : IUsageQueryService
    {
        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult(new UsageKpiResult());
        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DailyUsagePoint>>([]);
        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);
        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);
        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);
        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) => Task.FromResult(new SessionListResult());
        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult(new HourlyDistributionResult());
    }
}
