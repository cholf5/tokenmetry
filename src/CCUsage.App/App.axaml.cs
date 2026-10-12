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
            var mainWindow = new MainWindow
            {
                DataContext = DependencyInjection.AppServices.Get<MainWindowViewModel>(),
            };
            desktop.MainWindow = mainWindow;

            var trayService = new TrayService(mainWindow);
            trayService.Initialize();

            var vm = (MainWindowViewModel)desktop.MainWindow.DataContext;
            var refreshService = new BackgroundRefreshService(
                () => vm.Dashboard.RefreshCommand.ExecuteAsync(null),
                settings.RefreshMinutes);
            refreshService.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}