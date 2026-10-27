using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.Tray;

/// <summary>
/// D-016 弹窗壳的运行时控制器：持有唯一的 <see cref="StatusPopoverWindow"/>（懒建、复用、只 Hide
/// 不 Close）与内容层 <see cref="StatusPopoverViewModel"/>，负责托盘左键 Toggle、双平台锚定与
/// 「弹层内点击 → 唤回主窗 + 既有导航」。壳层零业务逻辑零数据查询——数据全部在 D-015 内容层。
/// <para>失焦收起接线在控制器（B-017 从窗口构造移入）：<see cref="StatusPopoverWindow"/> 的
/// Deactivated → 控制器 Hide（补上 VM.OnHidden，与 Esc / 再点收起同语义）+ 失焦时刻记账，
/// 供 <see cref="ToggleFromTrayClick"/> 识别"刚被本次托盘点击的失焦收掉"（Win32 先失焦后 Clicked 的时序）。</para>
/// <para>线程约定：托盘 Clicked / 导航命令都在 UI 线程，无需 marshal。</para>
/// <para>锚定（卡内台阶设计）：Windows 用 GetCursorPos（物理像素，与 Screen.Bounds 同空间）光标
/// 上方；macOS 零 interop 主屏工作区右上近似——真机目检（TC-003）不可接受再按卡升级最小 interop；
/// 其余平台回落 TopRight。宿主不提供屏幕信息（screens.All 为空）时跳过定位（装配/收起逻辑不受影响）。</para>
/// <para>尺寸跟随（B-021）：<c>SizeToContent="Height"</c> 窗口在显示后仍会因数据异步回填 / banner
/// 显隐改变高度，且只向下生长。位置只在 Show 后算一次会让底缘越过任务栏（首显尤甚：空态 420 DIP
/// → 回填后 719 DIP）；故 Show 时捕获本次点击的光标锚点，<see cref="StatusPopoverWindow.SizeChanged"/>
/// 时按同一锚点重算——Windows 底缘钉在光标上方，macOS/回落顶缘锚定天然幂等。</para>
/// </summary>
public sealed class StatusPopoverController
{
    // 锚定间隙（DIP，经 screen.Scaling 换算进屏幕坐标系）
    private const double TopRightGapX = 8;
    private const double TopRightGapY = 6;
    private const double AboveCursorGap = 10;
    private const double AboveCursorRightOverlap = 14;
    private const double BelowIconGap = 4;

    /// <summary>失焦收起 → 托盘左键 Toggle 的守卫窗口（B-017）：Win32 点托盘时系统在按下瞬间把
    /// 焦点交给任务栏——弹窗 Deactivated → 失焦收起先发生，TrayIcon.Clicked（WM_LBUTTONUP 抬起
    /// 才到）随后才到，ToggleFromTrayClick 看到的已是"关着"，照常弹出左键就永远只会 Open。
    /// 该窗口期内的 ToggleFromTrayClick 视为同一次点击的收起意图：维持隐藏。取值覆盖按下到抬起
    /// 的按压时长（p99 ≈ 250ms）；代价是点走焦点后 400ms 内点托盘会被吞一次，守卫即行失效
    /// （消费制），再点即开。守卫仅作用于托盘左键 ToggleFromTrayClick。</summary>
    private static readonly TimeSpan FocusLossToggleGuard = TimeSpan.FromMilliseconds(400);

    private readonly Window _mainWindow;
    private readonly MainWindowViewModel? _shell;
    private readonly IUsageQueryService? _query;
    private readonly DashboardViewModel? _dashboard;
    private readonly DateRangeService? _dateService;
    private readonly ModelAvatarResolver? _modelAvatars;

    private StatusPopoverWindow? _window;
    private StatusPopoverViewModel? _viewModel;
    private DateTimeOffset _lastFocusLostUtc = DateTimeOffset.MinValue;

    /// <summary>本次显示周期的 Windows 锚定光标点（点托盘那一刻捕获，重锚复用）：用户随后把鼠标
    /// 移开再遇到内容高度变化时，仍回到点击位置，而不是跟着当前光标跑。</summary>
    private PixelPoint? _anchorCursor;

