using Avalonia;
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
        }

        base.OnFrameworkInitializationCompleted();
    }
}