namespace CCUsage.Infrastructure.Tests.ClaudeCodeJsonl;

using System.Text.Json;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.ClaudeCodeJsonl;

public class ClaudeCodeJsonlLineParserTests
{
    private readonly ClaudeCodeJsonlLineParser _parser = new();

    [Fact]
    public void Parses_valid_direct_usage_entry()
    {
        var record = ParseLine(
            """{"sessionId":"session-a","timestamp":"2026-06-20T10:12:30.000Z","version":"1.2.3","message":{"id":"msg-a","model":"claude-sonnet-4-20250514","usage":{"input_tokens":123,"output_tokens":45,"cache_creation_input_tokens":10,"cache_read_input_tokens":1000}},"costUSD":0.0123,"requestId":"req-a","isApiErrorMessage":false,"isSidechain":false}""");

        record.Should().NotBeNull();
        record!.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 20, 10, 12, 30, TimeSpan.Zero));
        record.Agent.Should().Be("Claude Code");
        record.SessionId.Should().Be("session-a");
        record.ProjectPath.Should().Be("project-a");
        record.Model.Should().Be("claude-sonnet-4-20250514");
        record.CostUsd.Should().Be(0.0123m);
        record.InputTokens.Should().Be(123);
        record.OutputTokens.Should().Be(45);
        record.CacheCreationTokens.Should().Be(10);
        record.CacheCreationEphemeral5mTokens.Should().Be(0);
        record.CacheCreationEphemeral1hTokens.Should().Be(0);
        record.CacheReadTokens.Should().Be(1000);
        record.TotalTokens.Should().Be(1178);
        record.MessageId.Should().Be("msg-a");
        record.RequestId.Should().Be("req-a");
        record.IsSidechain.Should().BeFalse();
        record.SourceFile.Should().EndWith("session-a.jsonl");
    }

    [Fact]
    public void Parses_agent_progress_wrapper_entry()
    {
        var record = ParseLine(
            """{"data":{"message":{"timestamp":"2026-06-20T10:12:30.000Z","requestId":"req-wrapper","isSidechain":true,"costUSD":0.02,"message":{"id":"msg-wrapper","model":"claude-sonnet-4-20250514","usage":{"input_tokens":0,"output_tokens":10,"cache_creation_input_tokens":0,"cache_read_input_tokens":20}}}}}""");

        record.Should().NotBeNull();
        record!.SessionId.Should().Be("file-session"); // 包装行没有 sessionId，回退到文件推导值
        record.MessageId.Should().Be("msg-wrapper");
        record.RequestId.Should().Be("req-wrapper");
        record.IsSidechain.Should().BeTrue();
        record.CostUsd.Should().Be(0.02m);
        record.TotalTokens.Should().Be(30);
    }

    [Fact]
    public void Calculates_nested_cache_creation_tokens()
    {
        var record = ParseLine(
            """{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-cache","model":"claude","usage":{"input_tokens":10,"output_tokens":20,"cache_creation_input_tokens":999,"cache_read_input_tokens":30,"cache_creation":{"ephemeral_5m_input_tokens":40,"ephemeral_1h_input_tokens":50}}},"requestId":"req-cache"}""");

        record.Should().NotBeNull();
        record!.CacheCreationTokens.Should().Be(90); // 有细分时取 5m + 1h 之和，忽略平铺的 999
        record.CacheCreationEphemeral5mTokens.Should().Be(40);
        record.CacheCreationEphemeral1hTokens.Should().Be(50);
        record.CacheReadTokens.Should().Be(30);
        record.TotalTokens.Should().Be(150);
    }

    [Fact]
    public void Normalizes_synthetic_and_fast_models()
    {
        var synthetic = ParseLine(
            """{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-synthetic","model":"<synthetic>","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-synthetic"}""");
        var fast = ParseLine(
            """{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-fast","model":"claude-sonnet","usage":{"input_tokens":1,"output_tokens":1,"speed":"fast"}},"requestId":"req-fast"}""");
        var standard = ParseLine(
            """{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-standard","model":"claude-sonnet","usage":{"input_tokens":1,"output_tokens":1,"speed":"standard"}},"requestId":"req-standard"}""");

        synthetic!.Model.Should().BeNull();
        fast!.Model.Should().Be("claude-sonnet-fast");
        standard!.Model.Should().Be("claude-sonnet");
    }

    [Fact]
    public void Parses_nullable_metadata_from_real_claude_logs()
    {
        var record = ParseLine(
            """{"sessionId":null,"timestamp":"2026-06-20T10:12:30.000Z","version":null,"message":{"id":null,"model":"auto","usage":{"input_tokens":1064197,"output_tokens":34903,"cache_creation_input_tokens":0,"cache_read_input_tokens":2951936,"speed":null}},"costUSD":null,"requestId":null,"isApiErrorMessage":null,"isSidechain":null}""");

        record.Should().NotBeNull(); // null 元数据不应丢弃 usage
        record!.SessionId.Should().Be("file-session");
        record.Model.Should().Be("auto");
        record.CostUsd.Should().BeNull();
        record.InputTokens.Should().Be(1_064_197);
        record.OutputTokens.Should().Be(34_903);
        record.CacheReadTokens.Should().Be(2_951_936);
        record.TotalTokens.Should().Be(4_051_036);
        record.MessageId.Should().BeNull();
        record.RequestId.Should().BeNull();
        record.IsSidechain.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"timestamp":"   ","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}""")]
    [InlineData("""{"sessionId":"   ","timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}""")]
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"   "}""")]
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"   ","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}""")]
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-x","model":"   ","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}""")]
    [InlineData("""{"message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}""")] // 缺 timestamp
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","requestId":"req-x"}""")] // 缺 message
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-x","model":"claude"},"requestId":"req-x"}""")] // 缺 usage
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-x","model":"claude","usage":{"output_tokens":1}},"requestId":"req-x"}""")] // 缺 input_tokens
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":null}},"requestId":"req-x"}""")] // cache_read 显式 null
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":-1,"output_tokens":1}},"requestId":"req-x"}""")] // u64 不接受负数
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1,"speed":"turbo"}},"requestId":"req-x"}""")] // 未知 speed
    [InlineData("""{"timestamp":"not-a-timestamp","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}""")]
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","version":"not-a-version","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}""")]
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","version":"1.2","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}""")]
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","version":"1.2.x","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}""")]
    [InlineData("""{"timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x","isApiErrorMessage":"false"}""")] // isApiErrorMessage 非 bool（serde Option<bool> 拒绝）
    public void Rejects_invalid_records(string json)
    {
        ParseLine(json).Should().BeNull();
    }

    [Fact]
    public void Case_variant_property_names_are_not_bound()
    {
        // serde 按精确 JSON 名绑定：大小写变体视为未知字段忽略，而不是被误绑定。
        var record = ParseLine(
            """{"SessionID":"evil-session","timestamp":"2026-06-20T10:12:30.000Z","message":{"id":"msg-x","MODEL":"claude","usage":{"input_tokens":1,"output_tokens":1}},"REQUESTID":"evil-req","isSideChain":true}""");

        record.Should().NotBeNull();
        record!.SessionId.Should().Be("file-session"); // sessionId 未绑定，回退文件推导值
        record.Model.Should().BeNull();
        record.RequestId.Should().BeNull();
        record.IsSidechain.Should().BeFalse();
    }

    [Fact]
    public void Accepts_semver_version_prefixes()
    {
        ParseLine(
            """{"timestamp":"2026-06-20T10:12:30.000Z","version":"1.2.3-beta","message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}"""
        ).Should().NotBeNull();
        ParseLine(
            """{"timestamp":"2026-06-20T10:12:30.000Z","version":null,"message":{"id":"msg-x","model":"claude","usage":{"input_tokens":1,"output_tokens":1}},"requestId":"req-x"}"""
        ).Should().NotBeNull();
    }

    [Fact]
    public void Usage_marker_matches_claude_rs()
    {
        ClaudeCodeJsonlLineParser.UsageMarker.Should().Be("\"usage\":{");
        ClaudeCodeJsonlLineParser.AgentName.Should().Be("Claude Code");
    }

    [Fact]
    public void Extracts_flat_nested_and_subagent_session_parts()
    {
        var flat = "/root/projects/project-a/session-a.jsonl";
        ClaudeCodeJsonlLineParser.ExtractSessionParts(flat)
            .Should().Be(("session-a", "project-a"));

        var nested = "/root/projects/project-a/nested/session-b/file.jsonl";
        var (nestedSession, nestedProject) = ClaudeCodeJsonlLineParser.ExtractSessionParts(nested);
        nestedSession.Should().Be("session-b");
        nestedProject.Should().Be("project-a" + Path.DirectorySeparatorChar + "nested");

        var subagent = "/root/projects/project-a/session-c/subagents/worker.jsonl";
        ClaudeCodeJsonlLineParser.ExtractSessionParts(subagent)
            .Should().Be(("session-c", "project-a"));
    }

    [Fact]
    public void Falls_back_to_project_directory_when_file_name_is_bare_jsonl_suffix()
    {
        // 文件名为 ".jsonl" 时推导不出会话 id：会话 id 回退为项目目录名，项目路径为 Unknown Project。
        ClaudeCodeJsonlLineParser.ExtractSessionParts("/root/projects/project-a/.jsonl")
            .Should().Be(("project-a", "Unknown Project"));
    }

    [Fact]
    public void Extracts_session_parts_from_windows_backslash_paths()
    {
        // I-005 回归：Split('/', '\\', options) 曾重载解析为 Split(char, int, options)，
        // 反斜杠被隐式转成 int 当作最大段数，Windows 路径从未被拆分。
        ClaudeCodeJsonlLineParser.ExtractSessionParts(@"C:\root\projects\project-a\session-a.jsonl")
            .Should().Be(("session-a", "project-a"));
    }

    private UsageRecord? ParseLine(string json)
    {
        using var document = JsonDocument.Parse(json);
        return _parser.Parse(
            document.RootElement,
            "file-session",
            "project-a",
            "/logs/projects/project-a/session-a.jsonl");
    }
}
