using Tokenmetry.Infrastructure.Plugins;

namespace Tokenmetry.Plugins.Tests.Runtime;

/// <summary>
/// 目录/状态管理：扫描 <c>plugins/&lt;id&gt;/&lt;version&gt;/plugin.json</c>，同 id 取最高版本，
/// 无效 manifest 进 <see cref="IPluginCatalog.Invalid"/>；启用状态由 <c>plugins.json</c> 唯一决定，
/// 状态文件损坏时回落为全部禁用。卸载删除插件文件与缓存目录、保留 facts（本层不涉及）。
/// </summary>
public class PluginCatalogTests
{
    [Fact]
    public async Task Invalid_manifest_at_startup_is_listed_in_Invalid()
    {
        using var context = new TestPluginPaths();
        var directory = context.WriteInstalledPlugin(
            "com.acme.foo",
            "1.0.0",
            manifestJson: ValidManifest.Json(displayName: ""),
            writeEntryAssembly: false);
        var catalog = new PluginCatalog(context.Paths);

        await catalog.ReloadAsync(CancellationToken.None);

        Assert.Empty(catalog.Installed);
        var invalid = Assert.Single(catalog.Invalid);
        Assert.Equal(directory, invalid.Directory);
        Assert.Contains("displayName", invalid.Message);
    }

    [Fact]
    public async Task Reload_keeps_highest_version()
    {
        using var context = new TestPluginPaths();
        context.WriteInstalledPlugin("com.acme.foo", "1.0.0");
        context.WriteInstalledPlugin("com.acme.foo", "2.0.0");
        var catalog = new PluginCatalog(context.Paths);

        await catalog.ReloadAsync(CancellationToken.None);

        var single = Assert.Single(catalog.Installed);
        Assert.Equal("2.0.0", single.Manifest.Version);
        Assert.EndsWith(Path.Combine("com.acme.foo", "2.0.0"), single.Directory);
    }

    [Fact]
    public async Task Reload_compares_versions_numerically()
    {
        using var context = new TestPluginPaths();
        context.WriteInstalledPlugin("com.acme.foo", "1.9.0");
        context.WriteInstalledPlugin("com.acme.foo", "1.10.0");
        var catalog = new PluginCatalog(context.Paths);

        await catalog.ReloadAsync(CancellationToken.None);

        Assert.Equal("1.10.0", Assert.Single(catalog.Installed).Manifest.Version);
    }

    [Fact]
    public async Task Reload_falls_back_to_string_for_unparseable_versions()
    {
        using var context = new TestPluginPaths();
        context.WriteInstalledPlugin("com.acme.foo", "alpha");
        context.WriteInstalledPlugin("com.acme.foo", "beta");
        var catalog = new PluginCatalog(context.Paths);

        await catalog.ReloadAsync(CancellationToken.None);

        Assert.Equal("beta", Assert.Single(catalog.Installed).Manifest.Version);
    }

    [Fact]
    public async Task Reload_ignores_staging_directory()
    {
        using var context = new TestPluginPaths();
        var staging = Path.Combine(context.Paths.PluginsDirectory, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "plugin.json"), ValidManifest.Json(id: "com.acme.foo"));
        var catalog = new PluginCatalog(context.Paths);

        await catalog.ReloadAsync(CancellationToken.None);

        Assert.Empty(catalog.Installed);
        Assert.Empty(catalog.Invalid);
    }

    [Fact]
    public async Task Corrupt_state_file_disables_all_plugins_without_throwing()
    {
        using var context = new TestPluginPaths();
        context.WriteInstalledPlugin("com.acme.foo", "1.0.0");
        File.WriteAllText(context.Paths.RegistryFilePath, "{ this is not json");
        var catalog = new PluginCatalog(context.Paths);

        await catalog.ReloadAsync(CancellationToken.None);

        Assert.False(Assert.Single(catalog.Installed).Enabled);
    }

    [Fact]
    public async Task SetEnabled_persists_across_reload_and_new_catalog()
    {
        using var context = new TestPluginPaths();
        context.WriteInstalledPlugin("com.acme.foo", "1.0.0");
        var catalog = new PluginCatalog(context.Paths);
        await catalog.ReloadAsync(CancellationToken.None);

        Assert.False(Assert.Single(catalog.Installed).Enabled);

        await catalog.SetEnabledAsync("com.acme.foo", true, CancellationToken.None);
        Assert.True(Assert.Single(catalog.Installed).Enabled);

        var reopened = new PluginCatalog(context.Paths);
        await reopened.ReloadAsync(CancellationToken.None);
        Assert.True(Assert.Single(reopened.Installed).Enabled);

        await reopened.SetEnabledAsync("com.acme.foo", false, CancellationToken.None);
        Assert.False(Assert.Single(reopened.Installed).Enabled);
    }

    [Fact]
    public async Task Uninstall_removes_files_data_and_state()
    {
        using var context = new TestPluginPaths();
        context.WriteInstalledPlugin("com.acme.foo", "1.0.0");
        var dataDirectory = Path.Combine(context.Paths.PluginDataDirectory, "com.acme.foo");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, "cache.json"), "{}");
        var catalog = new PluginCatalog(context.Paths);
        await catalog.SetEnabledAsync("com.acme.foo", true, CancellationToken.None);

        await catalog.UninstallAsync("com.acme.foo", CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(context.Paths.PluginsDirectory, "com.acme.foo")));
        Assert.False(Directory.Exists(dataDirectory));
        Assert.Empty(catalog.Installed);

        var stateJson = File.Exists(context.Paths.RegistryFilePath)
            ? await File.ReadAllTextAsync(context.Paths.RegistryFilePath)
            : string.Empty;
        Assert.DoesNotContain("com.acme.foo", stateJson);
    }
}
