using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Tokenmetry.App.Services;

/// <summary>
/// 上传头像的标准化处理（图标选择器「图片」模式）：任意来源位图 → 居中裁方 → 面积均值缩放到
/// <see cref="OutputSize"/>×64 → 圆角 alpha 蒙版 → PNG data URI（data:image/png;base64,...）。
/// 产出同时用于落 settings.json 与预览——用户所见即所存。隐私红线：只处理用户显式选择的
/// 单张图片文件，不碰任何会话内容。
/// <para>
/// 实现刻意绕开 <see cref="Avalonia.Media.Imaging.RenderTargetBitmap"/>：Avalonia 12 的 RTB
/// 绘制走 FramebufferRenderTarget（合成渲染器，绘制命令延迟到渲染线程），Save 时内容是否已
/// 落帧是不确定的（headless 一次实锤：同一管线时对时错）。本处理全程 CPU 像素运算——
/// 解码（Skia 同步）→ CopyPixels 读回 → 纯 C# 重采样与圆角蒙版 → WriteableBitmap 帧缓冲
/// 直写 → Save 同步编码，任何环境确定性一致。
/// </para>
/// </summary>
public static class AvatarImageProcessor
{
    /// <summary>标准输出边长（像素）：30px 弹层槽的 2x（HiDPI 下仍清晰），PNG 编码后通常仅数 KB。</summary>
    public const int OutputSize = 64;

    /// <summary>烙进 PNG 的圆角半径（64px 尺度；≈行内槽 4/16 比例与弹层槽 9/30 比例的折中）。</summary>
    internal const double CornerRadius = 18;

    /// <summary>源文件字节上限：超限直接拒绝（不试图解码超大图，避免内存尖峰）。</summary>
    private const long MaxSourceBytes = 8 * 1024 * 1024;

    /// <summary>解码上限宽（居中裁方前的中间解码宽度；再大对 64px 输出没有意义）。</summary>
    private const int DecodeWidth = 256;

    /// <summary>
    /// 源图片流 → 标准化 data URI；任何一步失败（超大 / 非图片 / 解码异常）返回 null，
    /// 由调用方给出「图片解析失败」提示，不做半成品。
    /// </summary>
    public static string? ProcessToDataUri(Stream source)
    {
        try
        {
            if (source.CanSeek && source.Length > MaxSourceBytes)
            {
                return null;
            }

            // 复制到带字节上限的内存流：文件流 seek 不定长，统一先验总量再交给解码器。
            using var buffered = new MemoryStream();
            var copied = 0L;
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                copied += read;
                if (copied > MaxSourceBytes)
                {
                    return null;
                }

                buffered.Write(chunk, 0, read);
            }

            buffered.Position = 0;
            using var decoded = Bitmap.DecodeToWidth(buffered, DecodeWidth, BitmapInterpolationMode.HighQuality);
            var sourceWidth = decoded.PixelSize.Width;
            var sourceHeight = decoded.PixelSize.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return null;
            }

            var pixels = ReadPixels(decoded, sourceWidth, sourceHeight);
            var isPremul = decoded.AlphaFormat is not AlphaFormat.Unpremul; // Skia 原生位图默认预乘

            // 居中裁方（取短边）+ 面积均值缩放：每输出像素对源裁方内的对应方块求均值（含 alpha），
            // 任意源比例都得到干净的降采样，不做单点采样抖动。
            var sourceStride = sourceWidth * 4;
            var side = Math.Min(sourceWidth, sourceHeight);
            var cropX = (sourceWidth - side) / 2;
            var cropY = (sourceHeight - side) / 2;
            var scale = side / (double)OutputSize;

            var outStride = OutputSize * 4;
            var output = new byte[outStride * OutputSize];
            for (var y = 0; y < OutputSize; y++)
            {
                var y0 = cropY + (int)(y * scale);
                var y1 = Math.Max(cropY + (int)((y + 1) * scale), y0 + 1);
                for (var x = 0; x < OutputSize; x++)
                {
                    var x0 = cropX + (int)(x * scale);
                    var x1 = Math.Max(cropX + (int)((x + 1) * scale), x0 + 1);

                    long sumB = 0, sumG = 0, sumR = 0, sumA = 0, count = 0;
                    for (var yy = y0; yy < y1 && yy < cropY + side; yy++)
                    {
                        for (var xx = x0; xx < x1 && xx < cropX + side; xx++)
                        {
                            var i = (yy * sourceStride) + (xx * 4);
                            var a = pixels[i + 3];
                            // 统一折算到直通 alpha 空间再均值（预乘源先除回 alpha），避免半透明边发暗。
                            var (b, g, r) = isPremul && a > 0
                                ? ((pixels[i] * 255L) / a, (pixels[i + 1] * 255L) / a, (pixels[i + 2] * 255L) / a)
                                : ((long)pixels[i], (long)pixels[i + 1], (long)pixels[i + 2]);
                            sumB += b;
                            sumG += g;
                            sumR += r;
                            sumA += a;
                            count++;
                        }
                    }

                    // 圆角蒙版（烙进 alpha；圆外清零——避 border 圆角裁切的 Avalonia 12 剪裁语义坑）
                    var outAlpha = IsInsideRoundedCorner(x + 0.5, y + 0.5) ? (byte)Math.Min(sumA / count, 255) : (byte)0;
                    var o = (y * outStride) + (x * 4);
                    output[o] = (byte)Math.Min(sumB / count, 255);
                    output[o + 1] = (byte)Math.Min(sumG / count, 255);
                    output[o + 2] = (byte)Math.Min(sumR / count, 255);
                    output[o + 3] = outAlpha;
                }
            }

            // 预乘帧缓冲直写（WriteableBitmap.Lock 同步可写，Save 同步编码——不涉渲染线程）。
            using var target = new WriteableBitmap(
                new PixelSize(OutputSize, OutputSize), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Premul);
            using (var frame = target.Lock())
            {
                var rowBytes = (int)frame.RowBytes;
                if (rowBytes == outStride)
                {
                    Marshal.Copy(output, 0, frame.Address, output.Length);
                }
                else
                {
                    for (var y = 0; y < OutputSize; y++)
                    {
                        Marshal.Copy(output, y * outStride, frame.Address + (y * rowBytes), outStride);
                    }
                }
            }

            using var encoded = new MemoryStream();
            target.Save(encoded, new PngBitmapEncoderOptions());
            return $"data:image/png;base64,{Convert.ToBase64String(encoded.ToArray())}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>输出像素中心是否落在圆角矩形内（圆角半径 <see cref="CornerRadius"/> 的标准 SDF 判定）。</summary>
    private static bool IsInsideRoundedCorner(double px, double py)
    {
        var clampedX = Math.Clamp(px, CornerRadius, OutputSize - CornerRadius);
        var clampedY = Math.Clamp(py, CornerRadius, OutputSize - CornerRadius);
        var dx = px - clampedX;
        var dy = py - clampedY;
        return (dx * dx) + (dy * dy) <= CornerRadius * CornerRadius;
    }

    /// <summary>位图 → BGRA 字节数组（本机缓冲中转，CopyPixels 的同步读取路径）。</summary>
    private static byte[] ReadPixels(Bitmap bitmap, int width, int height)
    {
        var stride = width * 4;
        var native = Marshal.AllocHGlobal(stride * height);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), native, stride * height, stride);
            var pixels = new byte[stride * height];
            Marshal.Copy(native, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }
}
