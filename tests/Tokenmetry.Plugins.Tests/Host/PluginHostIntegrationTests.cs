using System.Text;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.Plugins.Tests.Host;

/// <summary>
/// 宿主进程集成测试：真的起 `Tokenmetry.PluginHost` 子进程、真的按 NDJSON 走一遍协议，
/// fixture 插件读临时数据目录里的 usage.jsonl。
/// </summary>
public class PluginHostIntegrationTests
{
    [Fact]
    public async Task Host_scans_fixture_plugin_and_returns_records()
    {
        using var workspace = new TestWorkspace();
        await workspace.WriteUsageAsync(
            """{"sessionId":"s1","projectPath":"/work/foo","model":"foo-large","inputTokens":10,"outputTokens":5}""");

        var result = await PluginHostTestRunner.ScanAsync(workspace.PluginDirectory, workspace.DataDirectory);

        Assert.Equal(PluginScanStatus.Ok, result.Diagnostics.Status);
        Assert.True(result.HasChanges);
        Assert.Single(result.Records);
        Assert.Equal("s1", result.Records[0].SessionId);
        Assert.Equal(10, result.Records[0].InputTokens);
        Assert.Equal(5, result.Records[0].OutputTokens);
        Assert.Equal(1, result.BatchCount);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Invalid_records_are_counted_and_not_emitted()
    {
        using var workspace = new TestWorkspace();
        // 缺 sessionId：fixture 仍产出记录，宿主校验后拒绝。
        await workspace.WriteUsageAsync(
            """{"projectPath":"/work/foo","model":"foo-large","inputTokens":10,"outputTokens":5}""");

        var result = await PluginHostTestRunner.ScanAsync(workspace.PluginDirectory, workspace.DataDirectory);

        Assert.Empty(result.Records);
        Assert.Equal(1, result.Diagnostics.InvalidRecords);
        Assert.Equal(PluginScanStatus.Partial, result.Diagnostics.Status);
    }

    [Fact]
    public async Task Plugin_exception_returns_error_message()
    {
        using var workspace = new TestWorkspace();
        // fixture 读到非法 JSON 行会抛异常，宿主必须把它映射成 error 消息而不是崩溃/污染协议。
        await workspace.WriteUsageAsync("{ this is not valid json");

        var result = await PluginHostTestRunner.ScanAsync(workspace.PluginDirectory, workspace.DataDirectory);

        Assert.Equal(PluginScanStatus.Error, result.Diagnostics.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task Plugin_stdout_writes_do_not_corrupt_protocol()
    {
        using var workspace = new TestWorkspace();
        // fixture 每次 ScanAsync 都会 Console.WriteLine；宿主若不把 Console.Out 转到 stderr，
        // 这行调试输出会混进 NDJSON，下面的反序列化就会失败。
        await workspace.WriteUsageAsync(
            """{"sessionId":"s1","projectPath":"/work/foo","model":"foo-large","inputTokens":1,"outputTokens":1}""");

        var result = await PluginHostTestRunner.ScanAsync(workspace.PluginDirectory, workspace.DataDirectory);

        Assert.Equal(PluginScanStatus.Ok, result.Diagnostics.Status);
        Assert.Single(result.Records);
    }

    [Fact]
    public async Task Duplicate_record_ids_are_dropped_and_counted()
    {
        using var workspace = new TestWorkspace();
        // fixture 用 sessionId 作 RecordId：两行同 sessionId → 第二条按重复丢弃。
        await workspace.WriteUsageAsync(
            """
            {"sessionId":"s1","projectPath":"/work/foo","model":"foo-large","inputTokens":10,"outputTokens":5}
            {"sessionId":"s1","projectPath":"/work/bar","model":"foo-large","inputTokens":20,"outputTokens":7}
            """);

        var result = await PluginHostTestRunner.ScanAsync(workspace.PluginDirectory, workspace.DataDirectory);

        Assert.Single(result.Records);
        Assert.Equal(1, result.Diagnostics.DuplicateRecords);
        Assert.Equal("/work/foo", result.Records[0].ProjectPath);
    }

    [Fact]
    public async Task Missing_usage_file_returns_nodata()
    {
        using var workspace = new TestWorkspace();

        var result = await PluginHostTestRunner.ScanAsync(workspace.PluginDirectory, workspace.DataDirectory);

        Assert.Equal(PluginScanStatus.NoData, result.Diagnostics.Status);
        Assert.Empty(result.Records);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task Records_are_batched_in_groups_of_at_most_500()
    {
        using var workspace = new TestWorkspace();

        var builder = new StringBuilder();
        for (var i = 0; i < 1101; i++)
        {
            builder.Append("{\"sessionId\":\"s").Append(i)
                .Append("\",\"projectPath\":\"/work/foo\",\"model\":\"foo-large\",")
                .Append("\"inputTokens\":1,\"outputTokens\":1}\n");
        }

        await workspace.WriteUsageAsync(builder.ToString());

        var result = await PluginHostTestRunner.ScanAsync(workspace.PluginDirectory, workspace.DataDirectory);

        Assert.Equal(PluginScanStatus.Ok, result.Diagnostics.Status);
        Assert.Equal(1101, result.Records.Count);
        Assert.Equal(3, result.BatchCount); // 500 + 500 + 101
        Assert.Equal(0, result.ExitCode);
    }
}
