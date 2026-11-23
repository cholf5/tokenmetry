using Tokenmetry.Infrastructure.Plugins;
using Tokenmetry.Plugin.Abstractions;
using Tokenmetry.Plugins.Tests.Host;

namespace Tokenmetry.Plugins.Tests.Runtime;

/// <summary>
/// 主进程宿主客户端：按 plugin id 复用进程、新建进程强制全量、超时杀进程、
/// 失败后下次重扫重建进程，以及一次真实宿主 + fixture 插件的端到端扫描。
/// </summary>
public class PluginHostClientTests
{
    [Fact]
    public async Task Scan_reuses_process_for_same_plugin()
    {
        var process = new FakeHostProcess(OkOutcome());
        var factory = new FakeHostProcessFactory(process);
        var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

        await client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None);
        await client.ScanAsync(Installation("com.acme.foo"), forceFull: false, CancellationToken.None);

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(2, process.ScanCount);
    }

    [Fact]
    public async Task Timeout_kills_process_and_throws()
    {
        var process = new FakeHostProcess(OkOutcome(), delay: TimeSpan.FromSeconds(5));
        var client = new PluginHostClient(new FakeHostProcessFactory(process), TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None));

        Assert.True(process.Killed);
    }

    [Fact]
    public async Task New_process_forces_full_scan()
    {
        var process = new FakeHostProcess(OkOutcome());
        var client = new PluginHostClient(new FakeHostProcessFactory(process), TimeSpan.FromMinutes(1));

        await client.ScanAsync(Installation("com.acme.foo"), forceFull: false, CancellationToken.None);

        Assert.True(process.LastForceFull);
    }

    [Fact]
    public async Task Reused_process_honours_caller_forceFull()
    {
        var process = new FakeHostProcess(OkOutcome());
        var client = new PluginHostClient(new FakeHostProcessFactory(process), TimeSpan.FromMinutes(1));

        await client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None);
        await client.ScanAsync(Installation("com.acme.foo"), forceFull: false, CancellationToken.None);

        Assert.False(process.LastForceFull);
    }

    [Fact]
    public async Task Different_plugin_ids_get_different_processes()
    {
        var factory = new FakeHostProcessFactory(() => new FakeHostProcess(OkOutcome()));
        var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

        await client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None);
        await client.ScanAsync(Installation("com.acme.bar"), forceFull: true, CancellationToken.None);

        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task Remove_disposes_process_and_next_scan_recreates_full()
    {
        var factory = new FakeHostProcessFactory(() => new FakeHostProcess(OkOutcome()));
        var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

        await client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None);
        var first = factory.Created[0];

        client.Remove("com.acme.foo");

        Assert.True(first.Killed);

        await client.ScanAsync(Installation("com.acme.foo"), forceFull: false, CancellationToken.None);

        Assert.Equal(2, factory.CreateCount);
        Assert.True(factory.Created[1].LastForceFull);
    }

    [Fact]
    public async Task Failed_process_is_discarded_and_recreated_on_next_scan()
    {
        var processes = new Queue<FakeHostProcess>(
        [
            new FakeHostProcess(OkOutcome(), failure: new PluginHostException("host crashed")),
            new FakeHostProcess(OkOutcome()),
        ]);
        var factory = new FakeHostProcessFactory(_ => processes.Dequeue());
        var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<PluginHostException>(() =>
            client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None));

        var outcome = await client.ScanAsync(
            Installation("com.acme.foo"), forceFull: false, CancellationToken.None);

        Assert.Equal(2, factory.CreateCount);
        Assert.True(factory.Created[0].Killed);
        Assert.True(factory.Created[1].LastForceFull);
        Assert.True(outcome.HasChanges);
    }

    [Fact]
    public async Task HasChanges_is_preserved_from_host()
    {
        var process = new FakeHostProcess(new PluginScanOutcome(
            HasChanges: false,
            Records: [],
            Diagnostics: new PluginDiagnostics { Status = PluginScanStatus.NoData }));
        var client = new PluginHostClient(new FakeHostProcessFactory(process), TimeSpan.FromMinutes(1));

        var outcome = await client.ScanAsync(
            Installation("com.acme.foo"), forceFull: true, CancellationToken.None);

        Assert.False(outcome.HasChanges);
        Assert.Empty(outcome.Records);
        Assert.Equal(PluginScanStatus.NoData, outcome.Diagnostics.Status);
    }

    [Fact]
    public async Task Caller_cancellation_kills_process_and_propagates()
    {
        var process = new FakeHostProcess(OkOutcome(), delay: TimeSpan.FromSeconds(5));
        var client = new PluginHostClient(new FakeHostProcessFactory(process), TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ScanAsync(Installation("com.acme.foo"), forceFull: true, cancellation.Token));

        Assert.True(process.Killed);
    }

    [Fact]
    public async Task DisposeAsync_kills_all_host_processes()
    {
        var factory = new FakeHostProcessFactory(() => new FakeHostProcess(OkOutcome()));
        var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

        await client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None);
        await client.ScanAsync(Installation("com.acme.bar"), forceFull: true, CancellationToken.None);

        await client.DisposeAsync();

        Assert.All(factory.Created, process => Assert.True(process.Killed));
    }

    [Fact]
    public async Task Real_host_end_to_end_with_fixture_plugin()
    {
        var pluginDirectory = TestPluginDirectory.CopyToTemp();
        try
        {
            // 宿主协议里 --data-dir 由安装目录充当：fixture 从这里读 usage.jsonl。
            await File.WriteAllTextAsync(
                Path.Combine(pluginDirectory, "usage.jsonl"),
                """{"sessionId":"s1","projectPath":"/work/foo","model":"foo-large","inputTokens":10,"outputTokens":5}""");

            var manifest = PluginJson.DeserializeManifest(
                File.ReadAllText(Path.Combine(pluginDirectory, "plugin.json")))
                ?? throw new InvalidOperationException("fixture plugin.json was not valid JSON");
            var installation = new PluginInstallation(
                manifest,
                pluginDirectory,
                Path.Combine(pluginDirectory, manifest.Icon),
                Enabled: true);

            var factory = new PluginHostProcessFactory(
                typeof(Tokenmetry.PluginHost.PluginHostServer).Assembly.Location,
                log: null);
            await using var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

            var outcome = await client.ScanAsync(installation, forceFull: true, CancellationToken.None);

            Assert.NotEmpty(outcome.Records);
            Assert.True(outcome.HasChanges);
            Assert.Equal(PluginScanStatus.Ok, outcome.Diagnostics.Status);
            Assert.Equal("s1", outcome.Records[0].SessionId);
            Assert.Equal(10, outcome.Records[0].InputTokens);
        }
        finally
        {
            TryDelete(pluginDirectory);
        }
    }

    private static PluginScanOutcome OkOutcome() => new(
        HasChanges: true,
        Records:
        [
            new PluginUsageRecord
            {
                RecordId = "r1",
                Timestamp = DateTimeOffset.UnixEpoch,
                SessionId = "s1",
                ProjectPath = "/work/foo",
                Model = "foo-large",
                InputTokens = 1,
                OutputTokens = 1,
            },
        ],
        Diagnostics: new PluginDiagnostics { Status = PluginScanStatus.Ok });

    private static PluginInstallation Installation(string pluginId, string displayName = "Foo") => new(
        new PluginManifest
        {
            SchemaVersion = 1,
            Id = pluginId,
            AgentId = pluginId,
            DisplayName = displayName,
            Version = "1.0.0",
            ApiVersion = "1",
            EntryAssembly = "Foo.Plugin.dll",
            EntryType = "Foo.Plugin.FooUsagePlugin",
            Icon = "icon.png",
        },
        Directory: "/plugin",
        IconPath: "/plugin/icon.png",
        Enabled: true);

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>可编排的假宿主进程：记录调用、按需延迟/抛错，Dispose 即视为被杀。</summary>
internal sealed class FakeHostProcess : IPluginHostProcess
{
    private readonly PluginScanOutcome? _outcome;
    private readonly TimeSpan _delay;
    private readonly Exception? _failure;

