namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 扫描失败的一个插件目录。用绝对目录路径定位，<see cref="Message"/> 是给用户的短句。
/// 无效目录不进入 <see cref="IPluginCatalog.Installed"/>，因此不会被注册进扫描管线。
/// </summary>
public sealed record PluginCatalogError(string Directory, string Message);

/// <summary>
/// 已安装插件的目录与启用状态门面。文件系统是安装事实来源，<c>plugins.json</c> 是启用状态的
/// 唯一来源。<see cref="ReloadAsync"/> 扫描 <c>plugins/&lt;id&gt;/&lt;version&gt;/plugin.json</c>。
/// </summary>
public interface IPluginCatalog
{
    /// <summary>每个 plugin id 的最高有效版本；启用状态来自注册表。</summary>
    IReadOnlyList<PluginInstallation> Installed { get; }

    /// <summary>扫描到但 manifest 无效（或目录 id 与 manifest 不一致）的插件目录。</summary>
    IReadOnlyList<PluginCatalogError> Invalid { get; }

    /// <summary>重新扫描插件目录并读取启用状态。</summary>
    Task ReloadAsync(CancellationToken cancellationToken);

    /// <summary>写入启用状态并重载目录；未知 id 也允许写入（幂等）。</summary>
    Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken);

    /// <summary>删除插件文件与 <c>plugin-data/&lt;id&gt;</c>，移除注册表条目并重载。历史 facts 不受影响。</summary>
    Task UninstallAsync(string pluginId, CancellationToken cancellationToken);
}
