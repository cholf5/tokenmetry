using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.ViewModels;
using Xunit;
// System.IO.Path（隐式 using）与 Avalonia 的 Path 撞名，测试里一律用别名
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using AvaloniaPathIcon = Avalonia.Controls.PathIcon;

namespace Tokenmetry.App.Headless.Tests.Styles;

/// <summary>
/// 界面矢量图标（2026-10-24 矢量图标化：Icons.axaml 的 AppIcon* StreamGeometry +
/// SharedStyles 的 Path.app-icon）守卫：
/// 1. ShellPages 九页的图标键名全部能解析到几何资源（生成脚本名单 / ShellPages / 本表三方不漂移）；
/// 2. 几何资源两主题同实例（与明暗无关——图标颜色由描边画刷翻转）；
/// 3. Path.app-icon 统一描边样式真实应用（Stretch=Uniform / 1.5 粗细 / AppTextBrush 默认色）；
/// 4. PageIcon.Icon 附加属性把键名翻译成 pi-* 伪类 → Data 指向对应几何资源；
/// 5. 几何墨迹全部落在 24×24 设计框内（子路径拼接漂移护栏，见用例注释）；
/// 6. 描边外半环不被 PathIcon 边界削掉（Avalonia 12 ClipToBounds 默认翻转护栏，见用例注释）。
/// </summary>
public class IconResourceTests
{
    /// <summary>图标键名（ShellPages.Icon 取值或使用处 StaticResource）→ 资源键。与
    /// scripts/generate-ui-icons.py 的 ICONS 表同步：名单变更必须同时更新生成器、
    /// Icons.axaml（重跑脚本）与本表。</summary>
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
        ["hard-drive-upload"] = "AppIconHardDriveUpload",
        ["hard-drive-download"] = "AppIconHardDriveDownload",
        ["circle-help"] = "AppIconCircleHelp",
        ["x"] = "AppIconClose",
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
    public void App_icon_template_applies_stroke_and_centers_ink()
    {
        // 居中回归锁（2026-10-24 zap/$ 真机目检实锤）：Avalonia 的 Shape.Stretch 只缩放适配并
        // 钉在元素左上角（CalculateSizeAndTransform 无居中项，与 WPF 不同），裸 Path 的墨迹在
        // 槽内偏移 (槽−墨)/2。修复 = PathIcon 隐式 ControlTheme（Icons.axaml）：内层 Path 自适应
        // 墨迹尺寸 + Center 对齐 → 布局居中。本断言锁定：任意图标墨迹中心 = 槽位中心。
        var app = Application.Current!;
        foreach (var key in new[] { "zap", "house", "loader-circle", "coins" })
        {
            Assert.True(app.TryGetResource(GeometryKeys[key], ThemeVariant.Default, out var geometry));
            var icon = new AvaloniaPathIcon
            {
                Classes = { "app-icon" },
                Width = 15,
                Height = 15,
                Data = (Geometry)geometry!,
                Foreground = Brushes.Red,
            };
            var window = new Window { Content = new Border { Width = 30, Height = 30, Child = icon } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var inner = Assert.IsType<AvaloniaPath>(icon.GetVisualDescendants().First(c => c is AvaloniaPath));
            Assert.Equal(Stretch.Uniform, inner.Stretch);
            Assert.Equal(1.5, inner.StrokeThickness);
            Assert.Same(icon.Foreground, inner.Stroke); // 模板 TemplateBinding Foreground → 状态色走继承

            // 墨迹中心（RenderedGeometry 含 Stretch 变换，坐标在内层 Path 空间）换算到槽位坐标
            var inkCenter = inner.RenderedGeometry!.Bounds.Center;
            var inSlot = inner.TranslatePoint(inkCenter, icon)!.Value;
            Assert.InRange(inSlot.X, 7.5 - 0.75, 7.5 + 0.75);
            Assert.InRange(inSlot.Y, 7.5 - 0.75, 7.5 + 0.75);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Stroke_bleed_escapes_path_icon_bounds()
    {
        // Avalonia 12 起 TemplatedControl（IconElement/PathIcon 继承）把 ClipToBounds 元数据
        // 默认翻成 true：模板内 Path 按几何 bounds 缩放填槽，居中描边的外半环（0.75px）溢出
        // PathIcon 边界即被削——大圆类图标（clock/dollar-circle/database/house）左右弧读成直边
        // （真机目检实锤；headless 像素量测复现：16px 槽 clock 墨迹盒恰 16×16、赤道整行贴边，
        // 同几何裸 Path 墨迹盒 18×18 圆弧收尾）。修复 = ControlTheme 显式 ClipToBounds=False
        // （Icons.axaml 随生成器再生）。双断言：主题应用后属性为 false；渲染墨迹盒宽高必须
        // 大于几何盒 16×16（描边外半环真实出墨——被削时墨迹盒恒等于几何盒）。
        var app = Application.Current!;
        Assert.True(app.TryGetResource(GeometryKeys["clock"], ThemeVariant.Default, out var geometry));
        var icon = new AvaloniaPathIcon
        {
            Classes = { "app-icon" },
            Width = 16,
            Height = 16,
            Data = (Geometry)geometry!,
            Foreground = Brushes.Red,
        };
        var window = new Window
        {
            Width = 32,
            Height = 32,
            Content = new Border { Width = 32, Height = 32, Background = Brushes.White, Child = icon },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(icon.ClipToBounds, "ControlTheme 的 ClipToBounds=False 未生效，描边外半环会被削掉");

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var (minX, maxX, minY, maxY) = MeasureInkBox(frame!);
        Assert.True(maxX - minX + 1 >= 17,
            $"墨迹盒宽 {maxX - minX + 1}（<17）：描边外半环被 PathIcon 边界削掉");
        Assert.True(maxY - minY + 1 >= 17,
            $"墨迹盒高 {maxY - minY + 1}（<17）：描边外半环被 PathIcon 边界削掉");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>帧内红墨迹（Foreground=Red）的像素包围盒。供削边回归用例量测。</summary>
    private static (int MinX, int MaxX, int MinY, int MaxY) MeasureInkBox(Avalonia.Media.Imaging.Bitmap frame)
    {
        var size = frame.PixelSize;
        var stride = size.Width * 4;
        var native = Marshal.AllocHGlobal(stride * size.Height);
        try
        {
            frame.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), native, stride * size.Height, stride);
            var pixels = new byte[stride * size.Height];
            Marshal.Copy(native, pixels, 0, pixels.Length);

            int? minX = null, maxX = null, minY = null, maxY = null;
            for (var y = 0; y < size.Height; y++)
            {
                for (var x = 0; x < size.Width; x++)
                {
                    // R/B 两通道一低一高判红墨迹（BGRA/RGBA 字节序都命中；白底 AA 像素同样命中）
                    var offset = y * stride + x * 4;
                    if ((pixels[offset] > 120 && pixels[offset + 2] < 120)
                        || (pixels[offset] < 120 && pixels[offset + 2] > 120))
                    {
                        minX = minX is null || x < minX ? x : minX;
                        maxX = maxX is null || x > maxX ? x : maxX;
                        minY = minY is null || y < minY ? y : minY;
                        maxY = maxY is null || y > maxY ? y : maxY;
                    }
                }
            }

            Assert.True(minX is not null, "帧内没有红墨迹：图标未渲染");
            return (minX!.Value, maxX!.Value, minY!.Value, maxY!.Value);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    [AvaloniaFact]
    public void PageIcon_attached_property_resolves_data_per_page()
    {
        var app = Application.Current!;
        foreach (var descriptor in ShellPages.All)
        {
            var icon = new AvaloniaPathIcon { Classes = { "app-icon" } };
            PageIcon.SetIcon(icon, descriptor.Icon);
            var window = new Window { Content = icon };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.True(icon.Classes.Contains($"pi-{descriptor.Icon}"),
                $"页面 {descriptor.Id}：pi-{descriptor.Icon} 伪类未挂上");
            Assert.True(app.TryGetResource(GeometryKeys[descriptor.Icon], window.ActualThemeVariant, out var expected),
                $"缺少几何资源 {GeometryKeys[descriptor.Icon]}");
            Assert.Same(expected, icon.Data);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
