using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Plugins;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Plugins.Tests.Runtime;

/// <summary>
/// 插件宿主 DTO → 统一 <see cref="UsageRecord"/> 的映射层：会话命名空间、字段搬运、去重、
/// 校验丢弃计数，以及 HasChanges / 故障时的 last-known-good 语义。
/// </summary>
public class PluginUsageSourceTests
{
    [Fact]
    public async Task Scan_maps_dto_to_usage_record_with_namespaced_session()
    {
        var host = new FakeHostClient(OkOutcome(ValidDto() with { SessionId = "1" }));
        var source = new PluginUsageSource(Installation("com.acme.foo", "Foo"), host);
        var result = await source.ScanAsync(CancellationToken.None);
        var record = Assert.Single(result.Records);
        Assert.Equal("plugin:com.acme.foo:1", record.SessionId);
        Assert.Equal("Foo", record.Agent);
        Assert.Equal(record.InputTokens + record.OutputTokens, record.TotalTokens);
    }

    [Fact]
    public async Task Duplicate_record_ids_are_dropped_and_counted()
    {
        var host = new FakeHostClient(OkOutcome(ValidDto(), ValidDto())); // 同 RecordId
        var result = await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None);
        Assert.Single(result.Records);
        Assert.Equal(1, result.Diagnostics.DuplicateRecords);
    }

    [Fact]
    public async Task Failure_returns_last_known_good_records_with_error_status()
    {
        var host = new FakeHostClient(OkOutcome(ValidDto()), thenThrow: new TimeoutException());
        var source = new PluginUsageSource(Installation(), host);
        await source.ScanAsync(CancellationToken.None);
        var second = await source.ScanAsync(CancellationToken.None);
        Assert.Single(second.Records);
        Assert.Equal(JsonlScanStatus.Error, second.Diagnostics.Status);
        Assert.Equal(0, second.Diagnostics.FilesChanged);
    }

    [Fact]
    public async Task No_last_good_and_no_changes_returns_error()
    {
        var host = new FakeHostClient(new PluginScanOutcome(false, [], OkDiagnostics()));
        var result = await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None);
        Assert.Empty(result.Records);
        Assert.Equal(JsonlScanStatus.Error, result.Diagnostics.Status);
    }

    [Fact]
    public async Task Host_failure_without_last_good_returns_empty_error()
    {
        var source = new PluginUsageSource(Installation(), new ThrowingHostClient(new PluginHostException("boom")));
        var result = await source.ScanAsync(CancellationToken.None);
        Assert.Empty(result.Records);
        Assert.Equal(JsonlScanStatus.Error, result.Diagnostics.Status);
        Assert.Equal(0, result.Diagnostics.FilesChanged);
    }

    [Fact]
    public async Task Unexpected_host_exception_is_swallowed_and_last_good_is_retained()
    {
        var host = new FakeHostClient(OkOutcome(ValidDto()), thenThrow: new InvalidOperationException("boom"));
        var source = new PluginUsageSource(Installation(), host);
        await source.ScanAsync(CancellationToken.None);

        var second = await source.ScanAsync(CancellationToken.None);

        Assert.Single(second.Records);
        Assert.Equal(JsonlScanStatus.Error, second.Diagnostics.Status);
    }

    [Fact]
    public async Task HasChanges_false_reuses_cached_snapshot_without_changes()
    {
        var host = new FakeHostClient(
            OkOutcome(ValidDto()),
            new PluginScanOutcome(false, [], OkDiagnostics()));
        var source = new PluginUsageSource(Installation(), host);

        var first = await source.ScanAsync(CancellationToken.None);
        var second = await source.ScanAsync(CancellationToken.None);

        Assert.Single(first.Records);
        Assert.Equal(1, first.Diagnostics.FilesChanged);
        Assert.Single(second.Records);
        Assert.Equal(0, second.Diagnostics.FilesChanged);
        Assert.Equal(JsonlScanStatus.Ok, second.Diagnostics.Status);
    }

    [Fact]
    public async Task First_scan_requests_full_and_cached_scan_does_not()
    {
        var host = new FakeHostClient(
            OkOutcome(ValidDto()),
            new PluginScanOutcome(false, [], OkDiagnostics()));
        var source = new PluginUsageSource(Installation(), host);

        await source.ScanAsync(CancellationToken.None);
        await source.ScanAsync(CancellationToken.None);

        Assert.Equal(new[] { true, false }, host.ForceFullRequests);
    }

    [Fact]
    public async Task HasChanges_false_without_last_good_is_an_error()
    {
        // 没有缓存时宿主自报无变化是非法状态：首次扫描必须给全量快照。
        var host = new FakeHostClient(new PluginScanOutcome(false, [], OkDiagnostics()));
        var result = await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None);

        Assert.Empty(result.Records);
        Assert.Equal(JsonlScanStatus.Error, result.Diagnostics.Status);
        Assert.True(host.ForceFullRequests[0], "没有 last-known-good 时必须向宿主请求全量");
    }

    [Fact]
    public async Task Fresh_empty_snapshot_is_NoData_not_error()
    {
        var host = new FakeHostClient(new PluginScanOutcome(
            true, [], new PluginDiagnostics { Status = PluginScanStatus.NoData }));
        var result = await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None);

        Assert.Empty(result.Records);
        Assert.Equal(JsonlScanStatus.NoData, result.Diagnostics.Status);
        Assert.Equal(1, result.Diagnostics.FilesChanged);
    }

    [Fact]
    public async Task Invalid_records_are_dropped_and_counted()
    {
        var invalid = ValidDto("bad", "bad-session") with { InputTokens = -1 };
        var valid = ValidDto("ok", "good-session");
        var host = new FakeHostClient(OkOutcome(invalid, valid));

        var result = await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None);

        var record = Assert.Single(result.Records);
        Assert.Equal("plugin:com.acme.foo:good-session", record.SessionId);
        Assert.Equal(1, result.Diagnostics.InvalidRecords);
        Assert.Equal(0, result.Diagnostics.LinesSkipped); // 无效记录只计 InvalidRecords，不混入 LinesSkipped
    }

    [Fact]
    public async Task Null_record_in_batch_is_counted_invalid_and_siblings_still_map()
    {
        // 宿主流由 source-gen JSON 反序列化，批量里可能出现 null：不得抛，按无效记录丢弃。
        PluginUsageRecord? nullRecord = null;
        var host = new FakeHostClient(OkOutcome(nullRecord!, ValidDto("ok", "good-session")));

        var result = await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None);

        var record = Assert.Single(result.Records);
        Assert.Equal("plugin:com.acme.foo:good-session", record.SessionId);
        Assert.Equal(1, result.Diagnostics.InvalidRecords);
    }

    [Fact]
    public async Task Host_reported_error_outcome_retains_last_good()
    {
        var host = new FakeHostClient(
            OkOutcome(ValidDto()),
            new PluginScanOutcome(true, [], new PluginDiagnostics { Status = PluginScanStatus.Error }));
        var source = new PluginUsageSource(Installation(), host);

        var first = await source.ScanAsync(CancellationToken.None);
        var second = await source.ScanAsync(CancellationToken.None);

        Assert.Single(first.Records);
        Assert.Single(second.Records);
        Assert.Equal(JsonlScanStatus.Error, second.Diagnostics.Status);
        Assert.Equal(0, second.Diagnostics.FilesChanged);
    }

    [Fact]
    public async Task Null_records_batch_is_contained_as_error()
    {
        var host = new FakeHostClient(new PluginScanOutcome(true, null!, OkDiagnostics()));

        var result = await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None);

        Assert.Empty(result.Records);
        Assert.Equal(JsonlScanStatus.Error, result.Diagnostics.Status);
    }

    [Fact]
    public async Task Mapping_failure_retains_last_good()
    {
        var host = new FakeHostClient(
            OkOutcome(ValidDto()),
            new PluginScanOutcome(true, null!, OkDiagnostics()));
        var source = new PluginUsageSource(Installation(), host);
        await source.ScanAsync(CancellationToken.None);

        var second = await source.ScanAsync(CancellationToken.None);

        Assert.Single(second.Records);
        Assert.Equal(JsonlScanStatus.Error, second.Diagnostics.Status);
        Assert.Equal(0, second.Diagnostics.FilesChanged);
    }

    [Fact]
    public async Task Host_drop_counts_are_added_to_local_counts()
    {
        var dto = ValidDto();
        var host = new FakeHostClient(new PluginScanOutcome(
            true,
            [dto, dto],
            new PluginDiagnostics
            {
                Status = PluginScanStatus.Partial,
                InvalidRecords = 3,
                DuplicateRecords = 2,
                FilesDiscovered = 4,
                FilesRead = 2,
            }));

        var result = await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None);

        Assert.Single(result.Records);
        Assert.Equal(3, result.Diagnostics.InvalidRecords);
        Assert.Equal(3, result.Diagnostics.DuplicateRecords); // 宿主 2 + 本地 1
        Assert.Equal(4, result.Diagnostics.FilesDiscovered);
        Assert.Equal(2, result.Diagnostics.FilesRead);
        Assert.Equal(JsonlScanStatus.Partial, result.Diagnostics.Status);
    }

    [Fact]
    public async Task Cost_and_model_diagnostics_are_computed_over_result_set()
    {
        var withCost = ValidDto("r1", "s1") with { CostUsd = 0.5m };
        var withoutCost = ValidDto("r2", "s2") with { CostUsd = null };
        var host = new FakeHostClient(OkOutcome(withCost, withoutCost));

        var result = await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None);

        Assert.Equal(2, result.Records.Count);
        Assert.Equal(1, result.Diagnostics.MissingCostRecords);
        Assert.Equal(0, result.Diagnostics.UnknownModelRecords);
    }

    [Theory]
    [InlineData(PluginSpeedTier.Standard, SpeedTier.Standard)]
    [InlineData(PluginSpeedTier.Fast, SpeedTier.Fast)]
    [InlineData(PluginSpeedTier.Flex, SpeedTier.Flex)]
    [InlineData(PluginSpeedTier.Ultrafast, SpeedTier.Ultrafast)]
    public async Task Speed_tier_is_mapped(PluginSpeedTier pluginTier, SpeedTier expected)
    {
        var host = new FakeHostClient(OkOutcome(ValidDto() with { Speed = pluginTier }));

        var result = await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None);

        Assert.Equal(expected, Assert.Single(result.Records).Speed);
    }

    [Fact]
    public async Task Scan_carries_every_dto_field()
    {
        var dto = new PluginUsageRecord
        {
            RecordId = "r1",
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)),
            SessionId = "s1",
            ProjectPath = "/work/foo",
            ProjectDisplayName = "Foo Workspace",
            Model = "foo-large",
            Provider = "acme",
            InputTokens = 10,
            OutputTokens = 20,
            CacheCreationTokens = 3,
            CacheCreationEphemeral5mTokens = 1,
            CacheCreationEphemeral1hTokens = 2,
            CacheReadTokens = 4,
            CostUsd = 1.25m,
            Speed = PluginSpeedTier.Fast,
        };
        var host = new FakeHostClient(OkOutcome(dto));

        var record = Assert.Single(
            (await new PluginUsageSource(Installation(), host).ScanAsync(CancellationToken.None)).Records);

        Assert.Equal(dto.Timestamp, record.Timestamp);
        Assert.Equal("/work/foo", record.ProjectPath);
        Assert.Equal("Foo Workspace", record.ProjectDisplayName);
        Assert.Equal("foo-large", record.Model);
        Assert.Equal("acme", record.Provider);
        Assert.Equal(10, record.InputTokens);
        Assert.Equal(20, record.OutputTokens);
        Assert.Equal(3, record.CacheCreationTokens);
        Assert.Equal(1, record.CacheCreationEphemeral5mTokens);
        Assert.Equal(2, record.CacheCreationEphemeral1hTokens);
        Assert.Equal(4, record.CacheReadTokens);
        Assert.Equal(1.25m, record.CostUsd);
        Assert.Equal(37, record.TotalTokens);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var host = new FakeHostClient(OkOutcome(ValidDto()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PluginUsageSource(Installation(), host).ScanAsync(cancellation.Token));
    }

    private static PluginScanOutcome OkOutcome(params PluginUsageRecord[] records) => new(
        HasChanges: true,
        Records: records,
        Diagnostics: OkDiagnostics());

    private static PluginDiagnostics OkDiagnostics() => new() { Status = PluginScanStatus.Ok };

    private static PluginUsageRecord ValidDto(string recordId = "r1", string sessionId = "s1") => new()
    {
        RecordId = recordId,
        Timestamp = DateTimeOffset.UnixEpoch,
        SessionId = sessionId,
        ProjectPath = "/work/foo",
        Model = "foo-large",
        InputTokens = 10,
        OutputTokens = 5,
    };

    private static PluginInstallation Installation(string pluginId = "com.acme.foo", string displayName = "Foo") => new(
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
}

