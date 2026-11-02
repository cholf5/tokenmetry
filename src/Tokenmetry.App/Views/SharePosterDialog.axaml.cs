using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

        using var bitmap = SharePosterExporter.Render(PreviewView.PosterRootControl);
        var ok = await SharePosterExporter.CopyToClipboardAsync(Clipboard, bitmap);
        StatusText.Text = ok ? "已复制到剪贴板" : "当前系统不支持复制图片，请改用「保存为文件」";
    }

    /// <summary>保存为 PNG；用户取消对话框时不提示。</summary>
    public async Task SaveAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        using var bitmap = SharePosterExporter.Render(PreviewView.PosterRootControl);
        var path = await SharePosterExporter.SaveAsync(this, bitmap, ViewModel.CurrentRequest);
        StatusText.Text = path is null ? string.Empty : "已保存：" + path;
    }
}
