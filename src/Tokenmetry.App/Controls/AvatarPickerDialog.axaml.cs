using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 模型头像图标选择器的内容层：上传按钮的文件拾取 + 标准化处理在代码后置（需要
/// TopLevel 的 StorageProvider，VM 不持窗口环境）；处理成功即写入草稿并切到图片模式，
/// 失败给行内「换一张」提示。其余交互全在 XAML 绑定（AvatarPickerViewModel 草稿）。
/// </summary>
public partial class AvatarPickerDialog : UserControl
{
    public AvatarPickerDialog()
    {
        InitializeComponent();
    }

    private async void OnUploadAvatarClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel
            || DataContext is not DataPricingViewModel viewModel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = Translations.Instance.AvatarUploadButton,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        var dataUri = AvatarImageProcessor.ProcessToDataUri(stream);
        if (dataUri is null)
        {
            // 解析失败如实提示（非图片 / 超限），不落半成品也不切模式。
            viewModel.AvatarPicker.IsImageDecodeFailed = true;
            return;
        }

        viewModel.AvatarPicker.IsImageDecodeFailed = false;
        viewModel.AvatarPicker.ImageDataUri = dataUri;
        // 用户点了上传 = 意图明确：切到图片模式，预览立即可见（所见即所存）。
        viewModel.AvatarPicker.Mode = ModelAvatarKind.Image;
    }
}