/// <summary>
/// 可编排的假宿主客户端：单/多轮结果 + 从第二轮起抛出指定异常；
/// 记录每次调用请求的 forceFull，便于断言首次全量语义。
/// </summary>
internal sealed class FakeHostClient : IPluginHostClient
{
    private readonly Queue<PluginScanOutcome> _queued;
    private readonly PluginScanOutcome? _repeat;
    private readonly Exception? _thenThrow;

    public FakeHostClient(PluginScanOutcome outcome, Exception? thenThrow = null)
    {
        _repeat = outcome;
        _thenThrow = thenThrow;
        _queued = new Queue<PluginScanOutcome>();
    }

    public FakeHostClient(params PluginScanOutcome[] outcomes)
    {
        _queued = new Queue<PluginScanOutcome>(outcomes);
    }

    public int Calls { get; private set; }

    public List<bool> ForceFullRequests { get; } = [];

    public Task<PluginScanOutcome> ScanAsync(
        PluginInstallation installation,
        bool forceFull,
        CancellationToken cancellationToken)
    {
        Calls++;
        ForceFullRequests.Add(forceFull);
        cancellationToken.ThrowIfCancellationRequested();

        if (_thenThrow is not null && Calls > 1)
        {
            return Task.FromException<PluginScanOutcome>(_thenThrow);
        }

        if (_queued.Count > 0)
        {
            return Task.FromResult(_queued.Dequeue());
        }

        if (_repeat is not null)
        {
            return Task.FromResult(_repeat);
        }

        return Task.FromException<PluginScanOutcome>(
            new InvalidOperationException("FakeHostClient ran out of outcomes"));
    }

    public void Remove(string pluginId)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>每次扫描都抛指定异常的假宿主客户端。</summary>
internal sealed class ThrowingHostClient : IPluginHostClient
{
    private readonly Exception _exception;

    public ThrowingHostClient(Exception exception) => _exception = exception;

    public Task<PluginScanOutcome> ScanAsync(
        PluginInstallation installation,
        bool forceFull,
        CancellationToken cancellationToken)
        => Task.FromException<PluginScanOutcome>(_exception);

    public void Remove(string pluginId)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
