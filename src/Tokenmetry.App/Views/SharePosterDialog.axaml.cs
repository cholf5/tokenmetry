using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
    public SharePosterDialog()
    {
        InitializeComponent();
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
            using var bitmap = SharePosterExporter.Render(PreviewView.PosterRootControl);
            var ok = await SharePosterExporter.CopyToClipboardAsync(Clipboard, bitmap);
            StatusText.Text = ok ? t.SharePosterCopied : t.SharePosterCopyFailed;
        }
        catch (Exception ex)
        {
            // 渲染/剪贴板都可能抛（显卡或平台差异）：如实报错，不静默
            StatusText.Text = string.Format(t.SharePosterSaveFailedFormat, ex.Message);
        }
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
