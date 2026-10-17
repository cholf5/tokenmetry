using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Tray;

/// <summary>
/// A-0 托盘常驻（v0.5 规格 docs/plans/2026-10-11-opinionated-usage-stats-pivot.md §5）：
/// 图标常驻，tooltip 与菜单首行展示今日成本；菜单 = 今日成本 + 快捷信息窗 + 打开主窗 / 立即刷新 / 设置 / 退出。
/// <para><b>双轨（D-016 升级台阶，用户拍板"点图标直接弹层"后实施）</b>：macOS 生产路径经
/// <see cref="MacTrayInterop"/> 自管 NSStatusItem——左键 Toggle 弹层、右键弹原生菜单（Avalonia 的
/// TrayIcon.Clicked 在 macOS 从不 raise，官方文档 + trayicon.mm 留证）；安装失败自动回落 Avalonia
/// 托盘路径（点图标开菜单 → 「快捷信息窗」项进弹层）。Windows 与 headless 测试宿主走 Avalonia
/// 托盘：Win32 左键 WM_LBUTTONUP → Clicked → Toggle，右键 NativeMenu。</para>
/// <para>菜单实例纪律（Avalonia 路径）：macOS 的 NativeMenu 导出器缓存首个挂载实例，运行中替换
/// 整个 _trayIcon.Menu 会抛 ArgumentException——语言切换只就地改 Header（interop 路径同理，
/// NSMenuItem 就地 setTitle）。</para>
/// <para>挂载唯一路径是 <see cref="TrayIcon.SetIcons"/>（仅 Avalonia 路径）。关窗隐藏（不退出）的
/// 拦截在 MainWindow.OnClosing；真正退出走 <see cref="ExitApp"/> → desktop.Shutdown()，经生命周期
/// 正常收尾（App 订阅 desktop.Exit 停后台刷新定时器），不使用 Environment.Exit。</para>
/// <para>依赖为可选注入：App 启动传齐 dashboard/shell/refreshService/popover；headless 测试等场景
/// 可仅传窗口（托盘仍注册，刷新/导航/成本联动/弹层按 null 跳过）。</para>
/// </summary>
public class TrayService
{
    /// <summary>interop 菜单里「今日成本」标签行的序号（BuildMacTrayItems 恒为首项）。</summary>
    private const int MacCostItemIndex = 0;

    private readonly Window _mainWindow;
    private readonly DashboardViewModel? _dashboard;
    private readonly MainWindowViewModel? _shell;
    private readonly BackgroundRefreshService? _refreshService;
    private readonly StatusPopoverController? _popover;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _costItem;
    private NativeMenuItem? _popoverItem;
    private NativeMenuItem? _openItem;
    private NativeMenuItem? _refreshItem;
    private NativeMenuItem? _settingsItem;
    private NativeMenuItem? _exitItem;
    private bool _macInterop;
    private List<MacTrayInterop.MacTrayItem>? _macItems;

    public TrayService(Window mainWindow) : this(mainWindow, null, null, null)
    {
    }

    public TrayService(Window mainWindow, DashboardViewModel? dashboard, MainWindowViewModel? shell, BackgroundRefreshService? refreshService, StatusPopoverController? popover = null)
    {
        _mainWindow = mainWindow;
        _dashboard = dashboard;
        _shell = shell;
        _refreshService = refreshService;
        _popover = popover;
    }

    public void Initialize()
    {
        // 托盘为应用级常驻：订阅语言切换信号重译菜单（NativeMenuItem.Header / NSMenuItem.title
        // 都不支持绑定，只能命令式改写；成本首行由 UpdateToolTip/BuildCostText 实时取串）。
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;

        if (!TryInstallMacInteropTray())
        {
            InstallAvaloniaTrayIcon();
        }

        // 今日成本联动：KPI 更新（含首轮刷新）即回写 tooltip 与菜单首行（两路径共用）
        if (_dashboard != null)
        {
            _dashboard.PropertyChanged += OnDashboardPropertyChanged;
            UpdateToolTip(BuildCostText(_dashboard.TodayCostUsd, _dashboard.HasData));
        }
    }

