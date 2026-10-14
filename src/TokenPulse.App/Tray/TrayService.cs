using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using TokenPulse.App.Localization;
using TokenPulse.App.Services;
using TokenPulse.App.ViewModels;

namespace TokenPulse.App.Tray;

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
    private NativeMenuItem? _openItem;
    private NativeMenuItem? _refreshItem;
    private NativeMenuItem? _settingsItem;
    private NativeMenuItem? _exitItem;

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
        // 托盘为应用级常驻：订阅语言切换信号重译菜单（NativeMenuItem.Header 不支持绑定，
        // 只能命令式改写；成本首行由 UpdateToolTip/BuildCostText 实时取串无需在此处理）。
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;

        // 菜单实例只建一份：macOS 的 NativeMenu 导出器缓存首个挂载实例，运行中替换整个
        // _trayIcon.Menu 会抛 ArgumentException("The menu being updated does not match.")，
        // 且该异常沿 PropertyChanged 同步链向上打断语言切换（实测）——语言切换只就地改 Header。
        _costItem = new NativeMenuItem { Header = Translations.Instance.TrayCostNoData, IsEnabled = false };
        _openItem = new NativeMenuItem { Header = Translations.Instance.TrayOpenWindow, Command = new RelayCommand(ShowWindow) };
        _refreshItem = new NativeMenuItem { Header = Translations.Instance.TrayRefreshNow, Command = new RelayCommand(RefreshNow) };
        _settingsItem = new NativeMenuItem { Header = Translations.Instance.TraySettings, Command = new RelayCommand(OpenSettings) };
        _exitItem = new NativeMenuItem { Header = Translations.Instance.TrayExit, Command = new RelayCommand(ExitApp) };

        _trayIcon = new TrayIcon
        {
            IsVisible = true,
            ToolTipText = BuildTooltip(),
            Icon = CreateIcon(),
            Menu = new NativeMenu
            {
                Items =
                {
                    _costItem,
                    new NativeMenuItemSeparator(),
                    _openItem,
                    _refreshItem,
                    _settingsItem,
                    new NativeMenuItemSeparator(),
                    _exitItem,
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

    private string BuildTooltip() => Translations.Instance.TrayTooltip;

    /// <summary>语言切换重译：就地更新各菜单项 Header + tooltip（成本首行仍按当前数据态取串）。
    /// <b>不得替换 NativeMenu 实例</b>——macOS 导出器缓存首个挂载实例（见 Initialize 注释）；
    /// Header 就地改写能同步到原生菜单（成本首行的运行时回写一直如此）。</summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Translations.CurrentLanguage) || _trayIcon is null)
        {
            return;
        }

        if (_openItem is { } open)
        {
            open.Header = Translations.Instance.TrayOpenWindow;
        }

        if (_refreshItem is { } refresh)
        {
            refresh.Header = Translations.Instance.TrayRefreshNow;
        }

        if (_settingsItem is { } settings)
        {
            settings.Header = Translations.Instance.TraySettings;
        }

        if (_exitItem is { } exit)
        {
            exit.Header = Translations.Instance.TrayExit;
        }

        if (_dashboard != null)
        {
            UpdateToolTip(BuildCostText(_dashboard.TodayCostUsd, _dashboard.HasData));
        }
        else
        {
            UpdateToolTip(BuildTooltip());
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
            ? string.Format(Translations.Instance.TrayCostFormat, todayCostUsd.ToString("F2", CultureInfo.InvariantCulture))
            : Translations.Instance.TrayCostNoData;

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
        using var stream = AssetLoader.Open(new Uri($"avares://TokenPulse.App/{asset}"));
        return new WindowIcon(stream);
    }
}
