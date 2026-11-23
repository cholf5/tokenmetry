using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace Tokenmetry.App.Services;

/// <summary>
/// 可选画板（整套配色皮肤）的信息。画板 × 模式两维：<see cref="Modes"/> 是该画板支持的亮暗档
/// （"light" / "dark"，多档画板另含 "system" 跟随系统）；单档画板（Nord/Dracula 天生只有暗色）
/// 在应用处强制唯一档。画板字典由 scripts/generate-theme-palettes.py 生成于 Styles/Themes/
/// （编译期 XAML，x:Class + 同名 code-behind），<see cref="CreateDictionary"/> 是类型化工厂，
/// 运行期由 ThemeService 调用换入 Application.Resources（合并字典后合入者胜，压过常驻的默认画板
/// Tokens.axaml；已实测确认方向）。不走 AvaloniaXamlLoader.Load(Uri)——该 API 带
/// RequiresUnreferencedCode，AOT 发布（TreatWarningsAsErrors）下直接 IL2026。
/// </summary>
/// <param name="Swatches">
/// 设置页画板下拉的 5 条本色色带（页面底 / 面板 / 强调蓝 / 强调紫 / 强调橙），取自该画板 Dark 档——
/// 全部画板都有 Dark，且深色档最能代表画板气质。纯展示数据：刻意不在这里实例化画板字典
/// （非 headless 单测进程里解析 XAML 不可靠），与生成值的同步由 DesignTokenTests 校验。
/// </param>
/// <param name="LightSwatches">
/// 同一组五条色带取该画板 Light 档，供设置页模式预览卡（日间卡）使用；只有夜间档的画板为空列表。
/// 与 <paramref name="Swatches"/> 同源同纪律（DesignTokenTests 逐项对账）。
/// </param>
public sealed record ThemePaletteInfo(
    string Id,
    IReadOnlyList<string> Modes,
    Func<ResourceDictionary>? CreateDictionary,
    IReadOnlyList<string> Swatches,
    IReadOnlyList<string> LightSwatches)
{
    /// <summary>浅色档色带；无浅色档的画板回落深色档——仅用于渲染不可用卡的占位外观
    /// （该卡此时置灰，颜色不承担「这套皮肤长什么样」的信息）。</summary>
    public IReadOnlyList<string> LightFace =>
        LightSwatches.Count > 0 ? LightSwatches : Swatches;
}

/// <summary>画板目录：新增画板 = 生成脚本加画板定义 → 此处登记一条（Id 与脚本内一致）。</summary>
public static class ThemePaletteCatalog
{
    public const string DefaultPaletteId = "default";

    /// <summary>默认画板：Tokens.axaml 常驻（App.axaml 静态合入），无运行期字典可换。</summary>
    public static ThemePaletteInfo Default { get; } =
        new(DefaultPaletteId, ["light", "dark", "system"], null,
            ["#0f1118", "#171c25", "#4c8dff", "#a779ff", "#ffb454"],
            ["#f4f6fa", "#ffffff", "#2e6bdc", "#7d4fe0", "#c77700"]);

    public static ThemePaletteInfo Nord { get; } =
        new("nord", ["dark"], () => new Styles.Themes.Nord(),
            ["#2e3440", "#3b4252", "#9cb5ce", "#c4a7bf", "#dda796"],
            []);

    public static ThemePaletteInfo Dracula { get; } =
        new("dracula", ["dark"], () => new Styles.Themes.Dracula(),
            ["#282a36", "#2f3140", "#8c9eff", "#bd93f9", "#ffb86c"],
            []);

    public static ThemePaletteInfo Solarized { get; } =
        new("solarized", ["light", "dark", "system"], () => new Styles.Themes.Solarized(),
            ["#002b36", "#073642", "#4ca2df", "#9094d3", "#ec7e51"],
            ["#fdf6e3", "#eee8d5", "#1d699e", "#565cbb", "#af4113"]);

    public static ThemePaletteInfo Gruvbox { get; } =
        new("gruvbox", ["light", "dark", "system"], () => new Styles.Themes.Gruvbox(),
            ["#282828", "#3c3836", "#89a99d", "#d895a7", "#fe8019"],
            ["#fbf1c7", "#fdf8e6", "#076678", "#8f3f71", "#af3a03"]);

    public static ThemePaletteInfo Catppuccin { get; } =
        new("catppuccin", ["light", "dark", "system"], () => new Styles.Themes.Catppuccin(),
            ["#1e1e2e", "#313244", "#89b4fa", "#cba6f7", "#fab387"],
            ["#eff1f5", "#e6e9ef", "#0b59f4", "#8230ee", "#ac4001"]);

    public static ThemePaletteInfo TokyoNight { get; } =
        new("tokyonight", ["dark"], () => new Styles.Themes.TokyoNight(),
            ["#1a1b26", "#24283b", "#7aa2f7", "#bb9af7", "#ff9e64"],
            []);

    public static ThemePaletteInfo One { get; } =
        new("one", ["light", "dark", "system"], () => new Styles.Themes.One(),
            ["#282c34", "#2c313a", "#61afef", "#c980df", "#d19a66"],
            ["#fafafa", "#f0f0f1", "#1a5def", "#a626a4", "#8e6101"]);

    public static ThemePaletteInfo Ayu { get; } =
        new("ayu", ["light", "dark", "system"], () => new Styles.Themes.Ayu(),
            ["#0b0e14", "#11151c", "#59c2ff", "#d2a6ff", "#ff8f40"],
            ["#fafafa", "#f1f2f3", "#166ead", "#844ebb", "#af4c05"]);

    public static ThemePaletteInfo Kanagawa { get; } =
        new("kanagawa", ["dark"], () => new Styles.Themes.Kanagawa(),
            ["#1f1f28", "#2a2a37", "#7e9cd8", "#a08cbf", "#ffa066"],
            []);

    /// <summary>全部画板（顺序即设置页下拉展示顺序，默认画板恒在首位）。</summary>
    public static IReadOnlyList<ThemePaletteInfo> All { get; } =
        [Default, Nord, Dracula, Solarized, Gruvbox,
         Catppuccin, TokyoNight, One, Ayu, Kanagawa];

    /// <summary>画板偏好解析：未知/空值如实回落默认画板（与 Theme/Language 的未知值处理同风格，
    /// 不会让 settings.json 整体失效）。</summary>
    public static ThemePaletteInfo Resolve(string? id)
    {
        var normalized = (id ?? string.Empty).Trim().ToLowerInvariant();
        return All.FirstOrDefault(p => p.Id == normalized) ?? Default;
    }
}
