using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.App.Tray;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // A-7：启动期不可恢复错误（facts 库迁移失败等）给可读错误窗，不再原始异常闪退。
        try
        {
            DependencyInjection.AppServices.Initialize();
        }
        catch (Exception ex)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime errorDesktop)
            {
                ShowStartupError(errorDesktop, string.Format(
                    Localization.Translations.Instance.StartupErrorBodyFormat,
                    ex.Message,
                    Services.AppPaths.CreateDefault().CacheDbPath));
                base.OnFrameworkInitializationCompleted();
                return;
            }

            throw;
        }

        var settings = DependencyInjection.AppServices.Get<AppSettings>();
        DependencyInjection.AppServices.Get<IThemeService>().Apply(settings.Theme);
        // 界面语言在主窗 / 托盘创建前应用（托盘菜单、顶栏、各页文案都经 Translations 取串）。
        Localization.Translations.ApplyPreference(settings.Language);

        // S-006 开机自启动对账：settings 为源，启用时重写 OS 注册（注册内容指向当前可执行文件——
        // 应用更新换路径后自启动不失效）。仅在启用时执行：默认关闭路径零 OS 写入，headless 测试
        // 宿主绝不触碰真实 OS 注册状态（TrayService 同一枚标记）。失败只记诊断不阻塞启动，
        // 下次启动再试；设置页开关的失败回滚是另一条即时路径。
        if (settings.AutoStartOnBoot && !Tray.TrayService.IsHeadlessTestHost)
        {
            try
            {
                DependencyInjection.AppServices.Get<IAutoStartManager>().SetEnabled(true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Tokenmetry] 开机自启动注册对账失败（不阻塞启动）：{ex.Message}");
            }
        }

        // S-006：开机自启动注册写入的 --autostart 参数 → 静默启动：不弹主窗、常驻托盘（A-0 语义）。
        // 主窗照常创建（DataContext / 托盘 / 弹层都挂它），只是不 Show；托盘菜单与弹层随时唤回。
        var startHidden = Environment.GetCommandLineArgs().Contains(Services.AutoStartManager.LaunchArg);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindowViewModel = DependencyInjection.AppServices.Get<MainWindowViewModel>();
            // S-005：主窗持有关窗行为依赖（设置单例 + 落盘 store）——OnClosing 据此分派
            // 隐藏到托盘 / 首关一次性提示 / 关窗即退出。
            var mainWindow = new MainWindow(
                settings,
                DependencyInjection.AppServices.Get<IAppSettingsStore>())
            {
                DataContext = mainWindowViewModel,
            };
            desktop.MainWindow = mainWindow;

            // D-018：macOS Dock 图标。裸可执行（无 .app bundle）下 Avalonia 不设 Dock 图标
            // （Window.Icon 只影响窗口语义），经 MacTrayInterop 的 ObjC 通道喂 Apple 网格
            // PNG 给 NSApplication。若未来改走 .app bundle + Info.plist icns，删掉本调用。
            if (OperatingSystem.IsMacOS() && !Tray.TrayService.IsHeadlessTestHost)
            {
                try
                {
                    using var dockIconStream = AssetLoader.Open(
                        new Uri("avares://Tokenmetry.App/Assets/app-icon.png"));
                    using var dockIconBuffer = new MemoryStream();
                    dockIconStream.CopyTo(dockIconBuffer);
                    Tray.MacTrayInterop.TrySetDockIcon(dockIconBuffer.ToArray());
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Tokenmetry] Dock 图标设置失败（不阻塞启动）：{ex.Message}");
                }
            }

            // A-0：托盘常驻。dashboard/refreshService 先于 TrayService 解析——托盘的今日成本
            // 联动、「立即刷新」「设置」菜单都依赖容器内同一实例。
            var dashboard = DependencyInjection.AppServices.Get<DashboardViewModel>();
            var refreshService = DependencyInjection.AppServices.Get<BackgroundRefreshService>();
            // D-016：状态栏快捷信息窗（托盘左键弹层）——内容层 D-015 查询 IUsageQueryService 同源
            // 管线，速率挂 dashboard 每轮回写，Agent 行/设置/打开统计经外壳既有导航命令唤回主窗。
            var popover = new Tray.StatusPopoverController(
                mainWindow,
                mainWindowViewModel,
                DependencyInjection.AppServices.Get<IUsageQueryService>(),
                dashboard,
                DependencyInjection.AppServices.Get<DateRangeService>(),
                DependencyInjection.AppServices.Get<ModelAvatarResolver>());
            var updateService = DependencyInjection.AppServices.Get<Update.UpdateService>();
            var trayService = new TrayService(mainWindow, dashboard, mainWindowViewModel, refreshService, popover, updateService);
            trayService.Initialize();

            // B-013：首实例监听第二实例的激活请求——唤回主窗与托盘「打开窗口」同一语义。
            // 守卫在 Program.Main 获取（headless 测试宿主旁路后恒为 null），监听必须在主窗
            // / 托盘就绪后才启动：激活回调要操作窗口。
            if (Program.ActiveSingleInstance is { } singleInstance)
            {
                singleInstance.StartListener(() =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(trayService.ShowWindow));
            }

            // B-018：macOS 点 Dock 图标唤回主窗。真机取证（卡内留证）：①上游 AvnAppDelegate 只
            // 实现拼写错误的 applicationShouldHandleReopen:hasVisibleWindows:（缺 Any），AppKit
            // 永远调不到；②macOS 26 裸可执行进程连正确拼写的委托方法也不被咨询（原生对照实验
            // 同样复现）——实际生效通道是自有 NSAppleEventManager 'rapp' handler。该注册必须晚于
            // NSApp finishLaunching（否则被 NSApplication 自装的 'rapp' handler 顶掉），而本方法
            // 运行于 finishLaunching 之前——必须 Post 到 run loop 首个空闲点。interop 内部全捕获，
            // 失败只降级为现状（Dock 死点）不阻塞启动。
            if (OperatingSystem.IsMacOS() && !Tray.TrayService.IsHeadlessTestHost)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => Tray.MacTrayInterop.TryInstallDockReopen(trayService.ShowWindow));
            }

            // D-003/Q-002：DashboardViewModel 无视图挂载（旧视图已清理），作为后台采集刷新锚点保留——
            // 刷新把本地会话日志解析进 facts 存储，供各页面卡查询（定案见其类型注释）。
            // S-002 审查修复（P0）：刷新接线在 dashboard 与外壳 VM 都就绪后 Attach，随后启动后台
            // 刷新服务——设置页「重新扫描」经容器内同一 BackgroundRefreshService 实例走同一条
            // 采集线（不再有 late map / 兜底实例），外壳时间戳回写也不再依赖 shell 局部变量的
            // 赋值顺序。
            DependencyInjection.AppServices.Get<BackgroundRefreshWiring>().Attach(dashboard, mainWindowViewModel);
            refreshService.Start();
            // A-0：真正退出（托盘「退出」/ OS 会话结束）经生命周期正常收尾——Exit 在两种
            // shutdown 路径都会触发，此处停掉后台刷新定时器（替代此前 Environment.Exit 的跳过）。
            desktop.Exit += (_, _) => refreshService.Stop();
            // 启动数据初始化：进程打开立即触发一轮采集刷新（定时器首跑要等一个完整间隔），
            // 顶栏即时切到"正在扫描"反馈；探测到库内完全无数据（首次使用）时挂全窗口
            // "正在初始化"遮罩，本轮结束自动摘除（成败均然）——首启不再是一片空白等用户
            // 手动点刷新。启动路径不观察成败返回值，回写经 Note* 内部 marshal 到 UI 线程。
            _ = mainWindowViewModel.BeginStartupDataRefreshAsync();

            // I-039 更新静默周检：启动 30s 后（错开首轮采集）判 ShouldAutoCheck（≥7 天才发请求），
            // 结果只翻托盘「检查更新」项标题，不弹窗不打扰；用户随时可手动查（托盘项 / 设置页）。
            // headless 测试宿主禁走（CI 里发真实网络请求）。
            if (!Tray.TrayService.IsHeadlessTestHost)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    if (!updateService.ShouldAutoCheck(TimeSpan.FromDays(7)))
                    {
                        return;
                    }
                    try
                    {
                        await updateService.CheckNowAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[Tokenmetry] 启动期检查更新失败：{ex.Message}");
                    }
                });
            }

            // A-0：主窗口随启动显示（此前从未 Show——修好托盘前应用启动后没有任何可见界面）；
            // --autostart（S-006 开机自启动）静默启动例外——不弹主窗，进程常驻托盘。
            if (!startHidden)
            {
                mainWindow.Show();
            }

            // [D-004/D-005] dev-only 预览窗口（Q-002 定案：StylePreviewView 为唯一保留的临时宿主，
            // 无导航入口、不进生产用户路径）：设置 TOKENMETRY_PREVIEW_WINDOW=1 启动时另开一个预览窗，
            // 用于图表控件 / 设计令牌的真机 hover 目检（TC-001/TC-002）与明暗切换观察；
            // 不影响正常启动与测试。
            if (Environment.GetEnvironmentVariable("TOKENMETRY_PREVIEW_WINDOW") == "1")
            {
                var previewWindow = new Window
                {
                    Title = "图表控件预览（D-004/D-005 临时）",
                    Width = 1100,
                    Height = 940,
                    Content = new StylePreviewView(),
                };
                previewWindow.Show();
                // 审查 P2-6：预览窗随主窗关闭（主窗可最小化到托盘继续运行，避免预览窗残留）
                desktop.MainWindow!.Closed += (_, _) => previewWindow.Close();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A-7：启动期不可恢复错误的可读提示窗（唯一窗口，关闭即退出进程）。
    /// 不用 MainWindow 的关窗隐藏拦截（那是常驻体验语义，错误窗必须可退出）。
    /// </summary>
    private static void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, string message)
    {
        // 初始化失败时 settings 尚未加载（也未应用语言偏好），文案按系统 UI 语言解析——
        // Translations 不依赖 DI 容器，此处可安全取串。
        var t = Localization.Translations.Instance;
        var closeButton = new Button { Content = t.StartupErrorExit, Width = 96, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var dialog = new Window
        {
            Title = t.StartupErrorTitle,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    closeButton,
                },
            },
        };
        desktop.MainWindow = dialog;
        closeButton.Click += (_, _) => dialog.Close();
        // ShutdownMode 默认 OnLastWindowClose：错误窗关闭 = 进程退出
        dialog.Show();
    }
}