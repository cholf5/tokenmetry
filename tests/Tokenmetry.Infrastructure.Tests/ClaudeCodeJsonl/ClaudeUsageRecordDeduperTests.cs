namespace Tokenmetry.Infrastructure.Tests.ClaudeCodeJsonl;

using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;

public class ClaudeUsageRecordDeduperTests
{
    private readonly ClaudeUsageRecordDeduper _deduper = new();

    [Fact]
    public void Deduplicates_exact_duplicates_and_sidechain_replays()
    {
        var parent = Record("msg-a", "req-parent", isSidechain: false, inputTokens: 100);
        var exactDuplicate = Record("msg-a", "req-parent", isSidechain: false, inputTokens: 80);
        var sidechainReplay = Record("msg-a", "req-side", isSidechain: true, inputTokens: 200);
        var distinctSidechain = Record("msg-b", "req-side", isSidechain: true, inputTokens: 50);

        var records = _deduper.Dedupe(new[]
        {
            sidechainReplay, parent, exactDuplicate, distinctSidechain,
        });

        records.Should().HaveCount(2);
        records.Should().BeEquivalentTo(new[] { parent, distinctSidechain });
    }

    [Fact]
    public void Prefers_higher_tokens_then_fast_model_suffix()
    {
        var lower = Record("msg-a", "req-a", isSidechain: false, inputTokens: 10);
        var higher = Record("msg-a", "req-a", isSidechain: false, inputTokens: 20);
        _deduper.Dedupe(new[] { lower, higher }).Should().BeEquivalentTo(new[] { higher });

        var standard = Record("msg-b", "req-b", isSidechain: false, inputTokens: 20, model: "claude");
        var fast = Record("msg-b", "req-b", isSidechain: false, inputTokens: 20, model: "claude-fast");
        _deduper.Dedupe(new[] { standard, fast }).Should().BeEquivalentTo(new[] { fast });
    }

    [Fact]
    public void Collapses_streaming_partials_sharing_message_id_without_request_id()
    {
        // auto 模式下一条 assistant 消息写出多行 usage（共享 message id、request id 为 null），
        // 必须折叠成一条并保留累计最大值。
        var partial = Record("msg-a", null, isSidechain: false, inputTokens: 100);
        var final = Record("msg-a", null, isSidechain: false, inputTokens: 250);
        var distinct = Record("msg-b", null, isSidechain: false, inputTokens: 70);

        var records = _deduper.Dedupe(new[] { partial, final, distinct });

        records.Should().HaveCount(2);
        records.Should().BeEquivalentTo(new[] { final, distinct });
    }

    [Fact]
    public void Records_without_message_id_are_never_deduplicated()
    {
        var first = Record(null, null, isSidechain: false, inputTokens: 10);
        var second = Record(null, null, isSidechain: false, inputTokens: 10);

        var records = _deduper.Dedupe(new[] { first, second });

        records.Should().HaveCount(2);
    }

    [Fact]
    public void Empty_input_yields_empty_output()
    {
        _deduper.Dedupe(Array.Empty<UsageRecord>()).Should().BeEmpty();
    }

