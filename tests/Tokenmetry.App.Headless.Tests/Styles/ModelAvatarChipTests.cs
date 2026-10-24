using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.ViewModels;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Styles;

/// <summary>
/// 模型自定义头像（图标选择器四模式）的芯片渲染守卫（Q-012，机制同 ModelLogoTests）：
/// 1. Vendor 模式走既有 model-* 伪类（logo / Φ / circle-help 兜底，覆盖在 ModelLogoTests）；
/// 2. Letter 模式：model-letter 伪类点亮字形层，厂商 logo 与兜底层全隐；
/// 3. Color 模式：芯片背景由 ModelChip 就地着色（用户固定 hex，内容非主题），数据层字形不显；
/// 4. Image 模式：model-image 伪类点亮 model-avatar 图层；
/// 5. null 视觉：全部伪类清空（诚实回退，不残留上一行头像）。
/// </summary>
public class ModelAvatarChipTests
{
    private static (Border Chip, TextBlock Glyph, Image Logo, Image Avatar, Window Window) CreateChip()
    {
        var glyph = new TextBlock();
        var logo = new Image { Classes = { "model-logo" } };
        var avatar = new Image { Classes = { "model-avatar" } };
        var border = new Border
        {
            Classes = { "model-chip" },
            Child = new Panel { Children = { glyph, logo, avatar } },
        };
        var window = new Window { Width = 200, Height = 100, Content = border };
        window.Show(); // 挂入视觉树触发样式应用
        Dispatcher.UIThread.RunJobs();
        return (border, glyph, logo, avatar, window);
    }

    [AvaloniaFact]
    public void LetterAvatar_ShowsGlyphOnly()
    {
        var (chip, glyph, logo, avatar, window) = CreateChip();
        try
        {
            ModelChip.SetAvatar(chip, ModelAvatarVisual.FromLetter("G"));
            Dispatcher.UIThread.RunJobs();

            Assert.True(glyph.IsVisible, "letter 模式点亮字形层（字形文本由视图绑定 Avatar.GlyphText 注入）");
            Assert.False(logo.IsVisible, "letter 模式厂商 logo 不该出现");
            Assert.False(avatar.IsVisible, "letter 模式图片层不该出现");
            Assert.Contains("model-letter", chip.Classes);
            Assert.DoesNotContain("model-claude", chip.Classes);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ColorAvatar_AppliesUserColorAsChipBackground()
    {
        var (chip, glyph, logo, avatar, window) = CreateChip();
        try
        {
            ModelChip.SetAvatar(chip, ModelAvatarVisual.FromColorHex("#FF5500"));
            Dispatcher.UIThread.RunJobs();

            var background = Assert.IsType<SolidColorBrush>(chip.Background);
            Assert.Equal(Color.Parse("#FF5500"), background.Color);
            Assert.Contains("model-color", chip.Classes);
            Assert.False(glyph.IsVisible, "色块模式字形不显（色块即头像）");
            Assert.False(logo.IsVisible);
            Assert.False(avatar.IsVisible);

            // 切回 letter：本属性着过的背景被清除（本地值撤销后样式值重新生效）
            ModelChip.SetAvatar(chip, ModelAvatarVisual.FromLetter("G"));
            Dispatcher.UIThread.RunJobs();
            Assert.False(chip.IsSet(Border.BackgroundProperty), "非色块模式不残留本地背景");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ImageAvatar_ShowsAvatarLayer()
    {
        var (chip, glyph, logo, avatar, window) = CreateChip();
        try
        {
            ModelChip.SetAvatar(chip, ModelAvatarVisual.FromImageData("data:image/png;base64,aGVsbG8="));
            Dispatcher.UIThread.RunJobs();

            Assert.True(avatar.IsVisible, "image 模式点亮 model-avatar 层");
            Assert.False(glyph.IsVisible);
            Assert.False(logo.IsVisible, "image 模式厂商 logo 不该出现");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void NullAvatar_ClearsAllVendorPseudoClasses()
    {
        var (chip, glyph, logo, avatar, window) = CreateChip();
        try
        {
            ModelChip.SetAvatar(chip, ModelAvatarVisual.FromVendor("claude"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(logo.IsVisible);

            ModelChip.SetAvatar(chip, null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(logo.IsVisible, "null 视觉清空全部伪类（诚实回退）");
            Assert.False(glyph.IsVisible);
            Assert.False(avatar.IsVisible);
            Assert.DoesNotContain("model-claude", chip.Classes);
            Assert.DoesNotContain("model-letter", chip.Classes);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DataUriImageConverter_DecodesAndCaches()
    {
        // 1×1 红点 PNG 运行时生成（WriteableBitmap 帧缓冲直写——RTB 绘制在 Avalonia 12 不确定）
        using var target = new WriteableBitmap(
            new PixelSize(1, 1), new Vector(96, 96), Avalonia.Platform.PixelFormats.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var frame = target.Lock())
        {
            var pixel = new byte[] { 0, 0, 255, 255 }; // BGRA 红
            System.Runtime.InteropServices.Marshal.Copy(pixel, 0, frame.Address, pixel.Length);
        }

        var pngStream = new System.IO.MemoryStream();
        target.Save(pngStream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(pngStream.ToArray());

        var first = Assert.IsType<Avalonia.Media.Imaging.Bitmap>(DataUriImageConverter.Instance.Convert(dataUri, typeof(Avalonia.Media.IImage), null, null!));
        Assert.Equal(new PixelSize(1, 1), first.PixelSize);

        var second = DataUriImageConverter.Instance.Convert(dataUri, typeof(Avalonia.Media.IImage), null, null!);
        Assert.Same(first, second); // 同一 data URI 缓存复用（多槽位只解码一次）

        Assert.Null(DataUriImageConverter.Instance.Convert("data:text/html;base64,aGVsbG8=", typeof(Avalonia.Media.IImage), null, null!));
        Assert.Null(DataUriImageConverter.Instance.Convert(null, typeof(Avalonia.Media.IImage), null, null!));
    }
}
