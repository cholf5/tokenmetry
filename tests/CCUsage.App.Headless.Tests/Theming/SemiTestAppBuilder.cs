using System;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using CCUsage.App;

namespace CCUsage.App.Headless.Tests.Theming;

/// <summary>
/// I-026 headless 测试宿主：以 headless 平台启动真实的 App（App.axaml 里的
/// SemiTheme + DataGridSemiTheme + 自建 ThemeDictionaries 全部生效），
/// 不 mock 样式层，保证被测对象就是迁移产物本身。
/// </summary>
public static class SemiTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
