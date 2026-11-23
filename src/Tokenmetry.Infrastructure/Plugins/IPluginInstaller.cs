using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 安装确认所需的数据：已校验的 manifest、面向用户的警告（例如升级改名会切断历史）、
/// 以及原始包路径。<see cref="Warnings"/> 为空时确认界面只显示通用的第三方代码警告。
/// </summary>
public sealed record PluginInstallPreview(
    PluginManifest Manifest,
    IReadOnlyList<string> Warnings,
    string PackagePath);

/// <summary>
/// 从 <c>.tkmplugin</c> 包安装/升级插件。校验失败抛 <see cref="PluginInstallException"/>，
/// 且不触碰已有安装。
/// </summary>
public interface IPluginInstaller
{
    /// <summary>只校验不落盘，供安装确认界面展示；无效包抛 <see cref="PluginInstallException"/>。</summary>
    PluginInstallPreview Preview(string packagePath);

    /// <summary>校验 → staging 解压 → 再校验 → 原子安装 → 默认启用 → 重载目录。</summary>
    Task<PluginInstallation> InstallAsync(string packagePath, CancellationToken cancellationToken);
}
