using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Tokenmetry.App.Localization;

namespace Tokenmetry.App.Services;

/// <summary>
/// 生产文件选择器：从主窗 <c>StorageProvider</c> 打开「选择插件包」对话框，只列
/// <c>*.tkmplugin</c>。DI 单例在构造时主窗可能还不存在，因此在每次调用时再取主窗
/// （<see cref="IToastService"/> 同一取窗约定）；无桌面生命周期时如实返回 null（取消）。
/// </summary>
public sealed class AvaloniaPluginFilePicker : IPluginFilePicker
{
    public async Task<string?> PickPackageAsync()
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = Translations.Instance.PluginInstallButton,
            FileTypeFilter =
            [
                new FilePickerFileType(Translations.Instance.PluginSectionTitle)
                {
                    Patterns = ["*.tkmplugin"],
                },
            ],
        });

        if (files.Count == 0)
        {
            return null;
        }

        // 只接受本地文件路径（安装器按文件系统路径读取 zip）；非本地提供者返回 null。
        return files[0].Path.LocalPath;
    }
}
