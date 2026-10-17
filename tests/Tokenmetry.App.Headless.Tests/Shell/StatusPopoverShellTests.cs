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
/// Esc 收起与失焦收起（Deactivated → Hide 在窗口构造接线，headless 直发按键验证 Esc 路径）；
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

        var icons = TrayIcon.GetIcons(Application.Current!);
        foreach (var i in icons!.ToList())
        {
            icons.Remove(i);
        }
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