    [Fact]
    public void Replacing_many_duplicates_finishes_in_linear_time()
    {
        // 每次替换若重建全表索引，历史量大时去重会把整轮采集卡住。
        // 2 万对「先低后高」同键记录：增量更新应很快完成并保留较高 token 那条。
        const int pairs = 20_000;
        var records = new List<UsageRecord>(pairs * 2);
        for (var i = 0; i < pairs; i++)
        {
            records.Add(Record($"msg-{i}", $"req-{i}", isSidechain: false, inputTokens: 1));
            records.Add(Record($"msg-{i}", $"req-{i}", isSidechain: false, inputTokens: 2));
        }

        var started = DateTime.UtcNow;
        var deduped = _deduper.Dedupe(records);
        var elapsed = DateTime.UtcNow - started;

        deduped.Should().HaveCount(pairs);
        deduped.Should().OnlyContain(record => record.InputTokens == 2);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Keeps_requestless_placeholder_ids_from_different_sessions_separate()
    {
        var deduped = new ClaudeUsageRecordDeduper().Dedupe([
            Row("gw-reused", requestId: null, session: "s-a", minute: 0),
            Row("gw-reused", requestId: null, session: "s-b", minute: 0),
        ]);

        deduped.Should().HaveCount(2, "网关占位 id 在不同会话里是两次真实请求");
    }

    [Fact]
    public void Keeps_requestless_ids_at_distinct_timestamps_separate_without_chain_proof()
    {
        var deduped = new ClaudeUsageRecordDeduper().Dedupe([
            Row("gw-reused", requestId: null, session: "s-a", minute: 0),
            Row("gw-reused", requestId: null, session: "s-a", minute: 1),
        ]);

        deduped.Should().HaveCount(2, "没有响应链证据时，同 message id 的不同时间戳无法证明是同一次响应");
    }

    [Fact]
    public void Collapses_requestless_records_sharing_a_response_chain_across_timestamps()
    {
        foreach (var messageId in new[] { "msg_01abc", "resp_01abc", "gw-uuid-01abc" })
        {
            var deduped = new ClaudeUsageRecordDeduper().Dedupe([
                Row(messageId, requestId: null, session: "s-a", minute: 0, chainIndex: 7),
                Row(messageId, requestId: null, session: "s-a", minute: 1, chainIndex: 7),
            ]);

            deduped.Should().ContainSingle($"{messageId} 的多个 content block 属于同一次响应，message id 拼写不参与判定");
        }
    }

    [Fact]
    public void Response_chain_key_is_scoped_by_session_and_source_file()
    {
        var otherSession = new ClaudeUsageRecordDeduper().Dedupe([
            Row("gw-id", requestId: null, session: "s-a", minute: 0, chainIndex: 7),
            Row("gw-id", requestId: null, session: "s-b", minute: 1, chainIndex: 7),
        ]);
        otherSession.Should().HaveCount(2, "不同会话是网关的两次真实请求");

        var otherFile = new ClaudeUsageRecordDeduper().Dedupe([
            Row("gw-id", requestId: null, session: "s-a", minute: 0, chainIndex: 7, sourceFile: "file-a"),
            Row("gw-id", requestId: null, session: "s-a", minute: 1, chainIndex: 7, sourceFile: "file-b"),
        ]);
        otherFile.Should().HaveCount(2, "链索引只在同一份转录内有效");
    }

    [Fact]
    public void Response_chain_does_not_overrule_request_ids()
    {
        var deduped = new ClaudeUsageRecordDeduper().Dedupe([
            Row("gw-id", requestId: "req-1", session: "s-a", minute: 0, chainIndex: 7),
            Row("gw-id", requestId: "req-2", session: "s-a", minute: 1, chainIndex: 7),
        ]);

        deduped.Should().HaveCount(2, "有 requestId 时它才是响应身份的唯一依据");
    }

    [Fact]
    public void Merges_slots_when_exact_and_response_keys_point_to_different_records()
    {
        var responseA = Row("gw-id", requestId: null, session: "s-a", minute: 0, chainIndex: 7, inputTokens: 100);
        var responseB = Row("gw-id", requestId: null, session: "s-a", minute: 1, chainIndex: 8, inputTokens: 50);
        // 时间戳与 responseB 相同（exact 键命中 B），链索引却指向 A 所属响应（response 键命中 A）：
        // 两槽位必须并成一个，否则同一次响应会被记两次。
        var bridge = Row("gw-id", requestId: null, session: "s-a", minute: 1, chainIndex: 7, inputTokens: 100);

        var deduped = new ClaudeUsageRecordDeduper().Dedupe([responseA, responseB, bridge]);

        deduped.Should().ContainSingle();
        deduped.Single().InputTokens.Should().Be(100);
    }

    [Fact]
    public void Collapses_matching_request_ids_across_sessions()
    {
        var deduped = new ClaudeUsageRecordDeduper().Dedupe([
            Row("msg-x", requestId: "req-1", session: "s-a", minute: 0),
            Row("msg-x", requestId: "req-1", session: "s-b", minute: 5),
        ]);

        deduped.Should().ContainSingle("有 requestId 时它足以证明是同一次响应");
    }

    private static UsageRecord Row(
        string messageId,
        string? requestId,
        string session,
        int minute,
        int? chainIndex = null,
        string sourceFile = "",
        long inputTokens = 100)
        => new()
        {
            MessageId = messageId,
            RequestId = requestId,
            SessionId = session,
            Timestamp = new DateTimeOffset(2026, 9, 14, 8, minute, 0, TimeSpan.Zero),
            InputTokens = inputTokens,
            OutputTokens = 10,
            IsSidechain = false,
            Model = "claude-sonnet-4-5-20250929",
            ResponseChainIndex = chainIndex,
            SourceFile = sourceFile,
        };

    private static UsageRecord Record(string? messageId, string? requestId, bool isSidechain, long inputTokens, string? model = null)
        => new()
        {
            Timestamp = new DateTimeOffset(2026, 6, 20, 10, 12, 30, TimeSpan.Zero),
            Agent = "Claude Code",
            ProjectPath = "project-a",
            SessionId = "session-a",
            Model = model,
            CostUsd = 0m,
            InputTokens = inputTokens,
            IsSidechain = isSidechain,
            MessageId = messageId,
            RequestId = requestId,
        };
}
