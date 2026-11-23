using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Views;
using Tokenmetry.Infrastructure.Plugins;

namespace Tokenmetry.App.Services;

/// <summary>
/// 生产确认服务（Task 8）：安装确认打开 <see cref="PluginInstallConfirmDialog"/>；卸载确认用
/// 同一视觉配方的小模态窗（代码建窗，复用「取消 / 确认」按钮与令牌样式）。
/// <para>
/// 每次调用时再取主窗（DI 单例构造早于主窗）：无桌面生命周期 / 主窗为空时如实返回 false，
/// 宁可拒绝也不在未确认的情况下安装或卸载第三方代码。
/// </para>
/// </summary>
public sealed class AvaloniaPluginInstallConfirmation : IPluginInstallConfirmation
{
    public async Task<bool> ConfirmAsync(PluginInstallPreview preview)
    {
        var owner = MainWindow;
        if (owner is null)
        {
            return false;
        }

        var dialog = new PluginInstallConfirmDialog(preview);
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<bool> ConfirmUninstallAsync(string displayName)
    {
        var owner = MainWindow;
        if (owner is null)
        {
            return false;
        }

        var dialog = BuildUninstallDialog(displayName);
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// 卸载确认窗的构建缝（headless 测试可直接 Show 后检查按钮文案）：标题、正文与肯定按钮
    /// 全部使用卸载语义（<see cref="Translations.PluginUninstallAction"/> /
    /// <see cref="Translations.PluginUninstallConfirm"/>），不得复用「确认安装」动词。
    /// </summary>
    public static Window BuildUninstallDialog(string displayName)
    {
        var cancel = new Button
        {
            Content = Translations.Instance.PluginInstallCancel,
            Classes = { "app-btn" },
            IsCancel = true,
            MinWidth = 96,
        };
        var confirm = new Button
        {
            Content = Translations.Instance.PluginUninstallAction,
            Classes = { "app-btn", "primary" },
            IsDefault = true,
            MinWidth = 96,
        };

        var title = new TextBlock
        {
            Text = Translations.Instance.PluginUninstallAction,
            TextWrapping = TextWrapping.Wrap,
        };
        title.Classes.Add("panel-title");

        var body = new TextBlock
        {
            Text = Translations.Instance.PluginUninstallConfirm,
            TextWrapping = TextWrapping.Wrap,
        };
        body.Classes.Add("page-subtitle");

        var dialog = new Window
        {
            Title = Translations.Instance.PluginUninstallAction,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 14,
                Children =
                {
                    title,
                    body,
                    new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        Children = { cancel, confirm },
                    },
                },
            },
        };
        WindowsCaptionTheme.Default.Attach(dialog);

        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        return dialog;
    }

    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
