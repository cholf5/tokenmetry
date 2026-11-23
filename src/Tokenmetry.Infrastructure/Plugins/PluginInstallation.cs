using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 一个已安装插件的运行期描述：manifest、安装目录、图标绝对路径与启用状态。
/// 由安装器（Task 4）在解压/校验后返回，宿主客户端据此定位插件包。
/// </summary>
public sealed record PluginInstallation(
    PluginManifest Manifest,
    string Directory,
    string IconPath,
    bool Enabled);
