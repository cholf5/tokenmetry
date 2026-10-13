using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using CCUsage.App.Services;
using CCUsage.App.ViewModels;

namespace CCUsage.App.Tray;

/// <summary>
/// A-0 托盘常驻（v0.5 规格 docs/plans/2026-10-11-opinionated-usage-stats-pivot.md §5）：
/// 图标常驻，tooltip 与菜单首行展示今日成本；菜单 = 打开主窗 / 立即刷新 / 设置 / 退出。
/// <para>挂载唯一路径是 <see cref="TrayIcon.SetIcons"/>（Application 的 Icons 附加属性变更才触发
/// 平台 Attach，仅 new TrayIcon{} 不会出现在托盘）。关窗隐藏（不退出）的拦截在
/// MainWindow.OnClosing；真正退出走 <see cref="ExitApp"/> → desktop.Shutdown()，
/// 经生命周期正常收尾（App 订阅 desktop.Exit 停后台刷新定时器），不使用 Environment.Exit。</para>
/// <para>依赖为可选注入：App 启动传齐 dashboard/shell/refreshService；headless 测试等场景
/// 可仅传窗口（托盘仍注册，刷新/导航/成本联动按 null 跳过）。</para>
/// </summary>
public class TrayService
{
    private readonly Window _mainWindow;
    private readonly DashboardViewModel? _dashboard;
    private readonly MainWindowViewModel? _shell;
    private readonly BackgroundRefreshService? _refreshService;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _costItem;

    public TrayService(Window mainWindow) : this(mainWindow, null, null, null)
    {
    }

    public TrayService(Window mainWindow, DashboardViewModel? dashboard, MainWindowViewModel? shell, BackgroundRefreshService? refreshService)
    {
        _mainWindow = mainWindow;
        _dashboard = dashboard;
        _shell = shell;
        _refreshService = refreshService;
    }

    public void Initialize()
    {
        // 菜单首行的成本展示与 tooltip 同源（macOS 状态项无 hover tooltip，此处是 macOS 上
        // 唯一可见的成本出口；规格要求常驻展示今日成本）
        _costItem = new NativeMenuItem { Header = "今日成本：—", IsEnabled = false };

        _trayIcon = new TrayIcon
        {
            IsVisible = true,
            ToolTipText = "ccusage — Token 成本监控",
            Icon = CreateIcon(),
            Menu = new NativeMenu
            {
                Items =
                {
                    _costItem,
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem { Header = "打开主窗", Command = new RelayCommand(ShowWindow) },
                    new NativeMenuItem { Header = "立即刷新", Command = new RelayCommand(RefreshNow) },
                    new NativeMenuItem { Header = "设置", Command = new RelayCommand(OpenSettings) },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem { Header = "退出", Command = new RelayCommand(ExitApp) },
                }
            }
        };
        _trayIcon.Clicked += (_, _) => ShowWindow();

        if (OperatingSystem.IsMacOS())
        {
            // macOS 状态栏模板图标：单色资产，跟随明暗菜单栏自动反色
            MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
        }

        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _trayIcon });

        // 今日成本联动：KPI 更新（含首轮刷新）即回写 tooltip 与菜单首行
        if (_dashboard != null)
        {
            _dashboard.PropertyChanged += OnDashboardPropertyChanged;
            UpdateToolTip(BuildCostText(_dashboard.TodayCostUsd, _dashboard.HasData));
        }
    }

    public void UpdateToolTip(string tooltip)
    {
        if (_trayIcon != null)
        {
            _trayIcon.ToolTipText = tooltip;
        }

        if (_costItem != null)
        {
            _costItem.Header = tooltip;
        }
    }

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.TodayCostUsd) or nameof(DashboardViewModel.HasData))
        {
            UpdateToolTip(BuildCostText(_dashboard!.TodayCostUsd, _dashboard.HasData));
        }
    }

    private static string BuildCostText(decimal todayCostUsd, bool hasData)
        => hasData
            ? $"今日成本：${todayCostUsd.ToString("F2", CultureInfo.InvariantCulture)}"
            : "今日成本：—（尚无数据）";

    private void ShowWindow()
    {
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void RefreshNow()
    {
        // 与启动首刷同一 fire-and-forget 语义（TriggerRefreshAsync 内部吞异常返回成败，不观察结果）
        _ = _refreshService?.TriggerRefreshAsync();
    }

    private void OpenSettings()
    {
        _shell?.OpenSettingsCommand.Execute(null);
        ShowWindow();
    }

    private void ExitApp()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 强制路径（ignoreCancel）：绕过 MainWindow 的"关窗即隐藏"拦截真正退出，
            // 同时走完生命周期收尾（App 在 desktop.Exit 停后台刷新定时器）
            desktop.Shutdown();
        }
    }

    private static WindowIcon CreateIcon()
    {
        var asset = OperatingSystem.IsMacOS() ? "Assets/tray-icon-template.png" : "Assets/tray-icon.png";
        using var stream = AssetLoader.Open(new Uri($"avares://CCUsage.App/{asset}"));
        return new WindowIcon(stream);
    }
}
