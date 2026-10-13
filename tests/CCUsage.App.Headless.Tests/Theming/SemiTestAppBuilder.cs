using System;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using CCUsage.App;

namespace CCUsage.App.Headless.Tests.Theming;

/// <summary>
/// I-026 headless 测试宿主：以 headless 平台启动真实的 App（App.axaml 里的
/// SemiTheme 生效（DataGridSemiTheme 与自建 ThemeDictionaries 已随 Q-002 清理移除）），
/// 不 mock 样式层，保证被测对象就是迁移产物本身。
/// I-027：UseHeadlessDrawing=false 改用真实 Skia 离屏渲染 —— 默认的 headless drawing
/// 管线无法截取合成帧（CaptureRenderedFrame 直接 NotSupported/空帧），而样式预览页的
/// 明暗两套截图留档（DesignTokenTests）需要整窗合成结果；资源/样式断言行为不受影响。
/// </summary>
public static class SemiTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia() // 注册 Skia 渲染/字体后端（UseHeadlessDrawing=false 时 IPlatformRenderInterface/IFontManagerImpl 由此提供）
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
}
