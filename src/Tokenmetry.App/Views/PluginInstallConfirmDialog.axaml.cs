using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Infrastructure.Plugins;

namespace Tokenmetry.App.Views;

/// <summary>
/// 插件安装确认窗（Task 8）：展示 manifest 名称/版本/作者（标注未验证）与第三方代码警告，
/// 确认返回 true。由 <see cref="AvaloniaPluginInstallConfirmation"/> 以
/// <c>ShowDialog&lt;bool&gt;</c> 打开；标题栏关闭 / 取消 → false。
/// </summary>
public partial class PluginInstallConfirmDialog : Window
{
    public PluginInstallConfirmDialog()
    {
        InitializeComponent();
        WindowsCaptionTheme.Default.Attach(this);
    }

    public PluginInstallConfirmDialog(PluginInstallPreview preview)
        : this()
    {
        ArgumentNullException.ThrowIfNull(preview);

        var manifest = preview.Manifest;
        ManifestNameText.Text = manifest.DisplayName;

        ManifestVersionText.Text = string.Format(
            Translations.Instance.PluginVersionFormat, manifest.Version);

        if (string.IsNullOrWhiteSpace(manifest.Author))
        {
            AuthorText.IsVisible = false;
        }
        else
        {
            AuthorText.Text = string.Format(
                Translations.Instance.PluginAuthorUnverifiedFormat, manifest.Author);
        }

        if (preview.Warnings.Count > 0)
        {
            WarningsText.Text = string.Join(Environment.NewLine, preview.Warnings);
            WarningsText.IsVisible = true;
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
