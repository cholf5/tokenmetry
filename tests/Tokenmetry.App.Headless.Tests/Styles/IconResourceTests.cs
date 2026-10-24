using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Tokenmetry.App.Controls;
using Tokenmetry.App.ViewModels;
using Xunit;
// System.IO.Path（隐式 using）与 Avalonia 的 Path 撞名，测试里的 Path 一律指矢量图标形状
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace Tokenmetry.App.Headless.Tests.Styles;

/// <summary>
/// 界面矢量图标（2026-10-24 矢量图标化：Icons.axaml 的 AppIcon* StreamGeometry +
/// SharedStyles 的 Path.app-icon）守卫：
/// 1. ShellPages 九页的图标键名全部能解析到几何资源（生成脚本名单 / ShellPages / 本表三方不漂移）；
/// 2. 几何资源两主题同实例（与明暗无关——图标颜色由描边画刷翻转）；
/// 3. Path.app-icon 统一描边样式真实应用（Stretch=Uniform / 1.5 粗细 / AppTextBrush 默认色）；
/// 4. PageIcon.Icon 附加属性把键名翻译成 pi-* 伪类 → Data 指向对应几何资源；
/// 5. 几何墨迹全部落在 24×24 设计框内（子路径拼接漂移护栏，见用例注释）。
/// </summary>
public class IconResourceTests
{
    /// <summary>图标键名（ShellPages.Icon 取值）→ 资源键。与 scripts/generate-ui-icons.py 的
    /// ICONS 表同步：名单变更必须同时更新生成器、Icons.axaml（重跑脚本）与本表。</summary>
    internal static readonly Dictionary<string, string> GeometryKeys = new()
    {
        ["house"] = "AppIconHouse",
        ["table"] = "AppIconTable",
        ["bot"] = "AppIconBot",
        ["cpu"] = "AppIconCpu",
        ["folder"] = "AppIconFolder",
        ["clock"] = "AppIconClock",
        ["dollar-circle"] = "AppIconDollarCircle",
        ["database"] = "AppIconDatabase",
        ["settings"] = "AppIconSettings",
        ["rotate-cw"] = "AppIconRotateCw",
        ["loader-circle"] = "AppIconLoaderCircle",
        ["coins"] = "AppIconCoins",
        ["zap"] = "AppIconZap",
    };

    [AvaloniaFact]
    public void ShellPages_icon_keys_all_resolve_to_geometry_resources()
    {
        var app = Application.Current!;
        foreach (var descriptor in ShellPages.All)
        {
            Assert.True(GeometryKeys.TryGetValue(descriptor.Icon, out var key),
                $"页面 {descriptor.Id} 的图标键 {descriptor.Icon} 不在名单（生成脚本与本表需同步）");
            Assert.True(app.TryGetResource(key, app.ActualThemeVariant, out var geometry),
                $"缺少几何资源 {key}");
            Assert.IsAssignableFrom<StreamGeometry>(geometry);
        }
    }

    [AvaloniaFact]
    public void Geometries_are_theme_invariant()
    {
        var app = Application.Current!;
        foreach (var key in GeometryKeys.Values)
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Light, out var light),
                $"Light 变体缺少几何资源 {key}");
            Assert.True(app.TryGetResource(key, ThemeVariant.Dark, out var dark),
                $"Dark 变体缺少几何资源 {key}");
            Assert.Same(light, dark); // 同一实例 ⇒ 与明暗无关
        }
    }

    [AvaloniaFact]
    public void Geometries_stay_within_24x24_design_box()
    {
        // 子路径拼接护栏（I-040 实锤教训）：多个子路径合并进同一个 d 串时，行首相对 moveto
        // 若未按 SVG 规范（首命令 m 按绝对解释）规范化并补隐式 l，会相对上一子路径终点漂移
        // 出 24×24 设计框（coins 首轮生成的 bug：小线段漂到 (22.1, 24.8)）。lucide 全集几何
        // 墨迹都在框内（stroke 2 留边），出框即转换错误。
        var app = Application.Current!;
        foreach (var key in GeometryKeys.Values)
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Default, out var value));
            var bounds = Assert.IsAssignableFrom<StreamGeometry>(value).Bounds;
            Assert.True(bounds.X >= 0 && bounds.Y >= 0 && bounds.Right <= 24 && bounds.Bottom <= 24,
                $"{key} 几何出框：{bounds}");
        }
    }

    [AvaloniaFact]
    public void App_icon_style_applies_unified_stroke()
    {
        var app = Application.Current!;
        var path = new AvaloniaPath { Classes = { "app-icon" } };
        var window = new Window { Content = path };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Stretch.Uniform, path.Stretch);
        Assert.Equal(1.5, path.StrokeThickness);
        Assert.True(app.TryGetResource("AppTextBrush", window.ActualThemeVariant, out var text));
        Assert.Same(text, path.Stroke);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void PageIcon_attached_property_resolves_data_per_page()
    {
        var app = Application.Current!;
        foreach (var descriptor in ShellPages.All)
        {
            var path = new AvaloniaPath { Classes = { "app-icon" } };
            PageIcon.SetIcon(path, descriptor.Icon);
            var window = new Window { Content = path };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(path.Classes.Contains($"pi-{descriptor.Icon}"),
                $"页面 {descriptor.Id}：pi-{descriptor.Icon} 伪类未挂上");
            Assert.True(app.TryGetResource(GeometryKeys[descriptor.Icon], window.ActualThemeVariant, out var expected),
                $"缺少几何资源 {GeometryKeys[descriptor.Icon]}");
            Assert.Same(expected, path.Data);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
