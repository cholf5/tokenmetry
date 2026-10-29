using System;
using System.IO;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Tokenmetry.App;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Services;

namespace Tokenmetry.App.Headless.Tests.Theming;

/// <summary>
/// I-026 headless 测试宿主：以 headless 平台启动真实的 App（App.axaml 里的
/// SemiTheme 生效（DataGridSemiTheme 与自建 ThemeDictionaries 已随 Q-002 清理移除）），
/// 不 mock 样式层，保证被测对象就是迁移产物本身。
/// I-027：UseHeadlessDrawing=false 改用真实 Skia 离屏渲染 —— 默认的 headless drawing
/// 管线无法截取合成帧（CaptureRenderedFrame 直接 NotSupported/空帧），而样式预览页的
/// 明暗两套截图留档（DesignTokenTests）需要整窗合成结果；资源/样式断言行为不受影响。
/// A-2：AppData 根重定向到测试进程专属临时目录（真实启动序列在此目录建 store/settings），
/// 测试进程绝不读写真实用户数据；旧临时目录由 OS 定期清理，不做进程内删除（并发会话安全）。
/// </summary>
public static class SemiTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // AppServices.Initialize 在首个测试触发生命周期时运行；覆盖必须先于它落定。
        AppServices.PathsOverride ??= new AppPaths(Path.Combine(
            Path.GetTempPath(), "ccusage-headless-tests", Guid.NewGuid().ToString("N")));

        // D-016：headless 宿主标记——TrayService 据此跳过 macOS interop 托盘（不自管 NSStatusItem），
        // 保持 Avalonia TrayIcon 路径供测试断言；真实桌面不设此变量。
        Environment.SetEnvironmentVariable("TOKENMETRY_HEADLESS", "1");

        return AppBuilder.Configure<App>()
            .UseSkia() // 注册 Skia 渲染/字体后端（UseHeadlessDrawing=false 时 IPlatformRenderInterface/IFontManagerImpl 由此提供）
            .WithInterFont() // 与 Program.BuildAvaloniaApp 对齐：AppFontFamily 令牌指向的嵌入字体必须在测试宿主注册
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
    }
}
