using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Tokenmetry.App.Controls;
using Tokenmetry.App.ViewModels;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Styles;

/// <summary>
/// I-031 模型厂商 logo（lobe-icons 静态 SVG 生成的 AppModelLogo* DrawingImage）守卫：
/// 1. Vendors 里每个厂商要么有 logo 资源（明暗两变体都可解析为 DrawingImage），
///    要么在字形兜底名单里——新增厂商漏配会被点名；
/// 2. 兜底厂商（phi/generic）没有 logo 资源——生成器名单与兜底语义不漂移；
/// 3. 单色官方标（openai/grok）与内联几何（glm）Light=黑 / Dark=白 真实翻转；
/// 4. model-chip 样式在窗口内真实应用：有 logo 厂商显示 Image 且 Source 指向资源，
///    兜底厂商显示字形。
/// </summary>
public class ModelLogoTests
{
    /// <summary>vendor → 资源键。与 scripts/generate-brand-logos.py 的 MODEL_PASCAL 同步：
    /// 新增厂商必须同时更新生成器、ModelLogos.axaml（重跑脚本）与本表。</summary>
    internal static readonly Dictionary<string, string> LogoKeys = new()
    {
        ["claude"] = "AppModelLogoClaude",
        ["openai"] = "AppModelLogoOpenAI",
        ["gemini"] = "AppModelLogoGemini",
        ["glm"] = "AppModelLogoGlm",
        ["deepseek"] = "AppModelLogoDeepSeek",
        ["qwen"] = "AppModelLogoQwen",
        ["kimi"] = "AppModelLogoKimi",
        ["llama"] = "AppModelLogoLlama",
        ["grok"] = "AppModelLogoGrok",
        ["mistral"] = "AppModelLogoMistral",
        ["doubao"] = "AppModelLogoDoubao",
        ["minimax"] = "AppModelLogoMiniMax",
        ["cohere"] = "AppModelLogoCohere",
        ["ernie"] = "AppModelLogoErnie",
        ["hunyuan"] = "AppModelLogoHunyuan",
    };

    /// <summary>无 lobe 图标、回退字形的厂商（ModelVisualMap.GlyphOf 兜底语义）→ 理论上不该存在的资源键。</summary>
    private static readonly (string Vendor, string Key)[] GlyphFallbackVendors =
    [
        ("phi", "AppModelLogoPhi"),
        ("generic", "AppModelLogoGeneric"),
    ];

    [AvaloniaFact]
    public void Every_vendor_has_logo_or_glyph_fallback_listed()
    {
        var vendors = ModelVisualMap.Vendors.ToHashSet();

        Assert.Subset(vendors, LogoKeys.Keys.ToHashSet());
        Assert.Subset(vendors, GlyphFallbackVendors.Select(v => v.Vendor).ToHashSet());
        Assert.Equal(vendors.Count, LogoKeys.Count + GlyphFallbackVendors.Length);
    }

