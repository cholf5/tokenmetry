using System.Collections.Generic;
using System.Linq;

namespace Tokenmetry.App.Services;

/// <summary>
/// 可选画板（整套配色皮肤）的信息。画板 × 模式两维：<see cref="Modes"/> 是该画板支持的亮暗档
/// （"light" / "dark"，多档画板另含 "system" 跟随系统）；单档画板（Nord/Dracula 天生只有暗色）
/// 在应用处强制唯一档。画板字典由 scripts/generate-theme-palettes.py 生成于 Styles/Themes/，
/// 运行期由 ThemeService 换入 Application.Resources（合并字典后合入者胜，压过常驻的默认画板
/// Tokens.axaml；已实测确认方向）。
/// </summary>
public sealed record ThemePaletteInfo(string Id, IReadOnlyList<string> Modes, string? ResourcePath);

/// <summary>画板目录：新增画板 = 生成脚本加画板定义 → 此处登记一条（Id 与脚本内一致）。</summary>
public static class ThemePaletteCatalog
{
    public const string DefaultPaletteId = "default";

    /// <summary>默认画板：Tokens.axaml 常驻（App.axaml 静态合入），无运行期字典可换。</summary>
    public static ThemePaletteInfo Default { get; } =
        new(DefaultPaletteId, ["light", "dark", "system"], null);

    public static ThemePaletteInfo Nord { get; } =
        new("nord", ["dark"], "avares://Tokenmetry.App/Styles/Themes/Nord.axaml");

    public static ThemePaletteInfo Dracula { get; } =
        new("dracula", ["dark"], "avares://Tokenmetry.App/Styles/Themes/Dracula.axaml");

    public static ThemePaletteInfo Solarized { get; } =
        new("solarized", ["light", "dark", "system"], "avares://Tokenmetry.App/Styles/Themes/Solarized.axaml");

    public static ThemePaletteInfo Gruvbox { get; } =
        new("gruvbox", ["light", "dark", "system"], "avares://Tokenmetry.App/Styles/Themes/Gruvbox.axaml");

    /// <summary>全部画板（顺序即设置页下拉展示顺序，默认画板恒在首位）。</summary>
    public static IReadOnlyList<ThemePaletteInfo> All { get; } =
        [Default, Nord, Dracula, Solarized, Gruvbox];

    /// <summary>画板偏好解析：未知/空值如实回落默认画板（与 Theme/Language 的未知值处理同风格，
    /// 不会让 settings.json 整体失效）。</summary>
    public static ThemePaletteInfo Resolve(string? id)
    {
        var normalized = (id ?? string.Empty).Trim().ToLowerInvariant();
        return All.FirstOrDefault(p => p.Id == normalized) ?? Default;
    }
}
