using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Plugins.Tests.Contract;

public class PluginJsonTests
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
        Author = "Acme Corp",
        Description = "Internal Foo agent usage source",
    };

    private static PluginUsageRecord ValidRecord() => new()
    {
        RecordId = "r1",
        Timestamp = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
        SessionId = "s1",
        ProjectPath = "/work/foo",
        ProjectDisplayName = "foo",
        Model = "foo-large",
        Provider = "acme",
        InputTokens = 10,
        OutputTokens = 5,
        CacheCreationTokens = 300,
        CacheCreationEphemeral5mTokens = 100,
        CacheCreationEphemeral1hTokens = 200,
        CacheReadTokens = 7,
        CostUsd = 0.42m,
        Speed = PluginSpeedTier.Fast,
    };

    [Fact]
    public void Manifest_round_trips()
    {
        var manifest = ValidManifest();
        Assert.Equal(manifest, PluginJson.DeserializeManifest(PluginJson.SerializeManifest(manifest)));
    }

    [Fact]
    public void Record_round_trips()
    {
        var record = ValidRecord();
        Assert.Equal(record, PluginJson.DeserializeRecord(PluginJson.SerializeRecord(record)));
    }

    [Fact]
    public void Manifest_uses_camel_case_wire_names()
    {
        var json = PluginJson.SerializeManifest(ValidManifest());
        Assert.Contains("\"schemaVersion\"", json);
        Assert.DoesNotContain("\"SchemaVersion\"", json);
    }

    [Fact]
    public void Contract_assembly_version_is_fixed_for_v1()
    {
        Assert.Equal(new Version(1, 0, 0, 0), typeof(IUsageSourcePlugin).Assembly.GetName().Version);
        Assert.Equal(1, PluginApiVersion.Current);
    }
}
