namespace Tokenmetry.App.Tests.Services;

using Avalonia.Media;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Infrastructure.Plugins;
using Tokenmetry.Plugin.Abstractions;

/// <summary>
/// 插件 Agent 图标解析（Task 7）：已安装插件的 Manifest.DisplayName 与 facts 里的 Agent 键一致，
/// 因此按显示名命中即给自定义 PNG 图标（走 generic 槽位，内置品牌标不受影响）；未命中或
/// 无目录/加载失败时委托 <see cref="AgentVisualMap"/> 的既有内置判定。
/// </summary>
public sealed class AgentIconServiceTests
{
    [Fact]
    public void Plugin_display_name_resolves_custom_icon()
    {
        var catalog = new FakePluginCatalog([Installation("com.acme.foo", "Foo")]);
        var service = new AgentIconService(catalog, new FakeIconLoader());
        var visual = service.Resolve("Foo");
        Assert.NotNull(visual.CustomIcon);
        Assert.Equal("generic", visual.LogoVendor);
    }

    [Fact]
    public void Builtin_agent_is_unchanged()
    {
        var service = AgentIconService.BuiltInOnly;
        Assert.Equal("claudecode", service.Resolve("Claude Code").LogoVendor);
        Assert.Null(service.Resolve("Claude Code").CustomIcon);
    }

    [Fact]
    public void Unknown_agent_falls_back_to_glyph_without_icon()
    {
        var catalog = new FakePluginCatalog([Installation("com.acme.foo", "Foo")]);
        var service = new AgentIconService(catalog, new FakeIconLoader());
        var visual = service.Resolve("Bar");
        Assert.Equal("generic", visual.LogoVendor);
        Assert.Null(visual.CustomIcon);
        Assert.Equal(AgentVisualMap.Glyph("Bar"), visual.Glyph);
    }

    [Fact]
    public void Plugin_name_without_loader_stays_generic()
    {
        var catalog = new FakePluginCatalog([Installation("com.acme.foo", "Foo")]);
        var service = new AgentIconService(catalog, loader: null);
        var visual = service.Resolve("Foo");
        Assert.Equal("generic", visual.LogoVendor);
        Assert.Null(visual.CustomIcon);
    }

    [Fact]
    public void Loader_failure_returns_null_icon()
    {
        var catalog = new FakePluginCatalog([Installation("com.acme.foo", "Foo")]);
        var service = new AgentIconService(catalog, new FakeIconLoader { Succeeds = false });
        var visual = service.Resolve("Foo");
        Assert.Equal("generic", visual.LogoVendor);
        Assert.Null(visual.CustomIcon);
    }

    [Fact]
    public void Null_catalog_delegates_to_builtin_map()
    {
        var service = new AgentIconService(null, null);
        var visual = service.Resolve("Codex");
        Assert.Equal("codex", visual.LogoVendor);
        Assert.Null(visual.CustomIcon);
    }

    private static PluginInstallation Installation(string id, string displayName) =>
        new(
            new PluginManifest { Id = id, AgentId = id, DisplayName = displayName },
            Directory: "/plugins/" + id,
            IconPath: "/plugins/" + id + "/icon.png",
            Enabled: true);

    private sealed class FakePluginCatalog(IReadOnlyList<PluginInstallation> installed) : IPluginCatalog
    {
        public IReadOnlyList<PluginInstallation> Installed { get; } = installed;

        public IReadOnlyList<PluginCatalogError> Invalid => [];

        public Task ReloadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UninstallAsync(string pluginId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeIconLoader : IPluginIconLoader
    {
        private static readonly IImage Icon = new DrawingImage();

        public bool Succeeds { get; init; } = true;

        public IImage? Load(string path) => Succeeds ? Icon : null;
    }
}
