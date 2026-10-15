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
/// I-034 Agent 品牌 logo（lobe-icons 静态 SVG 生成的 AppAgentLogo* DrawingImage）守卫，
/// 与 <see cref="ModelLogoTests"/> 同构：
/// 1. AgentVisualMap.Vendors 里每个变体要么有 logo 资源（明暗两变体都可解析为 DrawingImage），
///    要么在字形兜底名单里——新增 Agent 漏配会被点名；
/// 2. 兜底变体（generic）没有 logo 资源——生成器名单与兜底语义不漂移；
/// 3. 单色官方标（goose/opencode/kilo/hermes/grok/antigravity/copilot/zai/dsh）Light=黑 / Dark=白 真实翻转；
/// 4. agent-chip / agent-chip-lg 样式在窗口内真实应用：有 logo 变体显示 Image 且 Source 指向资源，
///    generic 显示散列兜底字形。
/// </summary>
public class AgentLogoTests
{
    /// <summary>变体名 → 资源键。与 scripts/generate-brand-logos.py 的 AGENT_PASCAL 同步：
    /// 新增 Agent 必须同时更新生成器、AgentLogos.axaml（重跑脚本）与本表。</summary>
    internal static readonly Dictionary<string, string> LogoKeys = new()
    {
        ["claudecode"] = "AppAgentLogoClaudeCode",
        ["codex"] = "AppAgentLogoCodex",
        ["geminicli"] = "AppAgentLogoGeminiCli",
        ["amp"] = "AppAgentLogoAmp",
        ["openclaw"] = "AppAgentLogoOpenClaw",
        ["qwen"] = "AppAgentLogoQwen",
        ["kimi"] = "AppAgentLogoKimi",
        ["goose"] = "AppAgentLogoGoose",
        ["opencode"] = "AppAgentLogoOpenCode",
        ["kilo"] = "AppAgentLogoKilo",
        ["hermes"] = "AppAgentLogoHermes",
        ["grok"] = "AppAgentLogoGrok",
        ["antigravity"] = "AppAgentLogoAntigravity",
        ["copilot"] = "AppAgentLogoCopilot",
        ["zai"] = "AppAgentLogoZai",
        ["dsh"] = "AppAgentLogoDsh",
    };

    /// <summary>无 lobe 图标、回退散列字形的变体（AgentVisualMap 兜底语义）→ 理论上不该存在的资源键。</summary>
    private static readonly (string Vendor, string Key)[] GlyphFallbackVendors =
    [
        ("generic", "AppAgentLogoGeneric"),
    ];

    [AvaloniaFact]
    public void Every_vendor_has_logo_or_glyph_fallback_listed()
    {
        var vendors = AgentVisualMap.Vendors.ToHashSet();

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
                $"{key} 不应存在（对应 Agent 走字形兜底）");
            Assert.False(app.TryGetResource(key, ThemeVariant.Dark, out _),
                $"{key} 不应存在（对应 Agent 走字形兜底）");
        }
    }

    [AvaloniaFact]
    public void Mono_logos_flip_between_light_and_dark()
    {
        var app = Application.Current!;

        // 官方标只有单色形态的 Agent：按 lobe 静态 PNG 惯例 Light=黑 / Dark=白。
        // dsh 复用 DeepSeek 几何（官方呈现为黑色），同走该策略。
        foreach (var key in new[]
                 {
                     "AppAgentLogoGoose", "AppAgentLogoOpenCode", "AppAgentLogoKilo",
                     "AppAgentLogoHermes", "AppAgentLogoGrok", "AppAgentLogoAntigravity",
                     "AppAgentLogoCopilot", "AppAgentLogoZai", "AppAgentLogoDsh",
                 })
        {
            Assert.Equal(Colors.Black, MonoLogoColor(app, key, ThemeVariant.Light));
            Assert.Equal(Colors.White, MonoLogoColor(app, key, ThemeVariant.Dark));
        }
    }

    [AvaloniaFact]
    public void Kimi_uses_mono_on_light_and_color_on_dark()
    {
        // kimi 官方彩标主体为白色 K（暗底设计），浅色底不可读 → Light=单色黑 / Dark=彩色（同模型表）
        var app = Application.Current!;

        Assert.True(app.TryGetResource("AppAgentLogoKimi", ThemeVariant.Light, out var lightValue));
        var lightGroup = Assert.IsType<DrawingGroup>(Assert.IsType<DrawingImage>(lightValue).Drawing);
        Assert.Equal(2, lightGroup.Children.Count);
        foreach (var child in lightGroup.Children)
        {
            var drawing = Assert.IsType<GeometryDrawing>(child);
            Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(drawing.Brush).Color);
        }

        Assert.True(app.TryGetResource("AppAgentLogoKimi", ThemeVariant.Dark, out var darkValue));
        var darkGroup = Assert.IsType<DrawingGroup>(Assert.IsType<DrawingImage>(darkValue).Drawing);
        var blue = Assert.IsType<GeometryDrawing>(darkGroup.Children[0]);
        Assert.Equal(Color.Parse("#1783FF"), Assert.IsAssignableFrom<ISolidColorBrush>(blue.Brush).Color);
    }

    [AvaloniaFact]
    public void Color_logos_share_one_instance_across_light_and_dark()
    {
        // 有彩色形态的官方标明暗同用（AgentLogos.axaml 字典级普通资源），与单色标的双实例相对
        var app = Application.Current!;

        Assert.True(app.TryGetResource("AppAgentLogoClaudeCode", ThemeVariant.Light, out var lightValue));
        Assert.True(app.TryGetResource("AppAgentLogoClaudeCode", ThemeVariant.Dark, out var darkValue));
        Assert.Same(lightValue, darkValue);
        var drawing = Assert.IsType<GeometryDrawing>(
            Assert.IsType<DrawingGroup>(Assert.IsType<DrawingImage>(lightValue).Drawing).Children[0]);
        Assert.Equal(Color.Parse("#D97757"), Assert.IsAssignableFrom<ISolidColorBrush>(drawing.Brush).Color);
    }

    [AvaloniaTheory]
    [InlineData("agent-chip")]
    [InlineData("agent-chip-lg")]
    public void Chip_styles_show_logo_for_logo_vendors_and_glyph_for_fallback(string slotClass)
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource("AppAgentLogoClaudeCode", ThemeVariant.Light, out var claudeLogo));

        var glyph = new TextBlock();
        var logo = new Image { Classes = { "agent-logo" } };
        var border = new Border { Classes = { slotClass }, Child = new Panel { Children = { glyph, logo } } };
        var window = new Window { Width = 200, Height = 100, Content = border };
        window.Show(); // 挂入视觉树触发样式应用
        Dispatcher.UIThread.RunJobs();

        AgentChip.SetLogo(border, "claudecode");
        Dispatcher.UIThread.RunJobs();
        Assert.True(logo.IsVisible, "claudecode 应显示 logo");
        Assert.Same(claudeLogo, logo.Source);
        Assert.False(glyph.IsVisible, "claudecode 不应显示兜底字形");

        AgentChip.SetLogo(border, "dsh");
        Dispatcher.UIThread.RunJobs();
        Assert.True(logo.IsVisible, "dsh 应显示 logo（DeepSeek 同形状标）");
        Assert.False(glyph.IsVisible, "dsh 不应显示兜底字形");

        AgentChip.SetLogo(border, "generic");
        Dispatcher.UIThread.RunJobs();
        Assert.False(logo.IsVisible, "generic 无 logo，Image 应隐藏");
        Assert.True(glyph.IsVisible, "generic 应显示散列兜底字形");

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