    [AvaloniaFact]
    public void Logo_resources_resolve_as_drawing_image_in_light_and_dark()
    {
        var app = Application.Current!;

        foreach (var (vendor, key) in LogoKeys)
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Assert.True(app.TryGetResource(key, variant, out var value),
                    $"{variant} 变体缺少 {vendor} 的 {key}");
                Assert.IsType<DrawingImage>(value);
            }
        }
    }

    [AvaloniaFact]
    public void Glyph_fallback_vendors_have_no_logo_resource()
    {
        var app = Application.Current!;

        foreach (var (_, key) in GlyphFallbackVendors)
        {
            Assert.False(app.TryGetResource(key, ThemeVariant.Light, out _),
                $"{key} 不应存在（{key} 对应厂商走字形兜底）");
            Assert.False(app.TryGetResource(key, ThemeVariant.Dark, out _),
                $"{key} 不应存在（{key} 对应厂商走字形兜底）");
        }
    }

    [AvaloniaFact]
    public void Mono_logos_flip_between_light_and_dark()
    {
        var app = Application.Current!;

        // openai / grok 官方只有单色标：按 lobe 静态 PNG 惯例 Light=黑 / Dark=白
        foreach (var key in new[] { "AppModelLogoOpenAI", "AppModelLogoGrok" })
        {
            Assert.Equal(Colors.Black, MonoLogoColor(app, key, ThemeVariant.Light));
            Assert.Equal(Colors.White, MonoLogoColor(app, key, ThemeVariant.Dark));
        }
    }

    [AvaloniaFact]
    public void Glm_uses_inlined_official_mark_with_mono_flip()
    {
        // 智谱官方新标由生成器内联（lobe-icons 1.95.1 的 zhipu 仍是旧标）：三块 "Z" 形几何，
        // 官方呈现即中性单色 → 走单色策略 Light=黑 / Dark=白（同 openai/grok）。
        // 形状数一并钉住，防回退成 lobe 的旧单 path 蓝标。
        var app = Application.Current!;

        foreach (var (variant, expected) in new[]
                 {
                     (ThemeVariant.Light, Colors.Black),
                     (ThemeVariant.Dark, Colors.White),
                 })
        {
            Assert.True(app.TryGetResource("AppModelLogoGlm", variant, out var value),
                $"{variant} 变体缺少 AppModelLogoGlm");
            var group = Assert.IsType<DrawingGroup>(Assert.IsType<DrawingImage>(value).Drawing);
            Assert.Equal(3, group.Children.Count);
            foreach (var child in group.Children)
            {
                var drawing = Assert.IsType<GeometryDrawing>(child);
                Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(drawing.Brush).Color);
            }
        }
    }

    [AvaloniaFact]
    public void Kimi_uses_mono_on_light_and_color_on_dark()
    {
        // kimi 官方彩标主体为白色 K（暗底设计），浅色底不可读 → Light=单色黑 / Dark=彩色
        var app = Application.Current!;

        Assert.True(app.TryGetResource("AppModelLogoKimi", ThemeVariant.Light, out var lightValue));
        var lightImage = Assert.IsType<DrawingImage>(lightValue);
        var lightGroup = Assert.IsType<DrawingGroup>(lightImage.Drawing);
        Assert.Equal(2, lightGroup.Children.Count); // 单色版：圆片 + K，同黑色
        foreach (var child in lightGroup.Children)
        {
            var drawing = Assert.IsType<GeometryDrawing>(child);
            Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(drawing.Brush).Color);
        }

        Assert.True(app.TryGetResource("AppModelLogoKimi", ThemeVariant.Dark, out var darkValue));
        var darkImage = Assert.IsType<DrawingImage>(darkValue);
        var darkGroup = Assert.IsType<DrawingGroup>(darkImage.Drawing);
        Assert.Equal(2, darkGroup.Children.Count); // 彩色版：蓝底块 + 白色 K
        var blue = Assert.IsType<GeometryDrawing>(darkGroup.Children[0]);
        Assert.Equal(Color.Parse("#1783FF"), Assert.IsAssignableFrom<ISolidColorBrush>(blue.Brush).Color);
    }

    [AvaloniaFact]
    public void Chip_styles_show_logo_for_logo_vendors_and_glyph_for_fallback()
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource("AppModelLogoClaude", ThemeVariant.Light, out var claudeLogo));

        var glyph = new TextBlock();
        var logo = new Image { Classes = { "model-logo" } };
        var border = new Border { Classes = { "model-chip" }, Child = new Panel { Children = { glyph, logo } } };
        var window = new Window { Width = 200, Height = 100, Content = border };
        window.Show(); // 挂入视觉树触发样式应用
        Dispatcher.UIThread.RunJobs();

        ModelChip.SetVendor(border, "claude");
        Dispatcher.UIThread.RunJobs();
        Assert.True(logo.IsVisible, "claude 应显示 logo");
        Assert.Same(claudeLogo, logo.Source);
        Assert.False(glyph.IsVisible, "claude 不应显示兜底字形");

        ModelChip.SetVendor(border, "generic");
        Dispatcher.UIThread.RunJobs();
        Assert.False(logo.IsVisible, "generic 无 logo，Image 应隐藏");
        Assert.True(glyph.IsVisible, "generic 应显示 ◇ 兜底字形");

        ModelChip.SetVendor(border, "phi");
        Dispatcher.UIThread.RunJobs();
        Assert.False(logo.IsVisible, "phi 无 logo，Image 应隐藏");
        Assert.True(glyph.IsVisible, "phi 应显示 Φ 兜底字形");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static Color MonoLogoColor(Application app, string key, ThemeVariant variant)
    {
        Assert.True(app.TryGetResource(key, variant, out var value), $"{variant} 缺少 {key}");
        var image = Assert.IsType<DrawingImage>(value);
        var group = Assert.IsType<DrawingGroup>(image.Drawing);
        var drawing = Assert.IsType<GeometryDrawing>(group.Children[0]);
        return Assert.IsAssignableFrom<ISolidColorBrush>(drawing.Brush).Color;
    }
}
