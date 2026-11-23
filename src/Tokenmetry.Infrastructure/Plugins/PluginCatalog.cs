using System.Text.Json;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Infrastructure.Plugins;

/// <summary>
/// 文件系统插件目录：扫描 <c>plugins/&lt;id&gt;/&lt;version&gt;/plugin.json</c>，每个 id 取最高有效版本；
/// 无效 manifest / id 与目录不一致的版本目录进 <see cref="Invalid"/>。启用状态完全由
/// <c>plugins.json</c> 决定（损坏时回落为全部禁用）。
/// <para>实现是同步磁盘操作，返回已完成的 <see cref="Task"/>；调用方无需担心线程切换。</para>
/// </summary>
public sealed class PluginCatalog : IPluginCatalog
{
    private readonly PluginPaths _paths;
    private readonly PluginStateStore _stateStore;
    private readonly object _gate = new();

    private IReadOnlyList<PluginInstallation> _installed = [];
    private IReadOnlyList<PluginCatalogError> _invalid = [];
    private Dictionary<string, bool> _states = new(StringComparer.Ordinal);

    public PluginCatalog(PluginPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(paths.PluginsDirectory))
        {
            throw new ArgumentException("plugins directory must be provided", nameof(paths));
        }

        if (string.IsNullOrWhiteSpace(paths.PluginDataDirectory))
        {
            throw new ArgumentException("plugin data directory must be provided", nameof(paths));
        }

        if (string.IsNullOrWhiteSpace(paths.RegistryFilePath))
        {
            throw new ArgumentException("registry file path must be provided", nameof(paths));
        }

        _paths = paths;
        _stateStore = new PluginStateStore(paths.RegistryFilePath);
    }

    public IReadOnlyList<PluginInstallation> Installed
    {
        get
        {
            lock (_gate)
            {
                return _installed;
            }
        }
    }

    public IReadOnlyList<PluginCatalogError> Invalid
    {
        get
        {
            lock (_gate)
            {
                return _invalid;
            }
        }
    }

    public Task ReloadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ReloadCore();
        }

        return Task.CompletedTask;
    }

    public Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || !PluginPackageValidator.IsSafeSegment(pluginId))
        {
            throw new ArgumentException(
                $"plugin id '{pluginId}' is not a safe directory name", nameof(pluginId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var next = new Dictionary<string, bool>(_states, StringComparer.Ordinal)
            {
                [pluginId] = enabled,
            };
            _stateStore.Save(next);
            _states = next;
            ReloadCore();
        }

        return Task.CompletedTask;
    }

    public Task UninstallAsync(string pluginId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || !PluginPackageValidator.IsSafeSegment(pluginId))
        {
            throw new ArgumentException(
                $"plugin id '{pluginId}' is not a safe directory name", nameof(pluginId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            DeleteDirectoryIfExists(Path.Combine(_paths.PluginsDirectory, pluginId));
            DeleteDirectoryIfExists(Path.Combine(_paths.PluginDataDirectory, pluginId));

            var next = new Dictionary<string, bool>(_states, StringComparer.Ordinal);
            if (next.Remove(pluginId))
            {
                _stateStore.Save(next);
            }

            _states = next;
            ReloadCore();
        }

        return Task.CompletedTask;
    }

    private void ReloadCore()
    {
        _states = new Dictionary<string, bool>(_stateStore.Load(), StringComparer.Ordinal);

        var installed = new List<PluginInstallation>();
        var invalid = new List<PluginCatalogError>();

        foreach (var pluginDirectory in EnumerateDirectoriesSafe(_paths.PluginsDirectory))
        {
            var pluginId = Path.GetFileName(pluginDirectory);

            // .staging（及未来任何点目录）不是已安装插件。
            if (pluginId.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            string? bestDirectory = null;
            PluginManifest? bestManifest = null;

            foreach (var versionDirectory in EnumerateDirectoriesSafe(pluginDirectory))
            {
                var versionName = Path.GetFileName(versionDirectory);
                var (manifest, error) = ReadManifest(versionDirectory);
                if (manifest is null)
                {
                    invalid.Add(new PluginCatalogError(versionDirectory, error ?? "plugin.json is invalid"));
                    continue;
                }

                if (!string.Equals(manifest.Id, pluginId, StringComparison.Ordinal))
                {
                    invalid.Add(new PluginCatalogError(
                        versionDirectory,
                        $"manifest id '{manifest.Id}' does not match directory '{pluginId}'"));
                    continue;
                }

                if (bestDirectory is null
                    || CompareVersionDirectories(versionName, Path.GetFileName(bestDirectory)) > 0)
                {
                    bestDirectory = versionDirectory;
                    bestManifest = manifest;
                }
            }

            if (bestDirectory is null || bestManifest is null)
            {
                continue;
            }

            var enabled = _states.TryGetValue(pluginId, out var stored) && stored;
            installed.Add(new PluginInstallation(
                bestManifest,
                bestDirectory,
                Path.Combine(bestDirectory, bestManifest.Icon),
                enabled));
        }

        _installed = installed.ToArray();
        _invalid = invalid.ToArray();
    }

    private static (PluginManifest? Manifest, string? Error) ReadManifest(string versionDirectory)
    {
        var manifestPath = Path.Combine(versionDirectory, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return (null, "plugin.json is missing");
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = PluginJson.DeserializeManifest(json);
            if (manifest is null)
            {
                return (null, "plugin.json is not valid JSON");
            }

            var errors = PluginManifestValidator.Validate(manifest);
            if (errors.Count > 0)
            {
                return (null, string.Join("; ", errors));
            }

            if (!PluginPackageValidator.IsSafeRelativePath(manifest.Icon))
            {
                return (null, "icon must be a relative path inside the plugin directory");
            }

            return (manifest, null);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            return (null, "plugin.json could not be read");
        }
    }

    /// <summary>
    /// 版本目录比较：两边都能解析为 <see cref="Version"/> 时按数值比较，否则按目录名字符串
    /// （Ordinal）比较。返回值 &gt; 0 表示 left 更新。
    /// </summary>
    private static int CompareVersionDirectories(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
