using Tokenmetry.Core.Models;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Tests.ViewModels;

/// <summary>
/// 模型头像视觉收敛（ModelAvatarVisual）：目录条目四模式 → 图标槽渲染依据；数据不合法回落
/// 名推断（统计行不该没有图标）；旧手写 LogoVendor 的 Auto 兼容；Vendor 直指（phi 字形等
/// 名字推断会漏的变体）；颜色规范化。
/// </summary>
public class ModelAvatarVisualTests
{
    [Fact]
    public void FromDefinition_NullDefinition_FallsBackToNameInference()
    {
        var visual = ModelAvatarVisual.FromDefinition(null, "claude-sonnet-5");

        visual.Kind.Should().Be(ModelAvatarKind.Auto);
        visual.Vendor.Should().Be("claude");
    }

    [Fact]
    public void FromDefinition_LetterKind_CarriesLetter()
    {
        var definition = new ModelDefinition { Id = "my-model", AvatarKind = ModelAvatarKind.Letter, AvatarLetter = "G" };

        var visual = ModelAvatarVisual.FromDefinition(definition, definition.Id);

        visual.Kind.Should().Be(ModelAvatarKind.Letter);
        visual.Vendor.Should().BeNull("自定义模式下厂商伪类全清（logo / 兜底不该出现）");
        visual.GlyphText.Should().Be("G");
        visual.ColorHex.Should().BeNull();
        visual.ImageDataUri.Should().BeNull();
    }

    [Fact]
    public void FromDefinition_ColorKind_NormalizesHexAndDropsAlpha()
    {
        var definition = new ModelDefinition { Id = "my-model", AvatarKind = ModelAvatarKind.Color, AvatarColor = "#ff5500" };

        var visual = ModelAvatarVisual.FromDefinition(definition, definition.Id);

        visual.Kind.Should().Be(ModelAvatarKind.Color);
        visual.ColorHex.Should().Be("#FF5500");
        visual.Vendor.Should().BeNull();
    }

    [Fact]
    public void FromDefinition_InvalidPayload_FallsBackToNameInference()
    {
        var letter = new ModelDefinition { Id = "claude-sonnet-5", AvatarKind = ModelAvatarKind.Letter, AvatarLetter = "  " };
        ModelAvatarVisual.FromDefinition(letter, letter.Id).Vendor.Should().Be("claude", "空字母 = 脏值，回落名推断");

        var color = new ModelDefinition { Id = "claude-sonnet-5", AvatarKind = ModelAvatarKind.Color, AvatarColor = "not-a-color" };
        ModelAvatarVisual.FromDefinition(color, color.Id).Kind.Should().Be(ModelAvatarKind.Auto, "脏色值不做半渲染");

        var image = new ModelDefinition { Id = "claude-sonnet-5", AvatarKind = ModelAvatarKind.Image, AvatarImage = "http://x/y.png" };
        ModelAvatarVisual.FromDefinition(image, image.Id).Vendor.Should().Be("claude", "非 data:image 的脏值回落名推断");
    }

    [Fact]
    public void FromDefinition_AutoWithLegacyLogoVendor_PresentsVendorMode()
    {
        // 旧 settings.json 手写 LogoVendor：装订层保留 Auto + LogoVendor，呈现层兼容为 Vendor。
        var definition = new ModelDefinition { Id = "my-custom-model", LogoVendor = "qwen" };

        var visual = ModelAvatarVisual.FromDefinition(definition, definition.Id);

        visual.Kind.Should().Be(ModelAvatarKind.Vendor);
        visual.Vendor.Should().Be("qwen");
    }

    [Fact]
    public void FromVendor_DirectKeyLookup_HandlesGlyphFallbackVendors()
    {
        var phi = ModelAvatarVisual.FromVendor("phi");
        phi.Vendor.Should().Be("phi");
        phi.GlyphText.Should().Be("Φ", "裸 \"phi\" 经名字推断会落 generic，直指变体名必须拿到 Φ 字形");

        var unknown = ModelAvatarVisual.FromVendor("not-a-vendor");
        unknown.Vendor.Should().Be("generic", "名单外变体（含手写脏值）落 generic 兜底");

        var casing = ModelAvatarVisual.FromVendor("DeepSeek");
        casing.Vendor.Should().Be("deepseek", "大小写变体收敛到规范名");
    }

    [Fact]
    public void FromLetter_TakesFirstCharacterOnly()
    {
        ModelAvatarVisual.FromLetter("豆").GlyphText.Should().Be("豆");
        ModelAvatarVisual.FromLetter(string.Empty).GlyphText.Should().BeEmpty();
    }

    [Fact]
    public void FromImageData_AcceptsDataUriOnly()
    {
        var good = ModelAvatarVisual.FromImageData("data:image/png;base64,aGVsbG8=");
        good.Should().NotBeNull();
        good!.Kind.Should().Be(ModelAvatarKind.Image);

        ModelAvatarVisual.FromImageData("data:text/html;base64,aGVsbG8=").Should().BeNull();
        ModelAvatarVisual.FromImageData(null).Should().BeNull();
    }

    [Fact]
    public void TryNormalizeColor_UppercaseCanonicalForm()
    {
        ModelAvatarVisual.TryNormalizeColor("#12b886", out var normalized).Should().BeTrue();
        normalized.Should().Be("#12B886");

        ModelAvatarVisual.TryNormalizeColor("#FF12B886", out var alphaStripped).Should().BeTrue("#AARRGGBB 输入剥 alpha");
        alphaStripped.Should().Be("#12B886");

        ModelAvatarVisual.TryNormalizeColor("   ", out _).Should().BeFalse();
    }
}