    public FakeHostProcess(
        PluginScanOutcome? outcome = null,
        TimeSpan delay = default,
        Exception? failure = null)
    {
        _outcome = outcome;
        _delay = delay;
        _failure = failure;
    }

    public int ScanCount { get; private set; }

    public bool LastForceFull { get; private set; }

    public bool Killed { get; private set; }

    public async Task<PluginScanOutcome> ScanAsync(bool forceFull, CancellationToken cancellationToken)
    {
        ScanCount++;
        LastForceFull = forceFull;

        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, cancellationToken);
        }

        if (_failure is not null)
        {
            throw _failure;
        }

        return _outcome ?? throw new InvalidOperationException("fake host process was not given an outcome");
    }

    public ValueTask DisposeAsync()
    {
        Killed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>假进程工厂：可共享单个进程，也可按调用创建新进程并记录全部实例。</summary>
internal sealed class FakeHostProcessFactory : IPluginHostProcessFactory
{
    private readonly Func<PluginInstallation, FakeHostProcess> _create;

    public FakeHostProcessFactory(FakeHostProcess shared)
        : this(_ => shared)
    {
    }

    public FakeHostProcessFactory(Func<FakeHostProcess> create)
        : this(_ => create())
    {
    }

    public FakeHostProcessFactory(Func<PluginInstallation, FakeHostProcess> create)
    {
        _create = create;
    }

    public int CreateCount { get; private set; }

    public List<FakeHostProcess> Created { get; } = [];

    public IPluginHostProcess Create(PluginInstallation installation)
    {
        CreateCount++;
        var process = _create(installation);
        Created.Add(process);
        return process;
    }
}
