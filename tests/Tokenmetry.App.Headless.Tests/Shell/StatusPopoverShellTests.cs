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
using Avalonia.Threading;
using Tokenmetry.App.Tray;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
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
/// PopoverPositioner 纯函数：TopRight / AboveCursor 锚定与工作区钳制；
/// B-021 首显偏下回归锁：内容异步回填撑高窗口时按点击光标重锚，底缘不越过任务栏。
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

    [AvaloniaFact]
    public void First_show_reanchors_to_click_point_when_async_content_grows()
    {
        // B-021 回归锁：首显 Show() 返回时内容仍是空态（无数据行），异步回填后 SizeToContent
        // 把窗口撑高——修复前位置只在 Show 后算一次，窗口向下长过任务栏（用户"首显偏下、
        // 二次正常」）。修法把点击光标记为本次显示首锚，SizeChanged 时重算位置（底缘不动）。
        if (!OperatingSystem.IsWindows())
        {
            return; // 底缘锚点只在 Windows 分支；macOS / 回落的顶缘锚定天然不受高度增长影响
        }

        var main = new MainWindow();
        var area = main.Screens.Primary!.WorkingArea;
        var anchor = new PixelPoint(area.X + area.Width - 30, area.Y + area.Height - 20);
        var stub = new GatedQueryService();
        var controller = new StatusPopoverController(main, null, stub, null, new DateRangeService())
        {
            CursorAnchorOverrideForTest = anchor, // headless 真实光标不可控：注入托盘点击点
        };

        controller.Show();
        var popover = controller.WindowForTest!;
        var scaling = popover.Screens.ScreenFromPoint(anchor)!.Scaling;
        var initialPosition = popover.Position;
        var initialHeight = popover.ClientSize.Height;

        stub.Release();
        // 数据回填 → 属性通知 → 布局 → SizeToContent 尺寸回写分属多个 dispatcher 轮次，
        // 且可能有多轮中间尺寸（KPI → Agent 行 → 模型行）：泵到队列排空再读终态
        for (var i = 0; i < 50; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        Assert.NotNull(controller.ViewModel.TopAgents);
        var grownHeight = popover.ClientSize.Height;
        Assert.True(grownHeight > initialHeight, "前提失败：异步回填应把 SizeToContent 窗口撑高");

        // 底缘恒定 = 点击光标上方 AboveCursorGap（10 DIP），两次都不得漂移
        var expectedBottom = anchor.Y - 10 * scaling;
        var grownBottom = popover.Position.Y + grownHeight * scaling;
        var initialBottom = initialPosition.Y + initialHeight * scaling;
        Assert.True(
            Math.Abs(grownBottom - expectedBottom) <= 1 && Math.Abs(initialBottom - expectedBottom) <= 1,
            $"anchor={anchor} scaling={scaling} initialHeight={initialHeight} grownHeight={grownHeight} " +
            $"initialBottom={initialBottom} grownBottom={grownBottom} expectedBottom={expectedBottom}");

        main.Close();
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

    /// <summary>首显异步回填的 stub：查询全部挂起到 <see cref="Release"/>——复刻真实链路
    /// （Show 返回时内容为空态，数据到达后窗口才被 SizeToContent 撑高）。</summary>
    private sealed class GatedQueryService : IUsageQueryService
    {
        private readonly TaskCompletionSource _gate = new();

        public void Release() => _gate.SetResult();

        public async Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            await _gate.Task;
            return new UsageKpiResult { TotalTokens = 42381, Cost = new AggregatedCost(12.46m, false), CacheReadShare = 0.68m };
        }

        public async Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            await _gate.Task;
            return
            [
                new DailyUsagePoint
                {
                    Day = "2026-10-17",
                    InputTokens = 26843,
                    OutputTokens = 13124,
                    CacheReadTokens = 2414,
                    TotalTokens = 42381,
                    HasData = true,
                },
            ];
        }

        public async Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            await _gate.Task;
            return
            [
                new DimensionUsageRow
                {
                    Key = "Claude Code",
                    TotalTokens = 42381,
                    TokenShare = 1m,
                    Cost = new AggregatedCost(12.46m, false),
                },
            ];
        }

        public async Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            await _gate.Task;
            return
            [
                new DimensionUsageRow
                {
                    Key = "claude-sonnet-4-5",
                    TotalTokens = 42381,
                    Cost = new AggregatedCost(12.46m, false),
                    CostShare = 1m,
                },
            ];
        }

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);
        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) => Task.FromResult(new SessionListResult());
        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult(new HourlyDistributionResult());
    }
}
