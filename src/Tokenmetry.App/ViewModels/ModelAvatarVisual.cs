using System;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 模型头像的最终视觉规格（行 VM 持有、<see cref="Controls.ModelChip"/> 附加属性消费）：
/// 把目录条目的 <see cref="ModelDefinition.AvatarKind"/> 四种用户头像模式 + 自动推断
/// 收敛成图标槽各层（厂商 logo / 字母字形 / 色块 / 上传图 / 兜底问号）的渲染依据。
/// 不可变；数据全部来自条目字段与 <see cref="ModelVisualMap"/>，不持画刷 / 位图——
/// 明暗翻转与图片解码分别交给伪类样式（VM 不解析画刷纪律）与 <see cref="DataUriImageConverter"/>。
/// </summary>
/// <param name="Kind">头像方式（数据语义，驱动 model-letter / model-color / model-image 伪类）。</param>
/// <param name="Vendor">厂商伪类键（Auto / Vendor 模式恒非空；自定义三模式为 null——logo 与兜底层全部隐去）。</param>
/// <param name="GlyphText">字形文本（letter 模式的字母 / phi 的 Φ / 其余厂商的数据层兜底字形，
/// 仅 model-letter / model-phi 伪类下可见）。</param>
/// <param name="ColorHex">Color 模式的 #RRGGBB；其他模式 null。</param>
/// <param name="ImageDataUri">Image 模式的 data URI；其他模式 null。</param>
public sealed record ModelAvatarVisual(
    ModelAvatarKind Kind,
    string? Vendor,
    string GlyphText,
    string? ColorHex,
    string? ImageDataUri)
{
    /// <summary>空视觉（预览兜底：无厂商伪类、无字形、无载荷——图标槽各层全隐的空芯片，
    /// 用于"选了模式但载荷未就绪"的诚实空态）。</summary>
    public static ModelAvatarVisual Empty { get; } = new(ModelAvatarKind.Auto, null, string.Empty, null, null);

    /// <summary>目录条目 → 头像视觉（统计页 / 目录行 / 别名目标共用）。definition 为 null（名字没
    /// 关联进目录）或条目头像数据不合法时回落按名字推断——统计行不该没有图标。</summary>
    public static ModelAvatarVisual FromDefinition(ModelDefinition? definition, string fallbackName)
    {
        if (definition is null)
        {
            return FromName(fallbackName);
        }

        var kind = definition.AvatarKind;
        // 各自定义模式的数据不合法（手改 settings.json / 脏值）一律回落自动推断，不做半渲染。
        if (kind == ModelAvatarKind.Image && FromImageData(definition.AvatarImage) is { } imageVisual)
        {
            return imageVisual;
        }

        if (kind == ModelAvatarKind.Color && TryNormalizeColor(definition.AvatarColor, out var colorHex))
        {
            return new ModelAvatarVisual(ModelAvatarKind.Color, null, string.Empty, colorHex, null);
        }

        if (kind == ModelAvatarKind.Letter && NonBlank(definition.AvatarLetter))
        {
            return new ModelAvatarVisual(ModelAvatarKind.Letter, null, definition.AvatarLetter!.Trim(), null, null);
        }

        // Vendor 模式缺载荷、或 Auto 模式带旧 settings.json 手写 LogoVendor——都按显式厂商呈现。
        if ((kind is ModelAvatarKind.Vendor or ModelAvatarKind.Auto) && NonBlank(definition.LogoVendor))
        {
            return FromVendor(definition.LogoVendor);
        }

        return FromName(fallbackName);
    }

    /// <summary>名字推断（自动模式的兜底口径，同既有 ModelVisualMap.Resolve；未识别落 generic）。</summary>
    public static ModelAvatarVisual FromName(string name)
    {
        var visual = ModelVisualMap.Resolve(name);
        return new ModelAvatarVisual(ModelAvatarKind.Auto, visual.Vendor, visual.Glyph.ToString(), null, null);
    }

    /// <summary>厂商变体名 → Vendor 模式视觉（图标选择器格子与 Vendor 头像共用同一口径）。</summary>
    public static ModelAvatarVisual FromVendor(string? vendorKey)
    {
        var visual = ModelVisualMap.ForVendor(vendorKey);
        return new ModelAvatarVisual(ModelAvatarKind.Vendor, visual.Vendor, visual.Glyph.ToString(), null, null);
    }

    /// <summary>字母模式视觉（图标选择器预览与表单预览共用；字母取首个字符，空串无意义由调用方拦）。</summary>
    public static ModelAvatarVisual FromLetter(string letter) =>
        new(ModelAvatarKind.Letter, null, letter.Length == 0 ? string.Empty : letter[..1], null, null);

    /// <summary>色块模式视觉；hex 不合法返回 null（调用方回落，不让脏色进渲染链）。</summary>
    public static ModelAvatarVisual? FromColorHex(string? hex) =>
        TryNormalizeColor(hex, out var normalized)
            ? new ModelAvatarVisual(ModelAvatarKind.Color, null, string.Empty, normalized, null)
            : null;

    /// <summary>图片模式视觉；data URI 形如 data:image/* 才收（Base64 合法性交给渲染层解码失败兜底）。</summary>
    public static ModelAvatarVisual? FromImageData(string? dataUri) =>
        NonBlank(dataUri) && dataUri!.StartsWith("data:image/", StringComparison.Ordinal)
            ? new ModelAvatarVisual(ModelAvatarKind.Image, null, string.Empty, null, dataUri.Trim())
            : null;

    private static bool NonBlank(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>hex 颜色校验 + 规范化（#RRGGBB 大写；#AARRGGBB 输入剥 alpha——头像不需要透明度）。
    /// 走 UI 层 Color 解析（App 项目）；解析失败 = 脏值，返回 false。</summary>
    internal static bool TryNormalizeColor(string? hex, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        try
        {
            var color = Avalonia.Media.Color.Parse(hex.Trim());
            normalized = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
