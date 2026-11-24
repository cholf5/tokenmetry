namespace Tokenmetry.Infrastructure.Tests.ClaudeCodeJsonl;

using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;

public class ClaudeCodeJsonlAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    /// <summary>TC-001: 样例 fixture 解析（fixture 素材取自 claude.rs 内联测试的真实 payload）。</summary>
    [Fact]
    public async Task Parses_sample_fixture_from_configured_root()
    {
        var root = CreateTempRoot();
        var fixture = await File.ReadAllTextAsync("fixtures/claude-code/sample-session.jsonl");
        WriteSessionFile(root, "projects/project-a/session-a.jsonl", fixture);
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: root);

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEquivalentTo(new[] { root });
        result.Records.Should().HaveCount(6);

        var direct = result.Records.Single(record => record.MessageId == "msg-a");
        direct.Model.Should().Be("claude-sonnet-4-20250514");
        direct.CostUsd.Should().Be(0.0123m);
        direct.InputTokens.Should().Be(123);
        direct.OutputTokens.Should().Be(45);
        direct.CacheCreationTokens.Should().Be(10);
        direct.CacheReadTokens.Should().Be(1000);
        direct.TotalTokens.Should().Be(1178);
        direct.SessionId.Should().Be("session-a");
        direct.ProjectPath.Should().Be("project-a");
        direct.Agent.Should().Be("Claude Code");
        direct.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 20, 10, 12, 30, TimeSpan.Zero));
        direct.SourceFile.Should().EndWith(Path.Combine("project-a", "session-a.jsonl"));

        var nestedCache = result.Records.Single(record => record.MessageId == "msg-b");
        nestedCache.CacheCreationTokens.Should().Be(90);
        nestedCache.CacheCreationEphemeral5mTokens.Should().Be(40);
        nestedCache.CacheCreationEphemeral1hTokens.Should().Be(50);

        var wrapper = result.Records.Single(record => record.MessageId == "msg-wrapper");
        wrapper.SessionId.Should().Be("session-a"); // 包装行回退到文件名推导的会话 id
        wrapper.IsSidechain.Should().BeTrue();

        var auto = result.Records.Single(record => record.Model == "auto");
        auto.MessageId.Should().BeNull();
        auto.RequestId.Should().BeNull();
        auto.TotalTokens.Should().Be(4_051_036);

        result.Records.Single(record => record.MessageId == "msg-synthetic").Model.Should().BeNull();
        result.Records.Single(record => record.MessageId == "msg-fast").Model.Should().Be("claude-sonnet-fast");

        var diagnostics = result.Diagnostics;
        diagnostics.Status.Should().Be(ClaudeCodeScanStatus.Ok);
        diagnostics.FilesDiscovered.Should().Be(1);
        diagnostics.FilesRead.Should().Be(1);
        diagnostics.LinesSkipped.Should().Be(1); // summary 行不含 usage 标记
        diagnostics.MalformedLines.Should().Be(0);
        diagnostics.InvalidRecords.Should().Be(0);
        diagnostics.DuplicateRecords.Should().Be(0);
        diagnostics.MissingCostRecords.Should().Be(3); // synthetic / fast / auto 三条无 costUSD
        diagnostics.UnknownModelRecords.Should().Be(1); // synthetic
    }

    [Fact]
    public async Task Multi_root_scan_combines_records_from_every_root()
    {
        var rootA = CreateTempRoot();
        var rootB = CreateTempRoot();
        WriteSessionFile(rootA, "projects/project-a/session-a.jsonl",
            await File.ReadAllTextAsync("fixtures/claude-code/sample-session.jsonl"));
        WriteSessionFile(rootB, "projects/project-b/session-b.jsonl",
            await File.ReadAllTextAsync("fixtures/claude-code/duplicate-messages.jsonl"));
        var locator = NewLocator(("CLAUDE_CONFIG_DIR", $"{rootA},{rootB}"));
        var adapter = new ClaudeCodeJsonlAdapter(locator: locator);

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEquivalentTo(new[] { rootA, rootB });
        // 6 + 4：duplicate fixture 里 msg-stream 无 requestId 且非 msg_/resp_ 前缀，两个时间戳
        // 不能证明是同一次响应（ccusage 20.0.45 口径）→ 保留两条；msg-replay 被 sidechain 兜底折叠。
        result.Records.Should().HaveCount(10);
        result.Records.Select(record => record.ProjectPath).Should().Contain("project-a");
        result.Records.Select(record => record.ProjectPath).Should().Contain("project-b");
        result.Diagnostics.FilesDiscovered.Should().Be(2);
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    /// <summary>
    /// 代理网关按 content block 逐行写同一次响应（共享 message id 与 usage，uuid/时间戳不同）：
    /// 沿 uuid/parentUuid + tool_use↔tool_result 链折叠成一条（ccusage 20.1.x 同口径）。
    /// </summary>
    [Fact]
    public async Task Collapses_proxied_multi_block_response_writes()
    {
        var root = CreateTempRoot();
        WriteSessionFile(root, "projects/project-p/session-proxy.jsonl",
            await File.ReadAllTextAsync("fixtures/claude-code/multi-block-proxy.jsonl"));
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: root);

        var result = await adapter.ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records.Single(record => record.MessageId == "gw-msg-1").TotalTokens.Should().Be(1110);
        result.Records.Single(record => record.MessageId == "gw-msg-2").TotalTokens.Should().Be(2220);
        result.Diagnostics.DuplicateRecords.Should().Be(2); // 第一次响应的 3 个 content block 折叠为 1 条
        result.Diagnostics.Status.Should().Be(ClaudeCodeScanStatus.Ok);
    }

    /// <summary>普通用户消息是轮次边界：网关复用同一占位 id 的两次响应必须各自记账。</summary>
    [Fact]
    public async Task Keeps_reused_gateway_message_id_for_separate_turns_apart()
    {
        var root = CreateTempRoot();
        WriteSessionFile(root, "projects/project-p/session-proxy.jsonl",
            await File.ReadAllTextAsync("fixtures/claude-code/multi-block-proxy-break.jsonl"));
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: root);

        var result = await adapter.ScanAsync();

        result.Records.Should().HaveCount(2, "链在普通用户消息处断开，不能把两轮真实请求折叠");
        result.Records.Should().OnlyContain(record => record.MessageId == "gw-msg-reused");
    }

    /// <summary>tool_result 引用的 tool_use 不属于父响应时，该行不能成为链的一环。</summary>
    [Fact]
    public async Task Tool_result_owned_by_another_response_breaks_the_chain()
    {
        var root = CreateTempRoot();
        var usage =
            """{"input_tokens":100,"output_tokens":10,"cache_read_input_tokens":1000}""";
        WriteSessionFile(root, "projects/project-p/session-proxy.jsonl",
            """{"parentUuid":null,"isSidechain":false,"type":"user","message":{"role":"user","content":"go"},"uuid":"u-1","sessionId":"s-p","timestamp":"2026-06-20T13:00:00.000Z"}""" + "\n"
            + $$"""{"parentUuid":"u-1","isSidechain":false,"type":"assistant","message":{"id":"gw-x","model":"deepseek","usage":{{usage}},"content":[{"type":"text","text":"one"}]},"uuid":"a-1","sessionId":"s-p","timestamp":"2026-06-20T13:00:01.000Z"}""" + "\n"
            + """{"parentUuid":"a-1","isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"other-tool","content":"ok"}]},"uuid":"u-2","sessionId":"s-p","timestamp":"2026-06-20T13:00:02.000Z"}""" + "\n"
            + $$"""{"parentUuid":"u-2","isSidechain":false,"type":"assistant","message":{"id":"gw-x","model":"deepseek","usage":{{usage}},"content":[{"type":"text","text":"two"}]},"uuid":"a-2","sessionId":"s-p","timestamp":"2026-06-20T13:00:03.000Z"}""" + "\n");
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: root);

        var result = await adapter.ScanAsync();

        result.Records.Should().HaveCount(2);
    }

    [Fact]
    public async Task Empty_projects_dir_returns_nodata()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: root);

        var result = await adapter.ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(ClaudeCodeScanStatus.NoData);
        result.Diagnostics.FilesDiscovered.Should().Be(0);
    }

    [Fact]
    public async Task Missing_root_returns_nodata_with_empty_roots()
    {
        var missing = Path.Combine(CreateTempRoot(), "does-not-exist");
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: missing);

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEmpty();
        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(ClaudeCodeScanStatus.NoData);
    }

    [Fact]
    public async Task Corrupted_lines_are_skipped_and_counted()
    {
        var root = CreateTempRoot();
        WriteSessionFile(root, "projects/project-a/session-a.jsonl",
            await File.ReadAllTextAsync("fixtures/claude-code/corrupted-lines.jsonl"));
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: root);

        var result = await adapter.ScanAsync();

        result.Records.Should().ContainSingle(); // 坏行不中断，合法记录仍保留
        result.Records.Single().MessageId.Should().Be("msg-ok");
        result.Diagnostics.Status.Should().Be(ClaudeCodeScanStatus.Partial);
        result.Diagnostics.LinesSkipped.Should().Be(2); // "not json" 与 summary 行
        result.Diagnostics.MalformedLines.Should().Be(1); // 含 usage 标记的坏 JSON
        result.Diagnostics.InvalidRecords.Should().Be(1); // 空 sessionId
    }

    /// <summary>TC-002: 重复运行幂等。</summary>
    [Fact]
    public async Task Repeated_scan_is_idempotent()
    {
        var root = CreateTempRoot();
        WriteSessionFile(root, "projects/project-b/session-b.jsonl",
            await File.ReadAllTextAsync("fixtures/claude-code/duplicate-messages.jsonl"));
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: root);

        var first = await adapter.ScanAsync();
        var second = await adapter.ScanAsync();

        first.Records.Should().HaveCount(4);
        second.Records.Should().HaveCount(4);
        second.Records.Should().BeEquivalentTo(first.Records);
        second.Diagnostics.FilesRead.Should().Be(0); // 未变更文件不重复解析
        second.Diagnostics.FilesDiscovered.Should().Be(1);
        second.Diagnostics.DuplicateRecords.Should().Be(1); // sidechain 回放折叠；另有 2 条同 message id 不同时刻的占位 id 不折叠
        second.Diagnostics.Status.Should().Be(ClaudeCodeScanStatus.Ok);
    }

    [Fact]
    public async Task Changed_file_is_reparsed_on_next_scan()
    {
        var root = CreateTempRoot();
        var fixture = await File.ReadAllTextAsync("fixtures/claude-code/sample-session.jsonl");
        var file = WriteSessionFile(root, "projects/project-a/session-a.jsonl", fixture);
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: root);

        var first = await adapter.ScanAsync();
        first.Records.Should().HaveCount(6);

        var appended =
            """{"sessionId":"session-a","timestamp":"2026-06-20T10:18:00.000Z","message":{"id":"msg-new","model":"claude-haiku","usage":{"input_tokens":5,"output_tokens":6}},"requestId":"req-new"}""" + "\n";
        File.AppendAllText(file, appended);

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesRead.Should().Be(1); // 只有变更文件被重新解析
        second.Records.Should().HaveCount(7);
        second.Records.Count(record => record.MessageId == "msg-new").Should().Be(1);
        second.Records.Count(record => record.MessageId == "msg-a").Should().Be(1); // 旧记录不重复
    }

    [Fact]
    public async Task Removed_file_drops_its_records()
    {
        var root = CreateTempRoot();
        var first = WriteSessionFile(root, "projects/project-a/one.jsonl",
            """{"sessionId":"session-a","timestamp":"2026-06-20T10:00:00.000Z","message":{"id":"msg-one","model":"claude","usage":{"input_tokens":10,"output_tokens":1}},"requestId":"req-one"}""" + "\n");
        var second = WriteSessionFile(root, "projects/project-a/two.jsonl",
            """{"sessionId":"session-a","timestamp":"2026-06-20T10:01:00.000Z","message":{"id":"msg-two","model":"claude","usage":{"input_tokens":20,"output_tokens":1}},"requestId":"req-two"}""" + "\n");
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: root);

        var initial = await adapter.ScanAsync();
        initial.Records.Should().HaveCount(2);

        File.Delete(second);
        var after = await adapter.ScanAsync();

        after.Records.Should().ContainSingle();
        after.Records.Single().MessageId.Should().Be("msg-one");
    }

    [Fact]
    public async Task Uses_claude_config_dir_from_injected_environment()
    {
        var root = CreateTempRoot();
        WriteSessionFile(root, "projects/project-a/session-a.jsonl",
            await File.ReadAllTextAsync("fixtures/claude-code/sample-session.jsonl"));
        var locator = NewLocator(("CLAUDE_CONFIG_DIR", root));
        var adapter = new ClaudeCodeJsonlAdapter(locator: locator);

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEquivalentTo(new[] { root });
        result.Records.Should().HaveCount(6);
    }

    [Fact]
    public async Task Explicit_root_overrides_injected_environment()
    {
        var envRoot = CreateTempRoot();
        WriteSessionFile(envRoot, "projects/project-env/session.jsonl",
            """{"sessionId":"s","timestamp":"2026-06-20T10:00:00.000Z","message":{"id":"msg-env","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"r"}""" + "\n");
        var explicitRoot = CreateTempRoot();
        WriteSessionFile(explicitRoot, "projects/project-explicit/session.jsonl",
            """{"sessionId":"s","timestamp":"2026-06-20T10:00:00.000Z","message":{"id":"msg-explicit","model":"claude","usage":{"input_tokens":2,"output_tokens":1}},"requestId":"r"}""" + "\n");
        var locator = NewLocator(("CLAUDE_CONFIG_DIR", envRoot));
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: explicitRoot, locator: locator);

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEquivalentTo(new[] { explicitRoot });
        result.Records.Single().MessageId.Should().Be("msg-explicit");
    }

    [Fact]
    public async Task Partial_status_persists_until_bad_file_is_fixed()
    {
        var root = CreateTempRoot();
        var file = WriteSessionFile(root, "projects/project-a/bad.jsonl",
            await File.ReadAllTextAsync("fixtures/claude-code/corrupted-lines.jsonl"));
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: root);

        var first = await adapter.ScanAsync();
        first.Diagnostics.Status.Should().Be(ClaudeCodeScanStatus.Partial);

        // 文件未变更、未重新解析，但历史坏文件的错误仍应反映在状态上。
        var second = await adapter.ScanAsync();
        second.Diagnostics.FilesRead.Should().Be(0);
        second.Diagnostics.Status.Should().Be(ClaudeCodeScanStatus.Partial);

        // 文件被修复后状态恢复 Ok。
        var goodLine =
            """{"sessionId":"session-b","timestamp":"2026-06-20T10:00:00.000Z","message":{"id":"msg-ok","model":"claude","usage":{"input_tokens":10,"output_tokens":1}},"requestId":"req-ok"}""" + "\n";
        File.WriteAllText(file, goodLine);

        var third = await adapter.ScanAsync();
        third.Diagnostics.Status.Should().Be(ClaudeCodeScanStatus.Ok);
    }

    [Fact]
    public async Task Mid_read_failure_discards_partial_records_and_retries_next_scan()
    {
        var root = CreateTempRoot();
        var firstLine =
            """{"sessionId":"session-a","timestamp":"2026-06-20T10:00:00.000Z","message":{"id":"msg-one","model":"claude","usage":{"input_tokens":10,"output_tokens":1}},"requestId":"req-one"}""" + "\n";
        var secondLine =
            """{"sessionId":"session-a","timestamp":"2026-06-20T10:01:00.000Z","message":{"id":"msg-two","model":"claude","usage":{"input_tokens":20,"output_tokens":1}},"requestId":"req-two"}""" + "\n";
        var file = WriteSessionFile(root, "projects/project-a/session-a.jsonl", firstLine);
        var failOnSecondLine = false;
        var adapter = new ClaudeCodeJsonlAdapter(
            configuredRoot: root,
            readerFactory: path => failOnSecondLine
                ? new FailAfterFirstLineReader(new StringReader(File.ReadAllText(path)))
                : new StreamReader(path));

        var initial = await adapter.ScanAsync();
        initial.Records.Should().ContainSingle();

        File.AppendAllText(file, secondLine);
        failOnSecondLine = true;
        var interrupted = await adapter.ScanAsync();
        interrupted.Diagnostics.FilesRead.Should().Be(0);
        interrupted.Records.Should().ContainSingle(); // 部分结果被丢弃，保留上次成功解析的记录

        // 未落账 → 文件在下次扫描被重新解析，补齐新记录。
        failOnSecondLine = false;
        var recovered = await adapter.ScanAsync();
        recovered.Diagnostics.FilesRead.Should().Be(1);
        recovered.Records.Should().HaveCount(2);
        recovered.Records.Select(record => record.MessageId).Should().BeEquivalentTo(["msg-one", "msg-two"]);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static ClaudeCodeLogLocator NewLocator(params (string Key, string? Value)[] variables)
        => new(key => variables.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.Ordinal)).Value);

    private string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccusage-adapter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static string WriteSessionFile(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>第一行正常返回，之后抛 IOException，模拟读取中途 IO 故障。</summary>
    private sealed class FailAfterFirstLineReader(TextReader inner) : TextReader
    {
        private bool _failed;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_failed)
            {
                throw new IOException("simulated mid-read failure");
            }

            _failed = true;
            return await inner.ReadLineAsync(cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
