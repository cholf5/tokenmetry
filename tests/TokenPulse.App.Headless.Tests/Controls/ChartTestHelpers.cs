using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TokenPulse.Core.Models;
using Xunit;

namespace TokenPulse.App.Headless.Tests.Controls;

/// <summary>D-004/D-005 测试共用 fixture 与渲染断言工具。</summary>
internal static class ChartTestHelpers
{
    /// <summary>
    /// 30 天日序列 fixture（形态对齐 I-028 输出）：窗口止于今天，稀疏有数据日
    /// （offset 2/5/6/11/17/23/29），有数据日 input+output+total，最后一天已知成本 + 未知成本混合；
    /// 其余日补零且 HasData=false（补零日辨识用例）。
    /// </summary>
    public static IReadOnlyList<DailyUsagePoint> DailyPoints30()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var realOffsets = new[] { 2, 5, 6, 11, 17, 23, 29 };
        var points = new List<DailyUsagePoint>();
        for (var i = 0; i < 30; i++)
        {
            var day = today.AddDays(-(29 - i));
            var real = realOffsets.Contains(i);
            var input = real ? 320_000L + i * 41_000 : 0;
            var output = real ? 120_000L + i * 17_000 : 0;
            points.Add(new DailyUsagePoint
            {
                Day = UsageQueryFilter.FormatDay(day),
                InputTokens = input,
                OutputTokens = output,
                TotalTokens = input + output,
                Cost = real
                    ? new AggregatedCost(1.20m + i * 0.31m, hasUnknownCost: i == 29)
                    : AggregatedCost.Empty,
                HasData = real,
            });
        }

        return points;
    }

    /// <summary>窗口首日（HasData=false 的补零日）。</summary>
    public static DailyUsagePoint FirstPaddedPoint(IReadOnlyList<DailyUsagePoint> points) =>
        points.First(p => !p.HasData);

    /// <summary>首个有数据日。</summary>
    public static DailyUsagePoint FirstRealPoint(IReadOnlyList<DailyUsagePoint> points) =>
        points.First(p => p.HasData);

    /// <summary>
    /// 30 天<b>全补零</b>窗口 fixture（I-028 双端有界 + 本机窗口内无 facts 的输出形状）：
    /// 审查 P1-2 空态语义用例——控件应呈诚实空态而非伪零线。
    /// </summary>
    public static IReadOnlyList<DailyUsagePoint> AllPadded30()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return Enumerable.Range(0, 30).Select(i => new DailyUsagePoint
        {
            Day = UsageQueryFilter.FormatDay(today.AddDays(-(29 - i))),
            Cost = AggregatedCost.Empty,
            HasData = false,
        }).ToList();
    }

    /// <summary>headless 渲染一帧（布局完成 + 手动触发渲染帧），返回合成帧位图。</summary>
    public static Bitmap RenderFrame(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        return frame!;
    }

    /// <summary>读取合成帧的原始像素（Bitmap.CopyPixels → 本机缓冲 → 托管数组）。</summary>
    private static byte[] GetPixelBytes(Bitmap frame)
    {
        var size = frame.PixelSize;
        var stride = size.Width * 4;
        var native = Marshal.AllocHGlobal(stride * size.Height);
        try
        {
            frame.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), native, stride * size.Height, stride);
            var pixels = new byte[stride * size.Height];
            Marshal.Copy(native, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    /// <summary>
    /// 合成帧里是否存在接近 target 的像素（容差匹配；BGRA/RGBA 字节序都试，
    /// 规避 PixelFormat 假设）。用于"非空像素/系列色出图"断言。
    /// </summary>
    public static bool FrameContainsColor(Bitmap frame, Color target, int tolerance = 36)
    {
        var pixels = GetPixelBytes(frame);
        if (pixels.Length < 4)
        {
            return false;
        }

        static bool Close(byte a, byte b, int tol) => Math.Abs(a - b) <= tol;

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var b0 = pixels[i];
            var b1 = pixels[i + 1];
            var b2 = pixels[i + 2];
            // BGRA 序：b0=B, b1=G, b2=R；RGBA 序：b0=R, b1=G, b2=B。两者任一命中即可。
            if ((Close(b0, target.B, tolerance) && Close(b1, target.G, tolerance) && Close(b2, target.R, tolerance))
                || (Close(b0, target.R, tolerance) && Close(b1, target.G, tolerance) && Close(b2, target.B, tolerance)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>可视化树里按文本找 TextBlock。</summary>
    public static TextBlock? FindTextBlock(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == text);

    /// <summary>有效可见性：自身及全部祖先 IsVisible（子节点属性不随父级隐藏而翻转）。</summary>
    public static bool IsEffectivelyVisible(Visual visual)
    {
        for (var node = (Visual?)visual; node is not null; node = node.GetVisualParent())
        {
            if (!node.IsVisible)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>渲染帧存 PNG（目录非空时留档；文件必须落盘）。</summary>
    public static void SaveArchive(Bitmap frame, string? dir, string fileName)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, fileName);
        frame.Save(path, new PngBitmapEncoderOptions());
        Assert.True(System.IO.File.Exists(path), $"截图未生成：{path}");
    }
}
