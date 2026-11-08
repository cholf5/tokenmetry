using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tokenmetry.App.Styles.Themes;

/// <summary>
/// Nord 画板字典：编译期 XAML（x:Class），ThemeService 类型化实例化。
/// 由 scripts/generate-theme-palettes.py 生成，勿手改。Load(this) 走编译期 populate（同 App.axaml.cs
/// 的既有模式）；刻意不走 AvaloniaXamlLoader.Load(Uri)——URI 重载带 RequiresUnreferencedCode，
/// AOT 发布（TreatWarningsAsErrors）下直接 IL2026。
/// </summary>
public partial class Nord : ResourceDictionary
{
    public Nord() => AvaloniaXamlLoader.Load(this);
}
