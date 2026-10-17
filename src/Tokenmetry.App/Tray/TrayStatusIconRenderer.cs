using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Tokenmetry.App.Tray;

/// <summary>
/// 状态栏文字位图渲染（D-014 纯托管方案①）：把紧凑用量文本画成 alpha-only 位图 → WindowIcon。
/// template 图标语义下 macOS 只取 alpha 通道并随明暗菜单栏自动反色，因此画不透明黑字于透明底即可。
/// <para>尺寸依据 Avalonia.Native trayicon.mm 的 SetIcon 逻辑：任意位图统一缩放到
/// floor(菜单字体 13pt × 4/3) = 17pt 高（宽度按比例）——按 2x 像素（34px）渲染，retina 屏上
/// 恰好 1:1 无重采样，1x 屏轻微降采样；两档 DPI 的真机目检是本卡验收项（TC-002）。</para>
/// </summary>
internal static class TrayStatusIconRenderer
{
    /// <summary>2x 像素空间（retina 主场景）。1x 屏由 NSImage 缩放承担。</summary>
    private const double PixelScale = 2;

    /// <summary>与 trayicon.mm 的 floor(13 × 1.3333) 对齐；原生端会把位图缩到该 pt 高。</summary>
    private const double LogicalHeight = 17;

    /// <summary>macOS 菜单栏字号；嫌宽/糊时优先调这里（真机目检项）。</summary>
    private const double LogicalFontSize = 13;

    /// <summary>左右留白（NSStatusBarButton 自带 ~8pt 内边距，此处只防字形贴边）。</summary>
    private const double LogicalPadding = 2;

    public static WindowIcon Render(string text)
    {
        // 不透明黑字：template 渲染只看 alpha 通道，颜色无意义
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            LogicalFontSize * PixelScale,
            Brushes.Black);

        var width = (int)Math.Ceiling(formatted.Width + LogicalPadding * 2 * PixelScale);
        var height = (int)(LogicalHeight * PixelScale);

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawText(
                formatted,
                new Point(LogicalPadding * PixelScale, (height - formatted.Height) / 2));
        }

        // 经 PNG 字节流构造 WindowIcon：与模板资产同一构造路径（stream → IPlatformIconLoader），
        // headless 测试宿主与桌面宿主行为一致；RenderTargetBitmap 直构不经此路，未验证过。
        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return new WindowIcon(png);
    }
}
