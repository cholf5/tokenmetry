using Avalonia;
using System;
using Tokenmetry.App.Services;

namespace Tokenmetry.App;

class Program
{
    /// <summary>B-013 单实例守卫：App.OnFrameworkInitializationCompleted 据此决定是否启动激活
    /// 监听。必须整个进程生命周期持有——Mutex 被终结器回收即释放、守卫失效。</summary>
    internal static SingleInstanceGuard? ActiveSingleInstance;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // B-013 单实例守卫：S-006 开机自启动 + 手动双开是自然组合，双进程同写 tokenmetry.db、
        // 双托盘、双倍采集扫描都要杜绝。第二实例给首实例发激活请求（唤回主窗）后静默退出；
        // headless 测试宿主整体旁路（多测试进程并行，TrayService.IsHeadlessTestHost 同源标记）。
        if (!Tray.TrayService.IsHeadlessTestHost)
        {
            var guard = new SingleInstanceGuard(SingleInstanceGuard.DefaultInstanceName);
            if (!guard.TryAcquire())
            {
                SingleInstanceGuard.TrySignalActivation(SingleInstanceGuard.DefaultInstanceName);
                return;
            }

            ActiveSingleInstance = guard;
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
