using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Tokenmetry.App.Services;

public interface IThemeService
{
    /// <summary>
    /// 应用画板 × 模式两维主题：画板（ThemePaletteCatalog，default/nord/dracula/solarized/gruvbox）
    /// 决定配色气质（运行期换 App 资源字典），模式（light/dark/system）决定亮暗档（RequestedThemeVariant）。
    /// </summary>
    void Apply(string? palettePreference, string? modePreference);

    /// <summary>当前生效画板 Id（未知偏好落 default；测试复原与诊断用）。</summary>
    string AppliedPaletteId { get; }

    /// <summary>
    /// 画板或模式应用完成（UI 线程同步触发，DynamicResource 消费方自动更新，无需订阅；
    /// 订阅者是 ScottPlot 这类「代码现取令牌渲染位图」的控件——变体不变而画板切换时
    /// ActualThemeVariantChanged 不触发，需要本信号重绘）。控件侧订阅须挂/脱树配对退订
    /// （同 Translations 订阅纪律，服务是应用级单例）。
    /// </summary>
    event EventHandler? Applied;
}

public sealed class ThemeService : IThemeService
{
    private ResourceDictionary? _appliedPalette;

    public string AppliedPaletteId { get; private set; } = ThemePaletteCatalog.DefaultPaletteId;

    public event EventHandler? Applied;

    public void Apply(string? palettePreference, string? modePreference)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var palette = ThemePaletteCatalog.Resolve(palettePreference);

        // 换字典：先移除旧画板再追加到末尾——Application.Resources 的合并字典「后合入者胜」
        //（Avalonia 12 实测确认），必须压过常驻的 Tokens.axaml 画板值才能接管 App* 键。
        // 默认画板没有运行期字典：只退挂。单档画板只定义自己支持的那档 ThemeDictionaries，
        // 误入另一档时查找自然落回 Tokens.axaml 同名档——安全的语义回落。
        // 每次重新解析 XAML：ResourceDictionary 一旦挂过 Application 就记住 owner，复用旧实例
        // 再挂会抛「already has a parent」；画板切换是低频操作，重复解析成本可忽略。
        if (_appliedPalette is not null)
        {
            app.Resources.MergedDictionaries.Remove(_appliedPalette);
            _appliedPalette = null;
        }

        if (palette.ResourcePath is { } resourcePath)
        {
            _appliedPalette = AvaloniaXamlLoader.Load(new Uri(resourcePath)) as ResourceDictionary
                ?? throw new InvalidOperationException($"画板字典加载失败：{resourcePath}");
            app.Resources.MergedDictionaries.Add(_appliedPalette);
        }

        AppliedPaletteId = palette.Id;

        // 模式归口：单档画板强制唯一档（Nord/Dracula 恒夜间）；多档画板 light/dark 直达，
        // system/未知 → Default 跟随操作系统并随系统切换自动更新。
        app.RequestedThemeVariant = palette.Modes.Count == 1
            ? ParseVariant(palette.Modes[0])
            : ParseVariant(modePreference) ?? ThemeVariant.Default;

        Applied?.Invoke(this, EventArgs.Empty);
    }

    private static ThemeVariant? ParseVariant(string? mode) => mode switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => null,
    };
}
