using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Views;

/// <summary>
/// 海报生成对话框（P5）：独立窗口。窗口本身就是渲染宿主——海报子树挂在里面才拿得到样式、
/// 主题和图表初始化，导出时直接对子树根光栅化。
/// </summary>
public partial class SharePosterDialog : Window
{
    /// <summary>
    /// 剪贴板位图必须活到粘贴那一刻：<c>Clipboard.SetBitmapAsync</c> 在 Windows/X11 上是延迟提供
    /// （真正的 HGLOBAL/字节在别的应用粘贴时才取），方法返回就 Dispose 会得到「提示已复制、粘贴为空」。
    /// 下一次复制替换、窗口关闭统一释放。
    /// </summary>
    private Bitmap? _clipboardBitmap;

    public SharePosterDialog()
    {
        InitializeComponent();
        // Esc 关闭：独立窗口的关闭语义之一（设计 §9.1 把 Esc/关闭列为选独立窗口的代价）
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _clipboardBitmap?.Dispose();
        _clipboardBitmap = null;
        base.OnClosed(e);
    }

    private SharePosterDialogViewModel? ViewModel => DataContext as SharePosterDialogViewModel;

    private async void OnCopyClick(object? sender, RoutedEventArgs e) => await CopyAsync();

    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>复制到剪贴板；平台不支持时降级为提示，不静默失败。</summary>
    public async Task CopyAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        var t = Translations.Instance;
        try
        {
            var bitmap = SharePosterExporter.Render(PreviewView.PosterRootControl);
            var ok = await SharePosterExporter.CopyToClipboardAsync(Clipboard, bitmap);
            if (ok)
            {
                // 延迟提供语义：位图必须留着（见字段注释），替换上一张并等窗口关闭时释放
                _clipboardBitmap?.Dispose();
                _clipboardBitmap = bitmap;
            }
            else
            {
                bitmap.Dispose();
            }

            StatusText.Text = ok ? t.SharePosterCopied : t.SharePosterCopyFailed;
            if (ok)
            {
                FlashCopied();
            }
        }
        catch (Exception ex)
        {
            // 渲染/剪贴板都可能抛（显卡或平台差异）：如实报错，不静默
            StatusText.Text = string.Format(t.SharePosterCopyFailedFormat, ex.Message);
        }
    }

    /// <summary>主按钮按下后 2 秒内显示「已复制」，随后复原（设计 §3.11 的即时反馈）。</summary>
    private void FlashCopied()
    {
        var t = Translations.Instance;
        CopyButton.Content = t.SharePosterCopied;
        DispatcherTimer.RunOnce(
            () =>
            {
                if (CopyButton is not null)
                {
                    CopyButton.Content = t.SharePosterCopy;
                }
            },
            TimeSpan.FromSeconds(2));
    }

    /// <summary>保存为 PNG；用户取消对话框时不提示。</summary>
    public async Task SaveAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        var t = Translations.Instance;
        try
        {
            using var bitmap = SharePosterExporter.Render(PreviewView.PosterRootControl);
            var path = await SharePosterExporter.SaveAsync(this, bitmap, ViewModel.CurrentRequest);
            StatusText.Text = path is null ? string.Empty : string.Format(t.SharePosterSavedFormat, path);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(t.SharePosterSaveFailedFormat, ex.Message);
        }
    }
}
