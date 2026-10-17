using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.Tray;

/// <summary>
/// D-016 弹窗壳的运行时控制器：持有唯一的 <see cref="StatusPopoverWindow"/>（懒建、复用、只 Hide
/// 不 Close）与内容层 <see cref="StatusPopoverViewModel"/>，负责托盘左键 Toggle、双平台锚定与
/// 「弹层内点击 → 唤回主窗 + 既有导航」。壳层零业务逻辑零数据查询——数据全部在 D-015 内容层。
/// <para>线程约定：托盘 Clicked / 导航命令都在 UI 线程，无需 marshal。</para>
/// <para>锚定（卡内台阶设计）：Windows 用 GetCursorPos（物理像素，与 Screen.Bounds 同空间）光标
/// 上方；macOS 零 interop 主屏工作区右上近似——真机目检（TC-003）不可接受再按卡升级最小 interop；
/// 其余平台回落 TopRight。headless 无屏幕信息时跳过定位（装配/收起逻辑不受影响）。</para>
/// </summary>
public sealed class StatusPopoverController
{
    // 锚定间隙（DIP，经 screen.Scaling 换算进屏幕坐标系）
    private const double TopRightGapX = 8;
    private const double TopRightGapY = 6;
    private const double AboveCursorGap = 10;
    private const double AboveCursorRightOverlap = 14;

    private readonly Window _mainWindow;
    private readonly MainWindowViewModel? _shell;
    private readonly IUsageQueryService? _query;
    private readonly DashboardViewModel? _dashboard;
    private readonly DateRangeService? _dateService;

    private StatusPopoverWindow? _window;
    private StatusPopoverViewModel? _viewModel;

    public StatusPopoverController(
        Window mainWindow,
        MainWindowViewModel? shell,
        IUsageQueryService? query,
        DashboardViewModel? dashboard,
        DateRangeService? dateService)
    {
        _mainWindow = mainWindow;
        _shell = shell;
        _query = query;
        _dashboard = dashboard;
        _dateService = dateService;
    }

    /// <summary>内容层 VM（懒建；App 启动接线后由托盘点击首次触发；测试可经它直接装配）。</summary>
    public StatusPopoverViewModel ViewModel
    {
        get
        {
            if (_viewModel is null)
            {
                _viewModel = new StatusPopoverViewModel(
                    _query,
                    _dashboard,
                    _dateService,
                    navigateToPage: NavigateTo,
                    openAgentSessions: OpenAgentSessions,
                    openSettings: OpenSettings);
            }

            return _viewModel;
        }
    }

    /// <summary>托盘左键：弹层开着 → 收起；否则弹出（TrayService.Clicked 分派至此）。</summary>
    public void Toggle()
    {
        if (_window?.IsVisible == true)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    /// <summary>壳窗实例（headless 测试装配/收起断言用；正常运行无其他消费者）。</summary>
    internal StatusPopoverWindow? WindowForTest => _window;

    public void Show()
    {
        _window ??= new StatusPopoverWindow { DataContext = ViewModel };
        ViewModel.OnShown(); // 可见期才发查询（内容层约定）；速率/三态机不受可见性影响
        _window.Show();
        PositionWindow();
        _window.Activate();
    }

    public void Hide()
    {
        if (_window is { } window)
        {
            window.Hide();
            ViewModel.OnHidden();
        }
    }

    // ── 定位 ──

    private void PositionWindow()
    {
        var window = _window!;
        var screens = window.Screens;
        if (screens.All.Count == 0)
        {
            return; // headless：无屏幕信息，默认位置即可（不验证锚点）
        }

        if (OperatingSystem.IsWindows())
        {
            if (!TryGetCursorPos(out var nativePoint))
            {
                return;
            }

            var cursor = new PixelPoint(nativePoint.X, nativePoint.Y);
            var screen = screens.ScreenFromPoint(cursor) ?? screens.Primary ?? screens.All[0];
            window.Position = PopoverPositioner.AnchorAboveCursor(
                screen.WorkingArea,
                ScreenSize(window, screen),
                cursor,
                AboveCursorGap * screen.Scaling,
                AboveCursorRightOverlap * screen.Scaling);
        }
        else
        {
            // macOS（卡定零 interop 近似）与其余平台回落：主屏工作区右上
            var screen = screens.Primary ?? screens.All[0];
            window.Position = PopoverPositioner.AnchorTopRight(
                screen.WorkingArea,
                ScreenSize(window, screen),
                TopRightGapX * screen.Scaling,
                TopRightGapY * screen.Scaling);
        }
    }

    private static Size ScreenSize(Window window, Screen screen)
    {
        // Window.Bounds 是 DIP；屏幕坐标系（Windows 物理像素 / macOS Cocoa 点）= DIP × Scaling
        var size = window.Bounds.Size;
        return new Size(size.Width * screen.Scaling, size.Height * screen.Scaling);
    }

    // ── 弹层内点击 → 唤回主窗 + 既有导航（修正项 6：不新增路由）──

    private void NavigateTo(PageId page)
    {
        Hide();
        _shell?.GoToPageCommand.Execute(page);
        ShowMainWindow();
    }

    private void OpenAgentSessions(string agentId)
    {
        Hide();
        _shell?.OpenSessionsForAgent(agentId);
        ShowMainWindow();
    }

    private void OpenSettings()
    {
        Hide();
        _shell?.OpenSettingsCommand.Execute(null);
        ShowMainWindow();
    }

    /// <summary>唤回主窗（与 TrayService.ShowWindow 同语义：Show + 脱最小化 + Activate）。</summary>
    private void ShowMainWindow()
    {
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static bool TryGetCursorPos(out NativePoint point) => GetCursorPos(out point);
}
