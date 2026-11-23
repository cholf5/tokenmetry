using System.Diagnostics;
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

        Assert.True(process.KillCalled);
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
    public async Task StopAll_kills_processes_but_allows_a_subsequent_scan()
    {
        var factory = new FakeHostProcessFactory(() => new FakeHostProcess(OkOutcome()));
        var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

        await client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None);
        await client.ScanAsync(Installation("com.acme.bar"), forceFull: true, CancellationToken.None);
        var first = factory.Created[0];

        client.StopAll();

        Assert.True(first.Killed);

        // 停掉不是永久关停：失败的更新回滚后，下一次扫描必须还能重建宿主。
        var outcome = await client.ScanAsync(
            Installation("com.acme.foo"), forceFull: false, CancellationToken.None);

        Assert.Equal(3, factory.CreateCount);
        Assert.True(factory.Created[^1].LastForceFull);
        Assert.True(outcome.HasChanges);
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
    public async Task Dead_process_during_scan_is_recreated_and_retried_once()
    {
        var processes = new Queue<FakeHostProcess>(
        [
            new FakeHostProcess(OkOutcome(), failure: new PluginHostException("host crashed")),
            new FakeHostProcess(OkOutcome()),
        ]);
        var factory = new FakeHostProcessFactory(_ => processes.Dequeue());
        var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

        // 一次调用内就恢复：进程死掉 → 重建 → 重试，不把失败抛给调用方。
        var outcome = await client.ScanAsync(
            Installation("com.acme.foo"), forceFull: false, CancellationToken.None);

        Assert.Equal(2, factory.CreateCount);
        Assert.True(factory.Created[0].Killed);
        Assert.True(factory.Created[1].LastForceFull);
        Assert.True(outcome.HasChanges);
    }

    [Fact]
    public async Task Cached_dead_process_is_recreated_before_scanning()
    {
        var factory = new FakeHostProcessFactory(() => new FakeHostProcess(OkOutcome()));
        var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

        await client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None);
        var first = factory.Created[0];
        first.HasExited = true;

        var outcome = await client.ScanAsync(
            Installation("com.acme.foo"), forceFull: false, CancellationToken.None);

        Assert.Equal(2, factory.CreateCount);
        Assert.True(first.Killed);
        Assert.Equal(1, first.ScanCount); // 已死进程上不再写请求
        Assert.True(factory.Created[1].LastForceFull);
        Assert.True(outcome.HasChanges);
    }

    [Fact]
    public async Task Recovery_retries_at_most_once()
    {
        var processes = new Queue<FakeHostProcess>(
        [
            new FakeHostProcess(OkOutcome(), failure: new PluginHostException("first")),
            new FakeHostProcess(OkOutcome(), failure: new PluginHostException("second")),
        ]);
        var factory = new FakeHostProcessFactory(_ => processes.Dequeue());
        var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<PluginHostException>(() =>
            client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None));

        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task Host_reported_error_is_not_retried()
    {
        // 宿主 self-report 的 error（插件抛异常）不是进程死亡：不重建、不重试。
        var process = new FakeHostProcess(
            OkOutcome(),
            failure: new PluginHostException("plugin threw"),
            markExitedOnFailure: false);
        var factory = new FakeHostProcessFactory(process);
        var client = new PluginHostClient(factory, TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<PluginHostException>(() =>
            client.ScanAsync(Installation("com.acme.foo"), forceFull: true, CancellationToken.None));

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, process.ScanCount);
        Assert.True(process.Killed);
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
        var pluginDataRoot = Directory.CreateTempSubdirectory("tokenmetry-plugin-data-").FullName;
        try
        {
            var manifest = PluginJson.DeserializeManifest(
                File.ReadAllText(Path.Combine(pluginDirectory, "plugin.json")))
                ?? throw new InvalidOperationException("fixture plugin.json was not valid JSON");

            // 宿主把 --data-dir 固定为 <root>/<pluginId>；fixture 从这里读 usage.jsonl。
            var dataDirectory = Directory.CreateDirectory(
                Path.Combine(pluginDataRoot, manifest.Id)).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(dataDirectory, "usage.jsonl"),
                """{"sessionId":"s1","projectPath":"/work/foo","model":"foo-large","inputTokens":10,"outputTokens":5}""");

            var installation = new PluginInstallation(
                manifest,
                pluginDirectory,
                Path.Combine(pluginDirectory, manifest.Icon),
                Enabled: true);

            var factory = new PluginHostProcessFactory(
                typeof(Tokenmetry.PluginHost.PluginHostServer).Assembly.Location,
                pluginDataRoot,
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
            TryDelete(pluginDataRoot);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("nested/id")]
    [InlineData("nested\\id")]
    [InlineData("/absolute")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public void Factory_rejects_unsafe_plugin_id(string pluginId)
    {
        var pluginDataRoot = Directory.CreateTempSubdirectory("tokenmetry-plugin-data-").FullName;
        try
        {
            var factory = new PluginHostProcessFactory(
                Path.Combine(pluginDataRoot, "missing-host.dll"),
                pluginDataRoot,
                log: null);

            Assert.Throws<PluginHostException>(() => factory.Create(Installation(pluginId)));

            // 校验在起进程/建目录之前完成：数据根下不应留下任何条目。
            Assert.Empty(Directory.EnumerateFileSystemEntries(pluginDataRoot));
        }
        finally
        {
            TryDelete(pluginDataRoot);
        }
    }

    [Fact]
    public async Task Real_host_timeout_kills_hung_process_and_next_scan_recreates()
    {
        var pluginDirectory = TestPluginDirectory.CopyToTemp();
        var pluginDataRoot = Directory.CreateTempSubdirectory("tokenmetry-plugin-data-").FullName;
        try
        {
            var manifest = PluginJson.DeserializeManifest(
                File.ReadAllText(Path.Combine(pluginDirectory, "plugin.json")))
                ?? throw new InvalidOperationException("fixture plugin.json was not valid JSON");
            var dataDirectory = Directory.CreateDirectory(
                Path.Combine(pluginDataRoot, manifest.Id)).FullName;

            // 挂起模式：插件永不返回、宿主永不写 stdout——只能靠客户端硬超时杀进程，
            // 走的是真实宿主进程与 Windows 匿名管道。
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "hang"), "1");

            var installation = new PluginInstallation(
                manifest,
                pluginDirectory,
                Path.Combine(pluginDirectory, manifest.Icon),
                Enabled: true);

            var factory = new CountingProcessFactory(new PluginHostProcessFactory(
                typeof(Tokenmetry.PluginHost.PluginHostServer).Assembly.Location,
                pluginDataRoot,
                log: null));
            await using var client = new PluginHostClient(factory, TimeSpan.FromSeconds(3));

            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(() =>
                client.ScanAsync(installation, forceFull: true, CancellationToken.None));
            stopwatch.Stop();

            // 超时没有卡在永不返回的管道读上，旧宿主进程已被杀/释放。
            Assert.Single(factory.Created);
            Assert.True(factory.Created[0].HasExited);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));

            // 去掉挂起标记、放入数据：下一次扫描必须重建进程并成功。
            File.Delete(Path.Combine(dataDirectory, "hang"));
            await File.WriteAllTextAsync(
                Path.Combine(dataDirectory, "usage.jsonl"),
                """{"sessionId":"s1","projectPath":"/work/foo","model":"foo-large","inputTokens":10,"outputTokens":5}""");

            var outcome = await client.ScanAsync(installation, forceFull: false, CancellationToken.None);

            Assert.Equal(2, factory.CreateCount);
            Assert.NotEmpty(outcome.Records);
        }
        finally
        {
            TryDelete(pluginDirectory);
            TryDelete(pluginDataRoot);
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
    private readonly bool _markExitedOnFailure;

    public FakeHostProcess(
        PluginScanOutcome? outcome = null,
        TimeSpan delay = default,
        Exception? failure = null,
        bool markExitedOnFailure = true)
    {
        _outcome = outcome;
        _delay = delay;
        _failure = failure;
        _markExitedOnFailure = markExitedOnFailure;
    }

    public int ScanCount { get; private set; }

    public bool LastForceFull { get; private set; }

    public bool Killed { get; private set; }

    public bool KillCalled { get; private set; }

    public bool HasExited { get; set; }

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
            if (_markExitedOnFailure)
            {
                HasExited = true;
            }

            throw _failure;
        }

        return _outcome ?? throw new InvalidOperationException("fake host process was not given an outcome");
    }

    public void Kill()
    {
        KillCalled = true;
        HasExited = true;
    }

    public ValueTask DisposeAsync()
    {
        Killed = true;
        HasExited = true;
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

/// <summary>包住真实工厂，记录创建次数与实例，供超时/重建断言使用。</summary>
internal sealed class CountingProcessFactory : IPluginHostProcessFactory
{
    private readonly IPluginHostProcessFactory _inner;

    public CountingProcessFactory(IPluginHostProcessFactory inner)
    {
        _inner = inner;
    }

    public int CreateCount { get; private set; }

    public List<IPluginHostProcess> Created { get; } = [];

    public IPluginHostProcess Create(PluginInstallation installation)
    {
        CreateCount++;
        var process = _inner.Create(installation);
        Created.Add(process);
        return process;
    }
}
