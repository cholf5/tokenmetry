using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Tokenmetry.App.Localization;

namespace Tokenmetry.App.Services;

/// <summary>
/// 分享海报的导出管线（P4）：光栅化 → 剪贴板 / 文件。
/// 渲染必须对<b>已挂树、已布局</b>的节点做——两个图表控件在 OnAttachedToVisualTree 里才建图，
/// 对悬空控件 Render 得到的是空白加无样式。
/// </summary>
public static class SharePosterExporter
{
    /// <summary>导出倍率：版面按 1× 排版，位图按 2× 光栅化（1080 → 2160）。</summary>
    public const double ExportScale = 2.0;

    private const string Extension = ".png";

    /// <summary>
    /// 按保存时刻生成文件名：固定 tokenmetry-yyyyMMdd-HHmmss，不含海报的数据范围。
    /// 保存对话框弹出时还不知道落盘目录，无法查重，所以靠保存时刻保证同一天多次导出的建议名互不覆盖。
    /// </summary>
    public static string FileName(DateTime? savedAt = null)
    {
        return "tokenmetry-" + (savedAt ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + Extension;
    }

    /// <summary>把已布局的海报根节点按 <see cref="ExportScale"/> 光栅化。</summary>
    public static RenderTargetBitmap Render(Control posterRoot, double scale = ExportScale)
    {
        ArgumentNullException.ThrowIfNull(posterRoot);

        // 明确先跑一次布局：Bounds 是 arrange 期状态，调用前是否已布局取决于谁触发了导出
        posterRoot.UpdateLayout();
        var width = Math.Max(1, (int)Math.Round(posterRoot.Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(posterRoot.Bounds.Height * scale));

        var bitmap = new RenderTargetBitmap(
            new PixelSize(width, height),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(posterRoot);
        return bitmap;
    }

    /// <summary>
    /// 复制到剪贴板。返回 false 表示当前平台不支持位图剪贴板——调用方须降级（例如改为存临时文件
    /// 并提示路径），不得静默失败。
    /// </summary>
    public static async Task<bool> CopyToClipboardAsync(IClipboard? clipboard, Bitmap bitmap)
    {
        if (clipboard is null)
        {
            return false;
        }

        try
        {
            await clipboard.SetBitmapAsync(bitmap);

            // X11/Wayland 的剪贴板内容是「本进程持有」的：flush 才会交给剪贴板管理器，
            // 否则进程退出/窗口关闭后粘贴为空。失败不影响已经设置成功的内容。
            try
            {
                await clipboard.FlushAsync();
            }
            catch (Exception)
            {
                // 部分平台未实现 flush：忽略，不把「已复制」误报成失败
            }

            return true;
        }
        catch (Exception)
        {
            // 平台不支持 / 剪贴板被占用：由调用方决定怎么告知用户
            return false;
        }
    }

    /// <summary>保存为 PNG。返回 null 表示用户取消了保存对话框。</summary>
    public static async Task<string?> SaveAsync(TopLevel topLevel, Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(topLevel);

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Translations.Instance.SharePosterSave,
            SuggestedFileName = FileName(),
            DefaultExtension = "png",
            FileTypeChoices =
            [
                new FilePickerFileType("PNG") { Patterns = ["*.png"] },
            ],
        });

        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenWriteAsync();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return file.Path.LocalPath;
    }
}
