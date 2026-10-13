using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CCUsage.App.ViewModels;
using CCUsage.App.Views;
using CCUsage.App.Tray;
using CCUsage.App.Services;
using CCUsage.Core.Models;

namespace CCUsage.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DependencyInjection.AppServices.Initialize();

        var settings = DependencyInjection.AppServices.Get<AppSettings>();
        DependencyInjection.AppServices.Get<IThemeService>().Apply(settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindowViewModel = DependencyInjection.AppServices.Get<MainWindowViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };
            desktop.MainWindow = mainWindow;

            // A-0：托盘常驻。dashboard/refreshService 先于 TrayService 解析——托盘的今日成本
            // 联动、「立即刷新」「设置」菜单都依赖容器内同一实例。
            var dashboard = DependencyInjection.AppServices.Get<DashboardViewModel>();
            var refreshService = DependencyInjection.AppServices.Get<BackgroundRefreshService>();
            var trayService = new TrayService(mainWindow, dashboard, mainWindowViewModel, refreshService);
            trayService.Initialize();

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
            // 启动立即首刷：定时器首跑要等一个完整间隔（默认 15 分钟），首启顶栏一直"尚未刷新"；
            // 触发一次立即刷新（定时器语义不变；启动路径不观察成败返回值），
            // 回写经 NoteDataRefreshed 内部 marshal 到 UI 线程。
            _ = refreshService.TriggerRefreshAsync();

            // A-0：主窗口随启动显示（此前从未 Show——修好托盘前应用启动后没有任何可见界面）
            mainWindow.Show();

            // [D-004/D-005] dev-only 预览窗口（Q-002 定案：StylePreviewView 为唯一保留的临时宿主，
            // 无导航入口、不进生产用户路径）：设置 CCUSAGE_PREVIEW_WINDOW=1 启动时另开一个预览窗，
            // 用于图表控件 / 设计令牌的真机 hover 目检（TC-001/TC-002）与明暗切换观察；
            // 不影响正常启动与测试。
            if (Environment.GetEnvironmentVariable("CCUSAGE_PREVIEW_WINDOW") == "1")
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
}