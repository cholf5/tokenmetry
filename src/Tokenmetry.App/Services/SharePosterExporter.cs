using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Tokenmetry.Core.Models;

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

    /// <summary>按时间范围生成文件名：单日一段日期，多日两段。</summary>
    public static string FileName(SharePosterRequest request)
    {
        var start = request.RangeStart.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (request.IsSingleDay)
        {
            return "tokenmetry-" + start + Extension;
        }

        var end = request.RangeEnd.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return "tokenmetry-" + start + "-" + end + Extension;
    }

    /// <summary>把已布局的海报根节点按 <see cref="ExportScale"/> 光栅化。</summary>
    public static RenderTargetBitmap Render(Control posterRoot, double scale = ExportScale)
    {
        ArgumentNullException.ThrowIfNull(posterRoot);

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
            return true;
        }
        catch (Exception)
        {
            // 平台不支持 / 剪贴板被占用：由调用方决定怎么告知用户
            return false;
        }
    }

    /// <summary>保存为 PNG。返回 null 表示用户取消了保存对话框。</summary>
    public static async Task<string?> SaveAsync(
        TopLevel topLevel, Bitmap bitmap, SharePosterRequest request)
    {
        ArgumentNullException.ThrowIfNull(topLevel);

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = FileName(request),
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
