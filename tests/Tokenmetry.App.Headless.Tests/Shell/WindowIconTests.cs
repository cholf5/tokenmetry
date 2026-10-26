using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tokenmetry.App.Views;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// D-018 应用图标接线回归锁：
/// 1. 主窗 Window.Icon 在构造期来自内嵌资产（app-icon-window.png，全出血 256）——
///    覆盖窗口标题栏 / 任务栏 / Alt-Tab / Linux WM（_NET_WM_ICON）；
/// 2. 图标三资产随 csproj 的 Assets/** AvaloniaResource 进包（exe ico / macOS Dock
///    Apple 网格 PNG / 窗口 PNG）——防「文件在目录里但没进包」的静默回归。
/// 3. window png 全出血（不透明 bbox 铺满画布）——修正项 1：该资产曾是 Chrome 截图
///    裁切图（不透明区只占右半），Windows 标题栏只见半张图标，alpha bbox 锁防复踩
///    （make-icon.py 烘焙自检只覆盖 ico/png，window png 是管线外产物，只能在测试里锁）。
/// Windows exe 文件图标（csproj ApplicationIcon）与 macOS Dock interop 无法在 headless
/// 断言，真机目检移交用户（与托盘手感目检同一约定）。
/// </summary>
public class WindowIconTests
{
    [AvaloniaFact]
    public void MainWindow_sets_window_icon_from_embedded_asset()
    {
        var window = new MainWindow();
        try
        {
            Assert.NotNull(window.Icon);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Window_icon_png_is_full_bleed()
    {
        // 全出血判定用不透明像素包围盒而非逐角像素：图标圆角由四角像素透明承担，
        // 但每条画布边上都有不透明像素——bbox 铺满 (0,0,255,255) 即「内容完整未裁切」。
        using var stream = AssetLoader.Open(new Uri("avares://Tokenmetry.App/Assets/app-icon-window.png"));
        using var bitmap = new Bitmap(stream);
        Assert.Equal(256, bitmap.PixelSize.Width);
        Assert.Equal(256, bitmap.PixelSize.Height);
        Assert.Equal(32, bitmap.Format?.BitsPerPixel); // 4 字节/像素，alpha 在第 4 字节（BGRA/RGBA 皆然）

        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var native = Marshal.AllocHGlobal(stride * size.Height);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), native, stride * size.Height, stride);
            var pixels = new byte[stride * size.Height];
            Marshal.Copy(native, pixels, 0, pixels.Length);

            int? minX = null, maxX = null, minY = null, maxY = null;
            for (var y = 0; y < size.Height; y++)
            {
                for (var x = 0; x < size.Width; x++)
                {
                    if (pixels[y * stride + x * 4 + 3] > 8)
                    {
                        minX = minX is null || x < minX ? x : minX;
                        maxX = maxX is null || x > maxX ? x : maxX;
                        minY = minY is null || y < minY ? y : minY;
                        maxY = maxY is null || y > maxY ? y : maxY;
                    }
                }
            }

            Assert.True(minX is not null, "整图全透明");
            Assert.Equal(0, minX);
            Assert.Equal(0, minY);
            Assert.Equal(size.Width - 1, maxX);
            Assert.Equal(size.Height - 1, maxY);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    [AvaloniaFact]
    public void Icon_assets_are_embedded_resources()
    {
        foreach (var asset in new[]
                 {
                     "Assets/app-icon.ico",
                     "Assets/app-icon.png",
                     "Assets/app-icon-window.png",
                 })
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Tokenmetry.App/{asset}"));
            Assert.True(stream.Length > 0, asset);
        }
    }
}
