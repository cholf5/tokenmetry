using System.Text.Json;
using Tokenmetry.Infrastructure.Plugins;

namespace Tokenmetry.Plugins.Tests.Runtime;

/// <summary>
/// 安装器：校验 → staging 解压 → 再校验 → 原子移动到 plugins/&lt;id&gt;/&lt;version&gt; →
/// 删除旧版本 → 写 plugins.json（默认启用）→ 重载目录。失败不得破坏已有安装。
/// </summary>
public class PluginInstallerTests
{
    [Fact]
    public async Task Install_extracts_plugin_and_enables_it()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: []);
        using var package = TestPackage.CreateValid();

        var installed = await installer.InstallAsync(package.Path, CancellationToken.None);

        Assert.Equal(
            Path.Combine(paths.Paths.PluginsDirectory, "com.acme.foo", "1.0.0"),
            installed.Directory);
        Assert.True(File.Exists(Path.Combine(installed.Directory, "Foo.Plugin.dll")));
        Assert.True(File.Exists(Path.Combine(installed.Directory, "icon.png")));
        Assert.True(installed.Enabled);

        var single = Assert.Single(catalog.Installed);
        Assert.True(single.Enabled);
        Assert.Empty(catalog.Invalid);
        Assert.Equal("com.acme.foo", single.Manifest.Id);
        Assert.Equal("1.0.0", single.Manifest.Version);
    }

    [Fact]
    public async Task Install_writes_state_file_with_enabled_true()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: []);
        using var package = TestPackage.CreateValid();

        await installer.InstallAsync(package.Path, CancellationToken.None);

        var stateJson = await File.ReadAllTextAsync(paths.Paths.RegistryFilePath);
        using var document = JsonDocument.Parse(stateJson);
        Assert.True(document.RootElement.GetProperty("plugins").GetProperty("com.acme.foo").GetBoolean());
    }

    [Fact]
    public async Task Upgrade_replaces_old_version_directory()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: []);

        using (var v1 = TestPackage.CreateValid(version: "1.0.0"))
        {
            await installer.InstallAsync(v1.Path, CancellationToken.None);
        }

        using (var v2 = TestPackage.CreateValid(version: "2.0.0"))
        {
            await installer.InstallAsync(v2.Path, CancellationToken.None);
        }

        Assert.False(Directory.Exists(Path.Combine(paths.Paths.PluginsDirectory, "com.acme.foo", "1.0.0")));
        Assert.True(Directory.Exists(Path.Combine(paths.Paths.PluginsDirectory, "com.acme.foo", "2.0.0")));
        var single = Assert.Single(catalog.Installed);
        Assert.Equal("2.0.0", single.Manifest.Version);
    }

    [Fact]
    public void Preview_throws_for_invalid_package()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: []);
        using var package = TestPackage.CreateRaw(
            ("plugin.json", System.Text.Encoding.UTF8.GetBytes(ValidManifest.Json()), 0),
            ("Foo.Plugin.dll", System.Text.Encoding.UTF8.GetBytes("x"), 0),
            ("icon.png", System.Text.Encoding.UTF8.GetBytes("not a png"), 0));

        Assert.Throws<PluginInstallException>(() => { installer.Preview(package.Path); });
    }

    [Fact]
    public async Task Invalid_install_leaves_existing_installation_untouched()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: []);

        using (var v1 = TestPackage.CreateValid(version: "1.0.0"))
        {
            await installer.InstallAsync(v1.Path, CancellationToken.None);
        }

        using var broken = TestPackage.CreateRaw(
            ("plugin.json", System.Text.Encoding.UTF8.GetBytes(ValidManifest.Json(version: "2.0.0")), 0),
            ("Foo.Plugin.dll", System.Text.Encoding.UTF8.GetBytes("x"), 0),
            ("icon.png", System.Text.Encoding.UTF8.GetBytes("not a png"), 0));

        await Assert.ThrowsAsync<PluginInstallException>(
            () => installer.InstallAsync(broken.Path, CancellationToken.None));

        var single = Assert.Single(catalog.Installed);
        Assert.Equal("1.0.0", single.Manifest.Version);
        Assert.True(File.Exists(
            Path.Combine(paths.Paths.PluginsDirectory, "com.acme.foo", "1.0.0", "Foo.Plugin.dll")));
        Assert.False(Directory.Exists(
            Path.Combine(paths.Paths.PluginsDirectory, "com.acme.foo", "2.0.0")));
    }

    [Fact]
    public async Task Preview_rejects_display_name_colliding_with_installed_plugin()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: []);

        using (var first = TestPackage.CreateValid(id: "com.acme.foo", displayName: "Foo"))
        {
            await installer.InstallAsync(first.Path, CancellationToken.None);
        }

        using var second = TestPackage.CreateValid(id: "com.acme.bar", displayName: "Foo");

        Assert.Throws<PluginInstallException>(() => { installer.Preview(second.Path); });
    }

    [Fact]
    public void Preview_rejects_display_name_colliding_with_reserved_agent()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: ["Claude Code"]);
        using var package = TestPackage.CreateValid(displayName: "Claude Code");

        Assert.Throws<PluginInstallException>(() => { installer.Preview(package.Path); });
    }

    [Fact]
    public async Task Upgrade_with_same_display_name_is_allowed()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: []);

        using (var v1 = TestPackage.CreateValid(version: "1.0.0", displayName: "Foo"))
        {
            await installer.InstallAsync(v1.Path, CancellationToken.None);
        }

        using (var v2 = TestPackage.CreateValid(version: "2.0.0", displayName: "Foo"))
        {
            await installer.InstallAsync(v2.Path, CancellationToken.None);
        }

        Assert.Equal("2.0.0", Assert.Single(catalog.Installed).Manifest.Version);
    }

    [Fact]
    public async Task Preview_warns_when_upgrade_changes_display_name()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: []);

        using (var v1 = TestPackage.CreateValid(version: "1.0.0", displayName: "Foo"))
        {
            await installer.InstallAsync(v1.Path, CancellationToken.None);
        }

        using var v2 = TestPackage.CreateValid(version: "2.0.0", displayName: "Foo Prime");
        var preview = installer.Preview(v2.Path);

        var warning = Assert.Single(preview.Warnings);
        Assert.Contains("Foo", warning);
        Assert.Contains("Foo Prime", warning);
    }

    [Fact]
    public async Task Install_does_not_touch_plugin_data_directory()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: []);

        var dataDirectory = Path.Combine(paths.Paths.PluginDataDirectory, "com.acme.foo");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "cache.json"), "{}");

        using var package = TestPackage.CreateValid();
        await installer.InstallAsync(package.Path, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(dataDirectory, "cache.json")));
    }

    [Fact]
    public async Task State_write_failure_aborts_before_touching_existing_installation()
    {
        using var paths = new TestPluginPaths();
        var catalog = new PluginCatalog(paths.Paths);
        var installer = new PluginInstaller(catalog, paths.Paths, reservedAgentNames: []);

        using (var v1 = TestPackage.CreateValid(version: "1.0.0"))
        {
            await installer.InstallAsync(v1.Path, CancellationToken.None);
        }

        var v1Directory = Path.Combine(paths.Paths.PluginsDirectory, "com.acme.foo", "1.0.0");
        var v1Entry = Path.Combine(v1Directory, "Foo.Plugin.dll");
        var v1EntryBytes = File.ReadAllBytes(v1Entry);

        // 让状态写入必定失败：注册表路径先被同名目录占据，原子 File.Move 覆盖目录会抛 IO 异常。
        File.Delete(paths.Paths.RegistryFilePath);
        Directory.CreateDirectory(paths.Paths.RegistryFilePath);

        using var v2 = TestPackage.CreateValid(version: "2.0.0");
        await Assert.ThrowsAsync<PluginInstallException>(
            () => installer.InstallAsync(v2.Path, CancellationToken.None));

        Assert.True(Directory.Exists(v1Directory));
        Assert.Equal(v1EntryBytes, File.ReadAllBytes(v1Entry));
        Assert.False(Directory.Exists(
            Path.Combine(paths.Paths.PluginsDirectory, "com.acme.foo", "2.0.0")));
    }
}
