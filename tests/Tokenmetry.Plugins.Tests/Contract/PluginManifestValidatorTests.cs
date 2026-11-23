using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Plugins.Tests.Contract;

public class PluginManifestValidatorTests
{
    private static PluginManifest ValidManifest() => new()
    {
        SchemaVersion = 1,
        Id = "com.acme.foo",
        AgentId = "com.acme.foo",
        DisplayName = "Foo",
        Version = "1.0.0",
        ApiVersion = "1",
        EntryAssembly = "Foo.Plugin.dll",
        EntryType = "Foo.Plugin.FooUsagePlugin",
        Icon = "icon.png",
    };

    [Fact]
    public void Valid_manifest_has_no_errors() =>
        Assert.Empty(PluginManifestValidator.Validate(ValidManifest()));

    [Fact]
    public void Missing_display_name_is_rejected() =>
        Assert.Contains(
            PluginManifestValidator.Validate(ValidManifest() with { DisplayName = "" }),
            error => error.Contains("displayName", StringComparison.Ordinal));

    [Fact]
    public void Unsupported_api_version_is_rejected() =>
        Assert.Contains(
            PluginManifestValidator.Validate(ValidManifest() with { ApiVersion = "2" }),
            error => error.Contains("apiVersion", StringComparison.Ordinal));
}
