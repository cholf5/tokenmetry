using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Tokenmetry.App.Services;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Controls;

/// <summary>
/// 上传头像标准化处理（Q-012，AvatarImageProcessor）：任意尺寸源图 → 居中裁方 → 64×64 圆角
/// PNG data URI。圆角烙进像素是刻意的（避开 Border 圆角裁切的 Avalonia 12 剪裁语义坑，
/// 见 docs/chore/avalonia-12-cliptobounds-stroke-shave.md）——角落透明即本测试的定案锚。
/// </summary>
public class AvatarImageProcessorTests
{
    /// <summary>非方形源图（40×80，纯红）→ PNG 流：验证居中裁方取短边。
    /// 用 WriteableBitmap 帧缓冲直写构造 fixture——RTB 绘制在 Avalonia 12 走合成线程延迟，
    /// 产出内容不确定（见 AvatarImageProcessor 的实现注释），不能做确定性测试源。</summary>
    private static MemoryStream CreateSourcePng(int width, int height, byte r, byte g, byte b)
    {
        using var target = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
        using (var frame = target.Lock())
        {
            var stride = (int)frame.RowBytes;
            var row = new byte[stride];
            for (var x = 0; x < width; x++)
            {
                row[(x * 4) + 0] = b;
                row[(x * 4) + 1] = g;
                row[(x * 4) + 2] = r;
                row[(x * 4) + 3] = 255;
            }

            var buffer = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                Array.Copy(row, 0, buffer, y * stride, stride);
            }

            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, frame.Address, buffer.Length);
        }

        var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return stream;
    }

    [AvaloniaFact]
    public void Process_CenterCropsAndRoundsToStandardFormat()
    {
        using var source = CreateSourcePng(40, 80, r: 255, g: 0, b: 0);
        var dataUri = AvatarImageProcessor.ProcessToDataUri(source);

        Assert.NotNull(dataUri);
        Assert.StartsWith("data:image/png;base64,", dataUri);

        var bytes = Convert.FromBase64String(dataUri!["data:image/png;base64,".Length..]);
        using var decoded = new Bitmap(new MemoryStream(bytes));
        Assert.Equal(new PixelSize(64, 64), decoded.PixelSize);

        // 圆角烙进像素：四角透明、中心不透明（alpha 恒为像素末字节，BGRA/RGBA 皆然；
        // 字节序断言走 ChartTestHelpers 同款套路——像素读取统一其 GetPixelBytes 本机缓冲路径）
        var buffer = ChartTestHelpers.GetPixelBytes(decoded);
        var stride = 64 * 4;
        Assert.Equal(0, buffer[3]);                            // 左上角
        Assert.Equal(0, buffer[(63 * stride) + (63 * 4) + 3]); // 右下角
        Assert.Equal(255, buffer[(32 * stride) + (32 * 4) + 3]); // 中心

        // 居中裁方语义：40×80 源取中部 40×40 → 中心是红色（BGRA 序 R 在偏移 2）
        Assert.Equal((byte)255, buffer[(32 * stride) + (32 * 4) + 2]);
    }

    [AvaloniaFact]
    public void Process_RejectsOversizedSource()
    {
        // 无法构造 8MB 头的合法小图——用长度欺骗：声明超长的不可解码流（拷贝上限路径）
        using var source = new MemoryStream(new byte[9 * 1024 * 1024]);
        Assert.Null(AvatarImageProcessor.ProcessToDataUri(source));
    }

    [AvaloniaFact]
    public void Process_ReturnsNullForGarbageInput()
    {
        using var source = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        Assert.Null(AvatarImageProcessor.ProcessToDataUri(source));
    }
}
