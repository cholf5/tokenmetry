using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Tokenmetry.App.Services;
using Tokenmetry.App.Views;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Theming;

/// <summary>
/// 应用画板（Styles/Themes/，ThemePaletteCatalog × ThemeService）的 headless 验证：
/// 1. 画板 × 模式两维应用：换字典后 App* 令牌解析到画板值，单档画板强制唯一变体；
/// 2. 默认画板退挂字典恢复 Tokens.axaml 常驻值；反复切换不累积字典；
/// 3. Applied 事件触发（图表重绘信号）；
/// 4. 海报隔离：SharePosterView 的 ThemeScope 本地合入默认 Tokens，画板压不进海报子树。
/// 每个用例 finally 还原默认画板——本程序集禁并行但用例间共享 Application 状态。
/// </summary>
public class ThemePaletteTests
{
    [AvaloniaFact]
    public void Apply_nord_swaps_tokens_and_pins_dark_variant()
    {
        var app = Application.Current!;
        var service = new ThemeService();
        try
        {
            // Nord 只定义 Dark：无论模式偏好是什么（哪怕 light），变体强制 Dark
            service.Apply("nord", "light");

            Assert.Equal("nord", service.AppliedPaletteId);
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.True(app.TryGetResource("AppBackgroundBrush", ThemeVariant.Dark, out var darkBg));
            Assert.Equal(Color.Parse("#2e3440"), Assert.IsAssignableFrom<ISolidColorBrush>(darkBg).Color);

            // Semi 别名键同样被画板接管（TextBox 底色不再是 Semi 默认灰，而是画板派生值）
            Assert.True(app.TryGetResource("TextBoxDefaultBackground", ThemeVariant.Dark, out var textBoxBg));
            Assert.IsAssignableFrom<ISolidColorBrush>(textBoxBg);

            // 同一变体下，默认画板的旧值不再可达（画板字典后合入者胜，已实测）
            Assert.NotEqual(Color.Parse("#0f1118"), ((ISolidColorBrush)darkBg!).Color);
        }
        finally
        {
            service.Apply(ThemePaletteCatalog.DefaultPaletteId, "dark");
        }
    }

    [AvaloniaFact]
    public void Apply_solarized_light_and_dark_respective_variants()
    {
        var app = Application.Current!;
        var service = new ThemeService();
        try
        {
            service.Apply("solarized", "light");
            Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
            Assert.True(app.TryGetResource("AppBackgroundBrush", ThemeVariant.Light, out var lightBg));
            Assert.Equal(Color.Parse("#fdf6e3"), Assert.IsAssignableFrom<ISolidColorBrush>(lightBg).Color);

            service.Apply("solarized", "dark");
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.True(app.TryGetResource("AppBackgroundBrush", ThemeVariant.Dark, out var darkBg));
            Assert.Equal(Color.Parse("#002b36"), Assert.IsAssignableFrom<ISolidColorBrush>(darkBg).Color);
        }
        finally
        {
            service.Apply(ThemePaletteCatalog.DefaultPaletteId, "dark");
        }
    }

    [AvaloniaFact]
    public void Apply_default_restores_tokens_and_does_not_accumulate_dictionaries()
    {
        var app = Application.Current!;
        var service = new ThemeService();
        var baselineCount = app.Resources.MergedDictionaries.Count;
        try
        {
            service.Apply("nord", "dark");
            Assert.Equal(baselineCount + 1, app.Resources.MergedDictionaries.Count);

            // 反复切换不同画板：始终恰好一个画板字典在挂（先退挂再追加）
            service.Apply("dracula", "dark");
            service.Apply("gruvbox", "dark");
            Assert.Equal(baselineCount + 1, app.Resources.MergedDictionaries.Count);

            service.Apply(ThemePaletteCatalog.DefaultPaletteId, "dark");
            Assert.Equal(baselineCount, app.Resources.MergedDictionaries.Count);
            Assert.Equal("default", service.AppliedPaletteId);
            Assert.True(app.TryGetResource("AppBackgroundBrush", ThemeVariant.Dark, out var darkBg));
            Assert.Equal(Color.Parse("#0f1118"), Assert.IsAssignableFrom<ISolidColorBrush>(darkBg).Color);
        }
        finally
        {
            service.Apply(ThemePaletteCatalog.DefaultPaletteId, "dark");
        }
    }

    [AvaloniaFact]
    public void Applied_event_fires_on_every_apply()
    {
        var service = new ThemeService();
        var fired = 0;
        EventHandler handler = (_, _) => fired++;
        service.Applied += handler;
        try
        {
            service.Apply("nord", "dark");
            service.Apply(ThemePaletteCatalog.DefaultPaletteId, "dark");
            Assert.Equal(2, fired);
        }
        finally
        {
            service.Applied -= handler;
            service.Apply(ThemePaletteCatalog.DefaultPaletteId, "dark");
        }
    }

    [AvaloniaFact]
    public void Poster_theme_scope_shields_poster_subtree_from_app_palette()
    {
        var app = Application.Current!;
        var service = new ThemeService();
        try
        {
            service.Apply("dracula", "dark");

            // 应用层：App* 令牌已是 Dracula
            Assert.True(app.TryGetResource("AppBackgroundBrush", ThemeVariant.Dark, out var appBg));
            Assert.Equal(Color.Parse("#282a36"), Assert.IsAssignableFrom<ISolidColorBrush>(appBg).Color);

            // 海报子树：ThemeScope 本地合入默认 Tokens，元素层查找先命中默认画板——
            // 拿真实 SharePosterView（ThemeScope.Resources 含 Tokens.axaml）断言隔离
            var view = new SharePosterView();
            var scope = view.FindControl<ThemeVariantScope>("ThemeScope");
            Assert.NotNull(scope);
            Assert.True(scope!.TryGetResource("AppBackgroundBrush", ThemeVariant.Dark, out var posterBg));
            Assert.Equal(Color.Parse("#0f1118"), Assert.IsAssignableFrom<ISolidColorBrush>(posterBg).Color);
        }
        finally
        {
            service.Apply(ThemePaletteCatalog.DefaultPaletteId, "dark");
        }
    }
}