    /// <summary>macOS 生产路径：自管 NSStatusItem（点图标直接弹层）。仅真实桌面环境启用
    /// （headless 测试宿主打 TOKENMETRY_HEADLESS=1 标记走 Avalonia 托盘）；安装失败回落 false。</summary>
    private bool TryInstallMacInteropTray()
    {
        if (!OperatingSystem.IsMacOS() || IsHeadlessTestHost)
        {
            return false;
        }

        var items = BuildMacTrayItems();
        var iconBytes = LoadAssetBytes("Assets/tray-icon-template.png");
        if (!MacTrayInterop.TryInstall(iconBytes, Translations.Instance.TrayTooltip, items, TogglePopoverOrShowWindow))
        {
            return false;
        }

        _macInterop = true;
        _macItems = items;
        return true;
    }

    /// <summary>弹层 Toggle；未装配弹层时维持 A-0 直开主窗（与 OnTrayClicked 同语义）。</summary>
    private void TogglePopoverOrShowWindow()
    {
        if (_popover is { } popover)
        {
            popover.Toggle();
        }
        else
        {
            ShowWindow();
        }
    }

    private List<MacTrayInterop.MacTrayItem> BuildMacTrayItems()
    {
        // 标题全部用 Func 活取串：语言切换 / 数据回写时由 OnTranslationsPropertyChanged / UpdateToolTip 回写
        var items = new List<MacTrayInterop.MacTrayItem>
        {
            new(
                () => BuildCostText(_dashboard?.TodayCostUsd ?? 0, _dashboard?.HasData ?? false),
                command: null,
                enabled: false),
            new(null!, isSeparator: true),
        };
        if (_popover is { } popover)
        {
            items.Add(new(() => Translations.Instance.TrayQuickInfo, () => popover.Toggle()));
        }

        items.Add(new(() => Translations.Instance.TrayOpenWindow, ShowWindow));
        items.Add(new(() => Translations.Instance.TrayRefreshNow, RefreshNow));
        items.Add(new(() => Translations.Instance.TraySettings, OpenSettings));
        items.Add(new(null!, isSeparator: true));
        items.Add(new(() => Translations.Instance.TrayExit, ExitApp));
        return items;
    }

