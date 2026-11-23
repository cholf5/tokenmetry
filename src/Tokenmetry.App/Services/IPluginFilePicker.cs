using System.Threading.Tasks;

namespace Tokenmetry.App.Services;

/// <summary>
/// 插件包文件选择（Task 8）：安装按钮的「选择 .tkmplugin 文件」入口。抽象成接口是为了让
/// <see cref="ViewModels.PluginsSectionViewModel"/> 在无 UI 的测试里注入假选择器；
/// 生产实现 <see cref="AvaloniaPluginFilePicker"/> 走主窗的 <c>StorageProvider</c>。
/// 返回 null 表示用户取消。
/// </summary>
public interface IPluginFilePicker
{
    /// <summary>弹出文件选择框，返回本地包路径；取消返回 null。</summary>
    Task<string?> PickPackageAsync();
}
