using Tokenmetry.Core.Services;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 插件采集源提供者（见 <see cref="IPluginSourceProvider"/>）：把插件目录与启用状态投影为
/// 采集管线可扫描的 <see cref="PluginUsageSource"/> 清单。内部锁保护快照替换；<see cref="Refresh"/>
/// 只增删差异项，未变化的插件复用既有实例（其 last-known-good 快照因此跨刷新保留）。
/// </summary>
public sealed class PluginSourceProvider : IPluginSourceProvider
{
    private readonly IPluginCatalog _catalog;
    private readonly IPluginHostClient _hostClient;
    private readonly IngestionScanRegistry _registry;
    private readonly object _gate = new();

    // 当前启用项（按 plugin id 索引）与不可变快照（注册序 = 目录枚举序）。Refresh 整体替换两者。
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private PluginUsageSource[] _snapshot = [];

    public PluginSourceProvider(
        IPluginCatalog catalog,
        IPluginHostClient hostClient,
        IngestionScanRegistry registry)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _hostClient = hostClient ?? throw new ArgumentNullException(nameof(hostClient));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public IReadOnlyList<PluginUsageSource> GetEnabledSources()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public void Refresh()
    {
        lock (_gate)
        {
            var enabled = _catalog.Installed
                .Where(installation => installation.Enabled
                    && !string.IsNullOrWhiteSpace(installation.Manifest.Id))
                .ToList();

            var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var ordered = new List<PluginUsageSource>(enabled.Count);
            var desired = new HashSet<string>(StringComparer.Ordinal);

            foreach (var installation in enabled)
            {
                var pluginId = installation.Manifest.Id;
                if (!desired.Add(pluginId))
                {
                    // 同 id 的多条安装描述防御性收口（目录实现每 id 只产一条，这里不依赖它）。
                    continue;
                }

                PluginUsageSource source;
                if (_entries.TryGetValue(pluginId, out var existing) && existing.Installation == installation)
                {
                    // 未变化：复用实例，保留 last-known-good。
                    source = existing.Source;
                }
                else
                {
                    if (existing is not null)
                    {
                        // 升级 / 目录变化：旧宿主握着旧 --plugin-dir，先释放，下一轮按新目录重建。
                        _hostClient.Remove(pluginId);

                        // 显示名（= facts Agent 键）变化时，旧数据源行随之消失；历史 facts 不动。
                        if (!string.Equals(existing.Source.Agent, installation.Manifest.DisplayName, StringComparison.Ordinal))
                        {
                            _registry.UnregisterSource(existing.Source.Agent);
                        }
                    }

                    source = new PluginUsageSource(installation, _hostClient);
                }

                next[pluginId] = new Entry(installation, source);
                ordered.Add(source);
            }

            // 停用 / 卸载：注销数据源行与快照，并释放宿主进程。历史 facts 由存储层保留。
            foreach (var (pluginId, existing) in _entries)
            {
                if (desired.Contains(pluginId))
                {
                    continue;
                }

                _registry.UnregisterSource(existing.Source.Agent);
                _hostClient.Remove(pluginId);
            }

            _entries = next;
            _snapshot = ordered.ToArray();

            // 新启用的源登记进扫描注册表（重复注册幂等，不改变注册序）。
            foreach (var source in _snapshot)
            {
                _registry.RegisterSource(source.Agent);
            }
        }
    }

    private sealed record Entry(PluginInstallation Installation, PluginUsageSource Source);
}