    private void InstallAvaloniaTrayIcon()
    {
        // 菜单实例只建一份：macOS 的 NativeMenu 导出器缓存首个挂载实例，运行中替换整个
        // _trayIcon.Menu 会抛 ArgumentException("The menu being updated does not match.")，
        // 且该异常沿 PropertyChanged 同步链向上打断语言切换（实测）——语言切换只就地改 Header。
        _costItem = new NativeMenuItem { Header = Translations.Instance.TrayCostNoData, IsEnabled = false };
        _openItem = new NativeMenuItem { Header = Translations.Instance.TrayOpenWindow, Command = new RelayCommand(ShowWindow) };
        _refreshItem = new NativeMenuItem { Header = Translations.Instance.TrayRefreshNow, Command = new RelayCommand(RefreshNow) };
        _settingsItem = new NativeMenuItem { Header = Translations.Instance.TraySettings, Command = new RelayCommand(OpenSettings) };
        _exitItem = new NativeMenuItem { Header = Translations.Instance.TrayExit, Command = new RelayCommand(ExitApp) };

        // D-016 快捷信息窗菜单项：macOS 上 TrayIcon.Clicked 不 raise（官方契约 + trayicon.mm 无点击
        // 回调，任何点击都打开 NativeMenu）——interop 安装失败回落本路径时，弹层经本菜单项进入；
        // Windows 左键已直弹弹层，此项为冗余但一致的入口。仅装配了弹层控制器时出现。
        if (_popover is { } popover)
        {
            _popoverItem = new NativeMenuItem { Header = Translations.Instance.TrayQuickInfo, Command = new RelayCommand(popover.Toggle) };
        }

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
        if (_popoverItem is { } popoverItem)
        {
            _trayIcon.Menu!.Items.Insert(2, popoverItem); // 成本行 + 分隔线之后，首区第三位
        }

        _trayIcon.Clicked += (_, _) => OnTrayClicked();

        if (OperatingSystem.IsMacOS())
        {
            // macOS 状态栏模板图标：单色资产，跟随明暗菜单栏自动反色
            MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
        }

        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _trayIcon });
    }

    /// <summary>headless 测试宿主标记（SemiTestAppBuilder 设置）：跳过 macOS interop 托盘，
    /// 保持 Avalonia TrayIcon 路径供测试断言；不设标记的真实桌面环境才走 interop。</summary>
    private static bool IsHeadlessTestHost =>
        Environment.GetEnvironmentVariable("TOKENMETRY_HEADLESS") == "1";

    private static byte[] LoadAssetBytes(string asset)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Tokenmetry.App/{asset}"));
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private string BuildTooltip() => Translations.Instance.TrayTooltip;

    /// <summary>语言切换重译：就地更新各菜单项标题 + tooltip（成本首行仍按当前数据态取串）。
    /// <b>不得替换 NativeMenu 实例</b>——macOS 导出器缓存首个挂载实例（见 InstallAvaloniaTrayIcon 注释）；
    /// Header 就地改写能同步到原生菜单（成本首行的运行时回写一直如此）。interop 路径同理改 NSMenuItem.title。</summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Translations.CurrentLanguage))
        {
            return;
        }

        if (_macInterop)
        {
            RunOnUIThread(() =>
            {
                MacTrayInterop.SetTooltip(BuildTooltip());
                if (_macItems is { } items)
                {
                    for (var index = 0; index < items.Count; index++)
                    {
                        MacTrayInterop.SetItemTitle(index, items[index].Title());
                    }
                }
            });
            return;
        }

        if (_trayIcon is null)
        {
            return;
        }

        if (_popoverItem is { } popover)
        {
            popover.Header = Translations.Instance.TrayQuickInfo;
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
        if (_macInterop)
        {
            // DashboardViewModel 回写可能来自线程池线程——Cocoa 调用必须主线程
            RunOnUIThread(() =>
            {
                MacTrayInterop.SetItemTitle(MacCostItemIndex, tooltip);
                MacTrayInterop.SetTooltip(BuildTooltip()); // interop 后 tooltip 真实可渲染，恢复产品语义
            });
            return;
        }

        if (_trayIcon != null)
        {
            _trayIcon.ToolTipText = tooltip;
        }

        if (_costItem != null)
        {
            _costItem.Header = tooltip;
        }
    }

    /// <summary>「快捷信息窗」菜单项（headless 测试断言与命令触发用；未装配弹层时为 null）。</summary>
    internal NativeMenuItem? PopoverItemForTest => _popoverItem;

    /// <summary>托盘左键分派（Avalonia 路径；macOS interop 路径左键直进 <see cref="TogglePopoverOrShowWindow"/>）：
    /// 有弹层控制器 → Toggle 弹窗（开着收起 / 关着弹出，D-015 内容层承载）；未装配（headless 旧构造 /
    /// 测试）维持 A-0 直开主窗。右键 NativeMenu 不动。
    /// <b>平台事实（实施留证）</b>：本事件仅 Win32/部分 Linux DE 会 raise——macOS 上 Avalonia 从不
    /// raise（TrayIcon.cs 文档原文 "on OSX this event is not raised"，trayicon.mm 亦无点击回调），
    /// macOS 生产左键走 MacTrayInterop 自管状态项。</summary>
    internal void OnTrayClicked() => TogglePopoverOrShowWindow();

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.TodayCostUsd) or nameof(DashboardViewModel.HasData))
        {
            UpdateToolTip(BuildCostText(_dashboard!.TodayCostUsd, _dashboard.HasData));
        }
    }

    /// <summary>今日成本文案（菜单首行；调用方保证不变量已检查）。</summary>
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
        using var stream = AssetLoader.Open(new Uri($"avares://Tokenmetry.App/{asset}"));
        return new WindowIcon(stream);
    }

    private static void RunOnUIThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
