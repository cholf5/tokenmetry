namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 插件子系统使用的三处路径根，由 App 层从 <c>AppPaths</c> 构造（Infrastructure 不引用 App）：
/// <list type="bullet">
/// <item><see cref="PluginsDirectory"/>：已安装插件目录 <c>plugins/&lt;id&gt;/&lt;version&gt;/</c>。</item>
/// <item><see cref="PluginDataDirectory"/>：每插件解析缓存根 <c>plugin-data/&lt;id&gt;/</c>，
/// 升级/卸载插件文件不误删缓存。</item>
/// <item><see cref="RegistryFilePath"/>：启用状态注册表（JSON），是唯一的启用状态来源。</item>
/// </list>
/// </summary>
public sealed record PluginPaths(
    string PluginsDirectory,
    string PluginDataDirectory,
    string RegistryFilePath);