    public StatusPopoverController(
        Window mainWindow,
        MainWindowViewModel? shell,
        IUsageQueryService? query,
        DashboardViewModel? dashboard,
        DateRangeService? dateService,
        ModelAvatarResolver? modelAvatars = null)
    {
        _mainWindow = mainWindow;
        _shell = shell;
        _query = query;
        _dashboard = dashboard;
        _dateService = dateService;
        _modelAvatars = modelAvatars;
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
                    openSettings: OpenSettings,
                    modelAvatars: _modelAvatars);
            }

            return _viewModel;
        }
    }

    /// <summary>托盘左键 Toggle（Win32/Linux Avalonia Clicked 与 macOS interop 左键共用）：
    /// 弹层开着 → 收起；关着 → 弹出，但前置失焦守卫（B-017）——刚失焦收起说明本次点击的收起
    /// 意图已由失焦通道执行，维持隐藏；守卫消费一次即失效，快速连点可再开。</summary>
    public void ToggleFromTrayClick()
    {
        if (_window?.IsVisible == true)
        {
            Hide();
        }
        else if (DateTimeOffset.UtcNow - _lastFocusLostUtc < FocusLossToggleGuard)
        {
            _lastFocusLostUtc = DateTimeOffset.MinValue;
        }
        else
        {
            Show();
        }
    }

    /// <summary>壳窗实例（headless 测试装配/收起断言用；正常运行无其他消费者）。</summary>
    internal StatusPopoverWindow? WindowForTest => _window;

    /// <summary>锚定光标测试注入：headless 测试无法控制真实光标，注入确定点后可断言「内容变高
    /// 时底缘不动」。默认 null 走真实 GetCursorPos。</summary>
    internal PixelPoint? CursorAnchorOverrideForTest { get; set; }

    public void Show()
    {
        if (_window is null)
        {
            var window = new StatusPopoverWindow { DataContext = ViewModel };
            // 失焦收起接线（B-017 从窗口构造移入）：走控制器 Hide 补 VM.OnHidden；守卫记账见下
            window.Deactivated += OnShellWindowDeactivated;
            // 高度跟随（B-021）：数据异步回填 / banner 显隐会让 SizeToContent 窗口继续向下长；
            // 显示期尺寸一变就按首锚重算，Windows 底缘才不会越长越压过任务栏。
            window.SizeChanged += OnShellWindowSizeChanged;
            _window = window;
        }

        _anchorCursor = CaptureCursorAnchor(); // 本次点击的光标（非 Windows / 取不到 → null）
        ViewModel.OnShown(); // 可见期才发查询（内容层约定）；速率/三态机不受可见性影响
        _window.Show();
        PositionWindow();
        _window.Activate();
    }

    /// <summary>弹窗失焦：外部抢走焦点（点任务栏 / 别的应用）→ 失焦收起 + 记账供 Toggle 守卫。
    /// 自身 Hide 引发的 Deactivated 到达时窗口已不可见，不算失焦收起（否则每次 Toggle 收起都给
    /// 下一次点击上锁）。internal 供 headless 测试模拟 Win32"先失焦后 Clicked"时序。</summary>
    internal void OnShellWindowDeactivated(object? sender, EventArgs e)
    {
        if (_window?.IsVisible != true)
        {
            return;
        }

        _lastFocusLostUtc = DateTimeOffset.UtcNow;
        Hide();
    }

    public void Hide()
    {
        if (_window is { } window)
        {
            window.Hide();
            ViewModel.OnHidden();
        }
    }

    /// <summary>显示期尺寸变化（SizeToContent 内容高度）→ 按本次显示的首锚重算位置：Windows 底缘
    /// 钉在托盘光标上方，窗口向上长；macOS / 回落是顶缘锚定，重算等价于不动。隐藏期不处理——下次
    /// Show 会用新光标 + 当时尺寸从头定位。</summary>
    private void OnShellWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_window?.IsVisible == true)
        {
            PositionWindow();
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
            if (_anchorCursor is not { } cursor)
            {
                return; // 取不到点击光标：保持平台默认位置（同旧行为）
            }

            var screen = screens.ScreenFromPoint(cursor) ?? screens.Primary ?? screens.All[0];
            window.Position = PopoverPositioner.AnchorAboveCursor(
                screen.WorkingArea,
                ScreenSize(window, screen),
                cursor,
                AboveCursorGap * screen.Scaling,
                AboveCursorRightOverlap * screen.Scaling);
        }
        else if (OperatingSystem.IsMacOS() && MacTrayInterop.TryGetStatusItemFrame(out var anchor))
        {
            // 精确锚定（interop 图标 frame，B4 右弹优先：左缘贴图标左缘、向右下展开，右侧放不下
            // 整体左移）。frame 与屏幕坐标同单位（macOS Scaling=1，Cocoa 点）；按可见卡片计算后内移投影
            // 边距（毛玻璃形态边距为零——窗口已贴合卡片，B6）。
            var screen = ScreenContaining(screens, anchor) ?? screens.Primary ?? screens.All[0];
            var windowSize = ScreenSize(window, screen);
            var margins = window.EffectiveShadowMargins * screen.Scaling;
            var cardSize = new Size(
                Math.Max(1, windowSize.Width - margins.Left - margins.Right),
                Math.Max(1, windowSize.Height - margins.Top - margins.Bottom));
            var cardPos = PopoverPositioner.AnchorBelow(anchor, cardSize, screen.WorkingArea, BelowIconGap * screen.Scaling);
            // macOS 的 AppKit constrainFrameRect 会把普通窗口压到菜单栏之下（实测设 y=14 被改写为
            // 30=栏底）——窗口顶最终 ≥ workArea.Y；可见卡片顶 = 窗口顶 + ShadowMargins.Top（6），
            // 即"贴着菜单栏下方 6px 弹出"（CodexBar 观感）。锚定钳制已保证 x；y 交给约束兜底。
            window.Position = new PixelPoint(
                cardPos.X - (int)Math.Round(margins.Left),
                cardPos.Y - (int)Math.Round(margins.Top));
        }
        else
        {
            // 近似回落（非 mac / interop 未装 / frame 不可得）：主屏工作区右上
            var screen = screens.Primary ?? screens.All[0];
            window.Position = PopoverPositioner.AnchorTopRight(
                screen.WorkingArea,
                ScreenSize(window, screen),
                TopRightGapX * screen.Scaling,
                TopRightGapY * screen.Scaling);
        }
    }

    private static Screen? ScreenContaining(Screens screens, PixelRect rect)
    {
        var center = new PixelPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        return screens.ScreenFromPoint(center);
    }

    private static Size ScreenSize(Window window, Screen screen)
    {
        // ClientSize 是 DIP；屏幕坐标系（Windows 物理像素 / macOS Cocoa 点）= DIP × Scaling。
        // 不用 window.Bounds：首次 Show 后 Bounds 高度仍是平台默认值（CW_USEDEFAULT ≈ 720 DIP，
        // SizeToContent 实测高度要等首个布局 tick 才回写 Bounds），用它锚定会把弹窗抬高数百 px
        // ——Windows 首显「弹在屏幕中间偏右」的根因；ClientSize 在 Show() 返回时即已正确。
        // 显示后内容高度仍会变（数据回填/banner），每次调用取当次 ClientSize，由 SizeChanged 触发重算。
        var size = window.ClientSize;
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

    /// <summary>本次显示的光标锚点：测试注入优先；Windows 取真实光标；其余平台无（用各自锚点）。</summary>
    private PixelPoint? CaptureCursorAnchor()
    {
        if (CursorAnchorOverrideForTest is { } overridden)
        {
            return overridden;
        }

        return OperatingSystem.IsWindows() && GetCursorPos(out var point)
            ? new PixelPoint(point.X, point.Y)
            : null;
    }
}
