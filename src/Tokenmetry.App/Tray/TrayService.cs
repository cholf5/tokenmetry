using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.Update;
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
    private NativeMenuItem? _updateItem;
    private NativeMenuItem? _exitItem;
    private UpdateService? _updateService;
    private bool _macInterop;
    private int _macUpdateItemIndex = -1;
    private List<MacTrayInterop.MacTrayItem>? _macItems;
    private DateTimeOffset? _lastTrayClickedUtc;

    /// <summary>双击判定是否启用（docs/plans/2026-10-26-tray-double-click-design.md §2 设计裁决：
    /// 仅 Windows——macOS 菜单栏无双击习惯且 interop 左键不经过 <see cref="OnTrayClicked"/>，
    /// Linux 各 DE 的 Clicked 触发行为不一）。internal 可覆写：headless 用例需在任意宿主平台验证。</summary>
    internal bool TrayDoubleClickEnabled { get; set; } = OperatingSystem.IsWindows();

    /// <summary>双击判定窗口（两次 Clicked 的最大间隔）。默认取系统双击时长（尊重用户设置，
    /// Win32 默认 500ms）；非 Windows 为占位默认（门控关闭时本值不被消费）。</summary>
    internal TimeSpan TrayDoubleClickWindow { get; set; } =
        OperatingSystem.IsWindows() ? TimeSpan.FromMilliseconds(GetDoubleClickTime()) : TimeSpan.FromMilliseconds(500);

    /// <summary>双击计时时钟（默认真实 UtcNow）；headless 用例注入假时钟保证时序确定性。</summary>
    internal Func<DateTimeOffset> TrayClock { get; set; } = static () => DateTimeOffset.UtcNow;

    public TrayService(Window mainWindow) : this(mainWindow, null, null, null)
    {
    }

    public TrayService(Window mainWindow, DashboardViewModel? dashboard, MainWindowViewModel? shell, BackgroundRefreshService? refreshService, StatusPopoverController? popover = null, UpdateService? updateService = null)
    {
        _mainWindow = mainWindow;
        _dashboard = dashboard;
        _shell = shell;
        _refreshService = refreshService;
        _popover = popover;
        _updateService = updateService;
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

        // I-039 更新检查联动：设置页 / 启动静默周检查完，托盘「检查更新」项标题就地翻状态。
        // Changed 可能在后台线程触发（CheckNowAsync 续延），统一 marshal 到 UI 线程改标题。
        if (_updateService != null)
        {
            _updateService.Changed += OnUpdateServiceChanged;
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

    /// <summary>弹层托盘左键 Toggle（macOS interop 左键回调与 Avalonia Clicked 共用；未装配弹层时
    /// 维持 A-0 直开主窗）。走 <see cref="StatusPopoverController.ToggleFromTrayClick"/> 带失焦守卫
    /// （B-017）——菜单项的显式 Toggle 不经此处，不带守卫。</summary>
    private void TogglePopoverOrShowWindow()
    {
        if (_popover is { } popover)
        {
            popover.ToggleFromTrayClick();
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
        // I-039「检查更新」常驻项：未发现新版本时点击即查；发现后标题就地翻成「发现新版本 vX.Y.Z」，
        // 点击改为打开 Release 页（interop 无运行期插项能力，只能常驻 + 改标题，见 MacTrayInterop）。
        _macUpdateItemIndex = items.Count;
        items.Add(new(UpdateItemTitle, OpenUpdateMenuItem));
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
        _updateItem = new NativeMenuItem { Header = UpdateItemTitle(), Command = new RelayCommand(OpenUpdateMenuItem) };
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
                    _updateItem,
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
    /// 保持 Avalonia TrayIcon 路径供测试断言；不设标记的真实桌面环境才走 interop。
    /// internal：App 启动的开机自启动对账（S-006）同用此标记避免触碰真实 OS 状态。</summary>
    internal static bool IsHeadlessTestHost =>
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
                    WriteMacItemTitles(items);
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

        if (_updateItem is { } update)
        {
            update.Header = UpdateItemTitle();
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
        // DashboardViewModel 的 KPI 回写发生在后台刷新线程（BackgroundRefreshWiring 整轮 Task.Run），
        // 其 PropertyChanged 同步回调到这里；TrayIcon.ToolTipText / NativeMenuItem.Header 都是
        // UI 线程亲和对象——非 UI 线程直接赋值会抛 InvalidOperationException，沿同步回调链冒进
        // RefreshAsync 把整轮扫描误判为失败（顶栏红字「数据源错误」）。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateToolTip(tooltip));
            return;
        }

        if (_macInterop)
        {
            // Cocoa 调用必须主线程（上面已保证；此分支保留原兜底语义，UI 线程上 RunOnUIThread 就地执行）
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
    /// <para><b>双击拦截（2026-10-26 设计，仅 Windows）</b>：Avalonia TrayIcon 无 DoubleClick 事件，
    /// Win32 双击表现为两次 Clicked——间隔在判定窗口内的第二击不再 Toggle，改为收弹层（仅可见时）+
    /// <see cref="ShowWindow"/>（单击瞄弹层 / 双击进主窗的分层）。与 B-017 失焦守卫互不干扰：守卫只
    /// 作用于第一击的 Toggle 语义，双击计时在拦截层独立记录；双击消费后时间戳清空，连点第三击回到单击。</para>
    /// <b>平台事实（实施留证）</b>：本事件仅 Win32/部分 Linux DE 会 raise——macOS 上 Avalonia 从不
    /// raise（TrayIcon.cs 文档原文 "on OSX this event is not raised"，trayicon.mm 亦无点击回调），
    /// macOS 生产左键走 MacTrayInterop 自管状态项。</summary>
    internal void OnTrayClicked()
    {
        var now = TrayClock();
        var isDoubleClick = TrayDoubleClickEnabled
            && _lastTrayClickedUtc is { } last
            && now - last < TrayDoubleClickWindow;
        _lastTrayClickedUtc = isDoubleClick ? null : now;

        if (isDoubleClick)
        {
            _popover?.HideIfVisible();
            ShowWindow();
            return;
        }

        TogglePopoverOrShowWindow();
    }

    // ── I-039 托盘「检查更新」项 ──

    /// <summary>菜单项标题随更新状态翻转：检查中 / 发现新版本 vX.Y.Z / 默认「检查更新」。
    /// 活取串（Func）供 interop 路径语言切换复用。</summary>
    private string UpdateItemTitle() => _updateService is null
        ? Translations.Instance.TrayUpdateCheck
        : _updateService.State switch
        {
            UpdateState.Checking => Translations.Instance.TrayUpdateChecking,
            UpdateState.Available => string.Format(
                Translations.Instance.TrayUpdateAvailable, _updateService.LastResult?.LatestVersion),
            _ => Translations.Instance.TrayUpdateCheck,
        };

    /// <summary>点击分派：已知新版本 → 浏览器打开 Release 页；否则发起一次检查（结果经 Changed 回调翻标题）。</summary>
    private void OpenUpdateMenuItem()
    {
        if (_updateService is null)
        {
            return;
        }

        if (_updateService.State == UpdateState.Available)
        {
            _updateService.OpenReleasePage();
            return;
        }

        _ = RunTrayUpdateCheckAsync();
    }

    private async Task RunTrayUpdateCheckAsync()
    {
        try
        {
            await _updateService!.CheckNowAsync();
        }
        catch (Exception ex)
        {
            // CheckAsync 正常不抛（网络失败转结果）；这里兜意外，托盘点击不打断用户。
            Console.Error.WriteLine($"[Tokenmetry] 托盘检查更新失败：{ex.Message}");
        }
    }

    private void OnUpdateServiceChanged() => RunOnUIThread(RefreshUpdateItemTitle);

    /// <summary>就地刷新更新项标题（两条托盘路径各自的原生侧改法——不得替换菜单实例）。</summary>
    private void RefreshUpdateItemTitle()
    {
        var title = UpdateItemTitle();
        if (_macInterop)
        {
            if (_macUpdateItemIndex >= 0)
            {
                MacTrayInterop.SetItemTitle(_macUpdateItemIndex, title);
            }
            return;
        }

        if (_updateItem is { } item)
        {
            item.Header = title;
        }
    }

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

    /// <summary>interop 菜单按安装序号全量重写标题（重译 / 成本行回写共用循环形态）。
    /// 分隔线必须跳过：其 Title 恒为 null（BuildMacTrayItems 以 null! 占位，无标题可写），
    /// 与 MacTrayInterop.Install 建项循环同纪律——无脑 Title() 会沿 PropertyChanged 同步链
    /// 炸 NRE 打断语言切换（真机必现；headless 宿主 _macInterop 恒 false 不可达，测试钉住）。</summary>
    internal static void WriteMacItemTitles(IReadOnlyList<MacTrayInterop.MacTrayItem> items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var spec = items[index];
            if (spec.IsSeparator)
            {
                continue;
            }

            MacTrayInterop.SetItemTitle(index, spec.Title());
        }
    }

    /// <summary>唤回主窗：托盘菜单 / 左键分派与 B-013 第二实例激活请求共用同一语义
    /// （internal 供 App 的激活监听回调复用）。</summary>
    internal void ShowWindow()
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

    /// <summary>系统双击时长（ms，控制面板鼠标设置；默认 500）。仅 Windows 初始化路径调用
    /// （<see cref="TrayDoubleClickWindow"/> 属性初始化器已按平台门控），非 Windows 不触达。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

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
