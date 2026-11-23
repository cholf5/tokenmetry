namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 插件采集源的提供者：把「当前已启用的插件」投影为采集管线可扫描的
/// <see cref="PluginUsageSource"/> 列表。`LocalIngestionDataSource.BuildScanPipeline()` 每轮读取
/// 一次当前快照；安装/停用/卸载后调用 <see cref="Refresh"/> 使清单与差异项同步。
/// </summary>
public interface IPluginSourceProvider
{
    /// <summary>当前启用插件的不可变快照（注册序稳定）。</summary>
    IReadOnlyList<PluginUsageSource> GetEnabledSources();

    /// <summary>
    /// 与插件目录对齐：注册新启用项、注销已移除项（并释放对应宿主进程），
    /// 未变化的插件复用既有实例（保留 last-known-good 快照）。
    /// </summary>
    void Refresh();
}

/// <summary>
/// 插件宿主不可用时的空提供者（<see cref="PluginHostLocator.Locate()"/> 未找到宿主可执行文件）：
/// 不注册任何插件源、<see cref="Refresh"/> 为空操作，应用照常启动。
/// </summary>
public sealed class UnavailablePluginSourceProvider : IPluginSourceProvider
{
    public static UnavailablePluginSourceProvider Instance { get; } = new();

    public IReadOnlyList<PluginUsageSource> GetEnabledSources() => [];

    public void Refresh()
    {
    }
}
