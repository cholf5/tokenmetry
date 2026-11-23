using System.Threading.Tasks;
using Tokenmetry.Infrastructure.Plugins;

namespace Tokenmetry.App.Services;

/// <summary>
/// 插件敏感操作的用户确认（Task 8）：安装前展示 manifest 与第三方代码警告，卸载前确认。
/// 抽象成接口是为了让 <see cref="ViewModels.PluginsSectionViewModel"/> 在无 UI 的测试里注入假确认；
/// 生产实现 <see cref="AvaloniaPluginInstallConfirmation"/> 打开模态窗。
/// <para>
/// 卸载确认也挂在这里（而不是给 Section VM 增加一个未在任务契约里的构造参数）——它是同一类
/// 「敏感操作确认」服务，测试用同一个假实现同时覆盖安装与卸载路径。
/// </para>
/// </summary>
public interface IPluginInstallConfirmation
{
    /// <summary>展示安装确认（含 <paramref name="preview"/> 的 manifest 与警告）；true = 继续安装。</summary>
    Task<bool> ConfirmAsync(PluginInstallPreview preview);

    /// <summary>展示卸载确认（插件显示名仅供提示）；true = 继续卸载。</summary>
    Task<bool> ConfirmUninstallAsync(string displayName);
}
