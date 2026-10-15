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
