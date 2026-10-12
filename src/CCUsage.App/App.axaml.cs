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

            var trayService = new TrayService(mainWindow);
            trayService.Initialize();

            // D-003：DashboardViewModel 不再挂在导航上（旧视图由 Q-002 清理），但本地采集
            // 周期刷新仍由它驱动——刷新把本地会话日志解析进 facts 存储，供后续页面卡查询；
            // 刷新完成回写顶部栏"最近更新"时间戳（NoteDataRefreshed 内部 marshal 到 UI 线程）。
            var dashboard = DependencyInjection.AppServices.Get<DashboardViewModel>();
            var refreshService = new BackgroundRefreshService(
                async () =>
                {
                    await dashboard.RefreshCommand.ExecuteAsync(null);
                    mainWindowViewModel.NoteDataRefreshed();
                },
                settings.RefreshMinutes);
            refreshService.Start();
            // 启动立即首刷：定时器首跑要等一个完整间隔（默认 15 分钟），首启顶栏一直"尚未刷新"；
            // 触发一次立即刷新（定时器语义不变），回写经 NoteDataRefreshed 内部 marshal 到 UI 线程。
            _ = refreshService.TriggerRefreshAsync();

            // [D-004/D-005] 临时预览窗口（StylePreviewView 自 I-027 起无导航入口）：设置
            // CCUSAGE_PREVIEW_WINDOW=1 启动时另开一个预览窗，用于图表控件的真机 hover 目检
            // （TC-001/TC-002）与明暗切换观察；不影响正常启动与测试。
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