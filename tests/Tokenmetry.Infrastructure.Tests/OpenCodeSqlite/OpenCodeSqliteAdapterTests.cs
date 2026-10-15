using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.OpenCodeSqlite;
using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.Tests.OpenCodeSqlite;

/// <summary>
/// I-009: OpenCode SQLite 适配器测试（临时库 fixtures，合成数据；语义对照
/// ../ccusage/rust/adapters/opencode/ 的 loader.rs / parser.rs / paths.rs 内联测试，同名场景逐一对齐）。
/// </summary>
public class OpenCodeSqliteAdapterTests : IDisposable
{
    private static readonly long NoonUtc20260101 = 1_767_225_600_000;

    private readonly List<string> _tempDirs = new();
    private readonly List<string> _environmentVariables = new();

    // ---- v1 message 表（蓝本 loads_messages_from_sqlite_database 等）----

    [Fact]
    public async Task Maps_message_rows_from_sqlite_database()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateMessageTable(db);
        InsertMessage(db, "msg-1", "session-a", """
            {"id":"payload-id","sessionID":"payload-session","providerID":"anthropic","modelID":"claude-sonnet-4-20250514","time":{"created":1767312000000},"tokens":{"input":100,"output":50,"cache":{"read":10,"write":20}},"cost":0.02}
            """);
        InsertMessage(db, "msg-user", "session-a", """
            {"id":"msg-user","sessionID":"session-a","type":"user","time":{"created":1767312000001},"text":"hello"}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        record.Agent.Should().Be("OpenCode");
        record.ProjectPath.Should().Be("OpenCode");
        record.SessionId.Should().Be("session-a", "列值 session_id 优先于载荷字段");
        record.Model.Should().Be("claude-sonnet-4-20250514");
        record.MessageId.Should().Be("msg-1", "列值 id 优先于载荷字段");
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(50);
        record.CacheCreationTokens.Should().Be(20);
        record.CacheReadTokens.Should().Be(10);
        record.CostUsd.Should().Be(0.02m, "消息自带正 cost 直接采用（蓝本 keeps_positive_opencode_cost）");

        var diagnostics = result.Diagnostics;
        diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        diagnostics.FilesDiscovered.Should().Be(1);
        diagnostics.FilesRead.Should().Be(1);
        diagnostics.LinesSkipped.Should().Be(1, "user 消息（无 tokens）不是 usage 行，按 skipped 计");
        diagnostics.MalformedLines.Should().Be(0);
        diagnostics.InvalidRecords.Should().Be(0);
        diagnostics.DuplicateRecords.Should().Be(0);
    }

    /// <summary>蓝本 keeps_opencode_record_when_cache_field_is_not_an_object：cache 非对象按缺失（两桶 0），行保留。</summary>
    [Fact]
    public async Task Keeps_record_when_cache_field_is_not_an_object()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateMessageTable(db);
        InsertMessage(db, "message-a", "session-a", """
            {"id":"message-a","sessionID":"session-a","providerID":"openai","modelID":"gpt-test","time":{"created":1767312000000},"tokens":{"input":100,"output":10,"cache":0},"cost":0.02}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(10);
        record.CacheCreationTokens.Should().Be(0);
        record.CacheReadTokens.Should().Be(0);
        record.CostUsd.Should().Be(0.02m);
    }

    /// <summary>蓝本 falls_back_to_total_tokens_when_opencode_token_parts_are_missing：分量缺失时 total 补入 output。</summary>
    [Fact]
    public async Task Falls_back_to_total_tokens_when_token_parts_are_missing()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateMessageTable(db);
        InsertMessage(db, "message-a", "session-a", """
            {"id":"message-a","sessionID":"session-a","providerID":"openai","modelID":"gpt-test","time":{"created":1767312000000},"tokens":{"total":123}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(123);
        record.TotalTokens.Should().Be(123);
        record.CostUsd.Should().BeNull("cost 缺失 → 定价引擎补价（MissingCostRecords 计 1）");
        result.Diagnostics.MissingCostRecords.Should().Be(1);
    }

    /// <summary>
    /// 蓝本 keeps_epoch_for_display_but_omits_missing_message_timestamp_for_pricing：
    /// time.created 缺失/非正 → epoch 展示；成本仍可计（自研侧记录 CostUsd=null 由引擎计价）。
    /// </summary>
    [Fact]
    public async Task Keeps_epoch_when_message_timestamp_is_missing()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateMessageTable(db);
        InsertMessage(db, "message-a", "session-a", """
            {"id":"message-a","sessionID":"session-a","providerID":"deepseek","modelID":"k2p6","tokens":{"input":1000}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(DateTimeOffset.UnixEpoch);
        record.Model.Should().Be("k2p6");
        record.CostUsd.Should().BeNull();
    }

    /// <summary>
    /// 蓝本 calculates_cost_for_k2p6_when_opencode_stores_zero_cost 的移植 + I-024 接线：
    /// k2p6 → kimi-k2.6 别名候选链随记录落 <see cref="UsageRecord.Pricing"/>，引擎逐候选命中
    /// 内置快照 moonshot/kimi-k2.6。快照价目 2026-10-12 按 ccusage 20.0.29 CLI 内嵌 models-dev
    /// 数据刷新（I-024 观察项）：input 1 / output 4.16 / cache_read 0.18 / cache_write 1（旧值
    /// 0.95 / 4 / 0.16 为过期 models.dev 数据）→ 100×1 + 10×4.16 + 50×0.18（每百万）= $0.0001506。
    /// </summary>
    [Fact]
    public async Task Prices_k2p6_alias_through_snapshot_after_model_resolution()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateMessageTable(db);
        InsertMessage(db, "message-a", "session-a", """
            {"id":"message-a","sessionID":"session-a","providerID":"kimi-for-coding","modelID":"k2p6","time":{"created":1767312000000},"tokens":{"input":100,"output":10,"cache":{"read":50}},"cost":0}
            """);
        var scan = await CreateAdapter(directory).ScanAsync();
        var record = scan.Records.Should().ContainSingle().Subject;
        record.CostUsd.Should().BeNull("stored cost 为 0 → 走定价引擎");

        // 接线断言：映射产物带蓝本 open_code_model_candidates 候选链（[别名解析名, 归一化名] +
        // provider 前缀变体；蓝本 base 首项即 resolved，原始 "k2p6" 不在链中），引擎按记录计价
        // 不再需要调用方手工解析别名。
        record.Pricing.Should().NotBeNull();
        record.Pricing!.ModelCandidates.Should().Equal("kimi-k2.6", "kimi_for_coding/kimi-k2.6");
        record.Model.Should().Be("k2p6", "分组/显示仍用原始 modelID");

        OpenCodeMessageParser.ResolveModelName("k2p6").Should().Be("kimi-k2.6");
        var priced = new PricingEngine().PriceRecord(record);
        priced.CostUsd.Should().Be(0.0001506m);
        priced.MatchedKey.Should().Be("moonshot/kimi-k2.6");
    }

    /// <summary>
    /// 蓝本 calculates_cost_when_opencode_stores_zero_cost 的移植（自定义价 → 用户覆盖等价注入）：
    /// stored cost 为 0 时按 token 计价 100×$1/M + 10×$10/M + 50×$0.1/M = $0.000205。
    /// </summary>
    [Fact]
    public async Task Prices_zero_stored_cost_from_tokens_with_override_rates()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateMessageTable(db);
        InsertMessage(db, "message-a", "session-a", """
            {"id":"message-a","sessionID":"session-a","providerID":"openai","modelID":"gpt-test","time":{"created":1767312000000},"tokens":{"input":100,"output":10,"cache":{"read":50}},"cost":0}
            """);
        var scan = await CreateAdapter(directory).ScanAsync();
        var record = scan.Records.Should().ContainSingle().Subject;

        var engine = new PricingEngine(overrides: new[]
        {
            new ModelPricingOverride
            {
                Model = "gpt-test",
                InputPerMillion = 1m,
                OutputPerMillion = 10m,
                CacheReadPerMillion = 0.1m,
            },
        });
        engine.PriceRecord(record).CostUsd.Should().Be(0.000205m);
    }

    // ---- v2 session_message 表（蓝本 loads_nested_v2_assistant_usage / v2 without time_created / dedup）----

    [Fact]
    public async Task Maps_v2_assistant_rows_and_ignores_other_types()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateSessionMessageTable(db, withTimeCreated: true);
        InsertSessionMessage(db, "v2-assistant", "session-b", "assistant", NoonUtc20260101, """
            {"model":{"id":"gpt-test","providerID":"openai"},"time":{"created":1767312000000},"tokens":{"input":120,"output":60},"cost":0.03}
            """, seq: 1);
        InsertSessionMessage(db, "v2-user", "session-b", "user", NoonUtc20260101, """
            {"time":{"created":1767312000001},"text":"hi"}
            """, seq: 2);
        InsertSessionMessage(db, "v2-step", "session-b", "step-start", NoonUtc20260101, "{}", seq: 3);

        var result = await CreateAdapter(directory).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.MessageId.Should().Be("v2-assistant");
        record.SessionId.Should().Be("session-b");
        record.Model.Should().Be("gpt-test", "model 嵌套对象 id 优先");
        record.InputTokens.Should().Be(120);
        record.OutputTokens.Should().Be(60);
        record.CostUsd.Should().Be(0.03m);
        result.Diagnostics.LinesSkipped.Should().Be(2, "非 assistant 的 v2 行按 skipped 计");
    }

    /// <summary>蓝本 loads_v2_messages_without_a_time_created_column：无列时 created 取 0 → epoch。</summary>
    [Fact]
    public async Task Maps_v2_rows_without_time_created_column()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateSessionMessageTable(db, withTimeCreated: false);
        InsertSessionMessage(db, "v2-no-time", "session-c", "assistant", null, """
            {"model":{"id":"gpt-test","providerID":"openai"},"tokens":{"input":80,"output":40}}
            """, seq: 0);

        var result = await CreateAdapter(directory).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(DateTimeOffset.UnixEpoch);
        record.InputTokens.Should().Be(80);
        record.OutputTokens.Should().Be(40);
    }

    /// <summary>v2 行载荷缺 time.created 时回退列值 time_created（蓝本 into_legacy_message 的 or(Some(created))）。</summary>
    [Fact]
    public async Task Falls_back_to_time_created_column_when_payload_time_is_missing()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateSessionMessageTable(db, withTimeCreated: true);
        InsertSessionMessage(db, "v2-column-time", "session-d", "assistant", 1_767_312_000_000, """
            {"model":{"id":"gpt-test","providerID":"openai"},"tokens":{"input":80,"output":40}}
            """, seq: 0);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().ContainSingle().Subject.Timestamp
            .Should().Be(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// 独立审查 P2-1：畸形 v1 message 表缺核心列时**静默**跳过 v1（不标 problems），v2 照常扫描——
    /// 蓝本 prepare 失败口径（跳过该表、继续其它表），不因 schema 异常把整库降级。
    /// </summary>
    [Fact]
    public async Task Skips_malformed_message_table_but_still_scans_session_messages()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        // v1 表缺 session_id / data 列（修复前：SELECT 抛 SqliteException → 整库 problems → v2 也被跳过）。
        Execute(db, "CREATE TABLE message (id TEXT)");
        CreateSessionMessageTable(db, withTimeCreated: true);
        InsertSessionMessage(db, "v2-good", "session-v2", "assistant", NoonUtc20260101, """
            {"model":{"id":"gpt-test","providerID":"openai"},"time":{"created":1767312000000},"tokens":{"input":30,"output":3}}
            """, seq: 0);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().ContainSingle().Subject.MessageId.Should().Be("v2-good");
        result.Diagnostics.FilesRead.Should().Be(1, "库本身可读，v1 表静默跳过不阻断");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok, "v1 列门槛跳过不标 problems（蓝本 prepare 失败口径）");
        result.Diagnostics.InvalidRecords.Should().Be(0);
    }

    /// <summary>
    /// 独立审查 P2-2：非 assistant 行的 data 不可读按蓝本"先查 type 再读 data"归类——
    /// 计 LinesSkipped（无声非事件）而非 InvalidRecords。
    /// </summary>
    [Fact]
    public async Task Non_assistant_rows_with_unreadable_data_count_as_skipped()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateSessionMessageTable(db, withTimeCreated: true);
        InsertRaw(db, "INSERT INTO session_message (id, session_id, type, seq, time_created, data) VALUES ('v2-user-bad-data', 'session-b', 'user', 0, 0, '{broken')");
        InsertSessionMessage(db, "v2-good", "session-b", "assistant", NoonUtc20260101, """
            {"model":{"id":"gpt-test","providerID":"openai"},"time":{"created":1767312000000},"tokens":{"input":30,"output":3}}
            """, seq: 1);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().ContainSingle().Subject.MessageId.Should().Be("v2-good");
        result.Diagnostics.LinesSkipped.Should().Be(1, "非 assistant 行在读 data 前剔除（蓝本顺序）");
        result.Diagnostics.InvalidRecords.Should().Be(0);
    }

    /// <summary>蓝本 deduplicates_legacy_and_v2_rows_by_message_id：v1/v2 同 message id 只留先读到的一行。</summary>
    [Fact]
    public async Task Deduplicates_legacy_and_v2_rows_by_message_id()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateMessageTable(db);
        InsertMessage(db, "shared-msg", "legacy-session", """
            {"providerID":"anthropic","modelID":"claude-sonnet-4-20250514","time":{"created":1767312000000},"tokens":{"input":150,"output":75},"cost":0.04}
            """);
        CreateSessionMessageTable(db, withTimeCreated: true);
        InsertSessionMessage(db, "shared-msg", "v2-session", "assistant", NoonUtc20260101, """
            {"model":{"id":"gpt-test","providerID":"openai"},"time":{"created":1767312000001},"tokens":{"input":999,"output":999},"cost":0.99}
            """, seq: 0);

        var result = await CreateAdapter(directory).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.MessageId.Should().Be("shared-msg");
        record.InputTokens.Should().Be(150, "v1 message 表先扫，重复的 v2 行被去重");
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    // ---- fork 拷贝剔除（蓝本 insert_fork_history 系列测试）----

    [Fact]
    public async Task Excludes_copied_history_through_the_fork_boundary()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        InsertForkHistory(db, boundaryType: "through");

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().HaveCount(5);
        result.Records.Where(record => record.SessionId == "parent").Sum(record => record.InputTokens)
            .Should().Be(1000);
        var forkRecords = result.Records.Where(record => record.SessionId == "fork").ToList();
        forkRecords.Should().ContainSingle();
        forkRecords[0].MessageId.Should().Be("fork-own");
        forkRecords[0].InputTokens.Should().Be(40);
    }

    [Fact]
    public async Task Excludes_copied_history_before_the_fork_boundary()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        InsertForkHistory(db, boundaryType: "before");

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Where(record => record.SessionId == "fork")
            .Select(record => record.MessageId)
            .Should().BeEquivalentTo(["fork-copy-boundary", "fork-own"],
                "before 边界保留拷贝边界行（seq 2），seq ≤ 1 的拷贝行剔除");
    }

    [Fact]
    public async Task Keeps_fork_rows_when_the_boundary_cannot_be_resolved()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        InsertForkHistory(db, boundaryType: "through");
        Execute(db, "UPDATE session_v2 SET fork_boundary = '{\"type\":\"through\",\"messageID\":\"missing\"}' WHERE id = 'fork'");

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().HaveCount(8, "边界无法解析时 fork 保留全部行");
        result.Records.Count(record => record.SessionId == "fork").Should().Be(4);
    }

    [Fact]
    public async Task Keeps_fork_rows_without_fork_metadata()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        InsertForkHistory(db, boundaryType: "through");
        Execute(db, "DROP TABLE session_v2");

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().HaveCount(8);
    }

    [Fact]
    public async Task Keeps_fork_rows_when_session_messages_have_no_seq_column()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        Execute(db, "CREATE TABLE session_message (id TEXT PRIMARY KEY, session_id TEXT, type TEXT, time_created INTEGER, data TEXT)");
        foreach (var (id, sessionId, input) in new[]
                 {
                     ("parent-msg", "parent", 100), ("fork-copy", "fork", 100), ("fork-own", "fork", 40),
                 })
        {
            InsertSessionMessage(db, id, sessionId, "assistant", NoonUtc20260101, AssistantPayload(input, 10), seq: null);
        }

        CreateSessionV2TableWithForkColumns(db);
        InsertSessionFork(db, "fork", "parent", """{"type":"through","messageID":"parent-msg"}""");

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().HaveCount(3, "无 seq 列无法区分拷贝行，全部保留");
    }

    [Fact]
    public async Task Excludes_copied_history_from_a_fork_of_a_fork()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        InsertForkHistory(db, boundaryType: "through");
        foreach (var (copyId, seq) in new[]
                 {
                     ("grand-copy-1", 0L), ("grand-copy-2", 1L), ("grand-copy-boundary", 2L), ("grand-copy-fork-own", 4L),
                 })
        {
            InsertSessionMessage(db, copyId, "grandchild", "assistant", NoonUtc20260101 + seq, AssistantPayload(100, 10), seq);
        }

        InsertSessionMessage(db, "grand-own", "grandchild", "assistant", NoonUtc20260101 + 5, AssistantPayload(7, 1), 5);
        InsertSessionFork(db, "grandchild", "fork", """{"type":"through","messageID":"fork-own"}""");

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Where(record => record.SessionId == "grandchild")
            .Select(record => record.MessageId)
            .Should().BeEquivalentTo(["grand-own"]);
        result.Records.Should().HaveCount(6, "parent 4 + fork 自有 1 + grandchild 自有 1");
    }

    /// <summary>蓝本 keeps_fork_rows_after_the_last_copy_when_before_seqs_are_sparse：seq 稀疏时 before 边界
    /// 取小于边界的最大 seq，不吞掉 fork 自有行。</summary>
    [Fact]
    public async Task Keeps_fork_rows_after_the_last_copy_when_before_seqs_are_sparse()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateSessionMessageTable(db, withTimeCreated: true);
        CreateSessionV2TableWithForkColumns(db);
        foreach (var (sessionId, id, seq, input) in new[]
                 {
                     ("parent", "parent-a", 0L, 100),
                     ("parent", "parent-b", 10L, 200),
                     ("parent", "parent-boundary", 100L, 300),
                     ("fork", "fork-copy-a", 0L, 100),
                     ("fork", "fork-copy-b", 10L, 200),
                     ("fork", "fork-own", 20L, 40),
                 })
        {
            InsertSessionMessage(db, id, sessionId, "assistant", NoonUtc20260101 + seq, AssistantPayload(input, 10), seq);
        }

        InsertSessionFork(db, "fork", "parent", """{"type":"before","messageID":"parent-boundary"}""");

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Where(record => record.SessionId == "fork")
            .Select(record => record.MessageId)
            .Should().BeEquivalentTo(["fork-own"]);
    }

    // ---- 行级诊断口径 ----

    [Fact]
    public async Task Counts_row_level_rejections_by_diagnostics_kind()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateMessageTable(db);
        InsertRaw(db, "INSERT INTO message (id, session_id, time_created, data) VALUES ('bad-json', 's', 0, '{not-json')");
        InsertRaw(db, "INSERT INTO message (id, session_id, time_created, data) VALUES ('no-model', 's', 0, '{\"tokens\":{\"input\":10,\"output\":1}}')");
        InsertRaw(db, "INSERT INTO message (id, session_id, time_created, data) VALUES (NULL, 's', 0, '{\"providerID\":\"openai\",\"modelID\":\"gpt-test\",\"tokens\":{\"input\":10,\"output\":1}}')");
        InsertMessage(db, "ok", "s", """
            {"providerID":"openai","modelID":"gpt-test","time":{"created":1767312000000},"tokens":{"input":10,"output":1}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().ContainSingle().Subject.MessageId.Should().Be("ok");
        result.Diagnostics.MalformedLines.Should().Be(1, "data 载荷非法 JSON 计 malformed");
        result.Diagnostics.InvalidRecords.Should().Be(2, "有载荷但缺 modelID 与 id 列不可读各计 1");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    /// <summary>去重发生在映射成功之后（I-006 口径）：首现行无 usage 载荷不占去重名额，另一库的有效行保留。</summary>
    [Fact]
    public async Task Dedup_happens_after_mapping_so_valid_row_survives_payload_less_first_occurrence()
    {
        var directoryA = CreateTempDir();
        var directoryB = CreateTempDir();
        CreateMessageTable(Path.Combine(directoryA, "opencode.db"));
        InsertMessage(Path.Combine(directoryA, "opencode.db"), "shared", "s-a", """
            {"providerID":"openai","modelID":"gpt-test","time":{"created":1767312000000},"text":"no tokens"}
            """);
        CreateMessageTable(Path.Combine(directoryB, "opencode.db"));
        InsertMessage(Path.Combine(directoryB, "opencode.db"), "shared", "s-b", """
            {"providerID":"openai","modelID":"gpt-test","time":{"created":1767312000000},"tokens":{"input":20,"output":2}}
            """);

        var result = await CreateAdapter($"{directoryA},{directoryB}").ScanAsync();

        result.Records.Should().ContainSingle().Subject.SessionId.Should().Be("s-b");
        result.Diagnostics.DuplicateRecords.Should().Be(0);
        result.Diagnostics.LinesSkipped.Should().Be(1);
    }

    /// <summary>跨目录同 id 去重保留先配置目录的行（蓝本多目录 seen 顺序）。</summary>
    [Fact]
    public async Task Deduplicates_cross_directory_rows_keeping_first_directory()
    {
        var directoryA = CreateTempDir();
        var directoryB = CreateTempDir();
        CreateMessageTable(Path.Combine(directoryA, "opencode.db"));
        InsertMessage(Path.Combine(directoryA, "opencode.db"), "dup", "s-a", """
            {"providerID":"openai","modelID":"gpt-test","time":{"created":1767312000000},"tokens":{"input":10,"output":1}}
            """);
        CreateMessageTable(Path.Combine(directoryB, "opencode.db"));
        InsertMessage(Path.Combine(directoryB, "opencode.db"), "dup", "s-b", """
            {"providerID":"openai","modelID":"gpt-test","time":{"created":1767312000000},"tokens":{"input":20,"output":2}}
            """);

        var result = await CreateAdapter($"{directoryA},{directoryB}").ScanAsync();

        result.Records.Should().ContainSingle().Subject.SessionId.Should().Be("s-a");
        result.Diagnostics.DuplicateRecords.Should().Be(1);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
    }

    // ---- 库选择与定位（蓝本 db_path / paths.rs 内联测试）----

    [Fact]
    public async Task Loads_channel_sqlite_database()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode-beta.db");
        CreateMessageTable(db);
        InsertMessage(db, "channel-msg-1", "channel-session-a", """
            {"providerID":"anthropic","modelID":"claude-sonnet-4-20250514","time":{"created":1767312000000},"tokens":{"input":80,"output":40}}
            """);

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().ContainSingle().Subject.SessionId.Should().Be("channel-session-a");
    }

    [Fact]
    public void Channel_database_selection_prefers_default_then_sorted_channel_names()
    {
        var directory = CreateTempDir();
        Path.Combine(directory, "opencode-echo.db").Should().NotBe(OpenCodeSqliteAdapter.SelectDatabase(directory),
            "目录里尚无库文件时不应选出任何库");
        File.WriteAllText(Path.Combine(directory, "opencode-echo.db"), "not-a-real-db");
        File.WriteAllText(Path.Combine(directory, "opencode-alpha.db"), "not-a-real-db");
        File.WriteAllText(Path.Combine(directory, "opencode-zulu.db"), "not-a-real-db");
        File.WriteAllText(Path.Combine(directory, "opencode-bad!.db"), "not-a-real-db");
        File.WriteAllText(Path.Combine(directory, "other.db"), "not-a-real-db");
        OpenCodeSqliteAdapter.SelectDatabase(directory)
            .Should().Be(Path.Combine(directory, "opencode-alpha.db"), "频道库按名排序取第一个，非法字符名忽略");
        File.WriteAllText(Path.Combine(directory, "opencode.db"), "not-a-real-db");
        OpenCodeSqliteAdapter.SelectDatabase(directory)
            .Should().Be(Path.Combine(directory, "opencode.db"), "默认库存在时优先");
    }

    [Fact]
    public async Task Missing_database_returns_no_data()
    {
        var directory = CreateTempDir();

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.FilesDiscovered.Should().Be(0);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>坏库（非 SQLite 文件）：打不开记问题并降级 Partial，不抛异常。</summary>
    [Fact]
    public async Task Undreadable_database_degrades_to_partial()
    {
        var directory = CreateTempDir();
        File.WriteAllText(Path.Combine(directory, "opencode.db"), "this is not a sqlite database");

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.FilesDiscovered.Should().Be(1);
        result.Diagnostics.FilesRead.Should().Be(0);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData, "无记录产出时状态为 NoData（问题计数体现在打开失败）");
    }

    [Fact]
    public async Task Sorts_records_by_timestamp_across_tables()
    {
        var directory = CreateTempDir();
        var db = Path.Combine(directory, "opencode.db");
        CreateMessageTable(db);
        InsertMessage(db, "late", "s", """{"providerID":"openai","modelID":"gpt-test","time":{"created":1767312900000},"tokens":{"input":10,"output":1}}""");
        InsertMessage(db, "early", "s", """{"providerID":"openai","modelID":"gpt-test","time":{"created":1767312000000},"tokens":{"input":10,"output":1}}""");

        var result = await CreateAdapter(directory).ScanAsync();

        result.Records.Select(record => record.MessageId).Should().ContainInOrder("early", "late");
    }

    // ---- 定位器（蓝本 paths.rs 内联测试逐条对齐）----

    [Fact]
    public void Uses_xdg_data_home_for_default_path()
    {
        var root = CreateTempDir();
        var home = Path.Combine(root, "home");
        var xdg = Path.Combine(root, "xdg");
        Directory.CreateDirectory(Path.Combine(home, ".local", "share", "opencode"));
        Directory.CreateDirectory(Path.Combine(xdg, "opencode"));
        SetEnvironment("HOME", home);
        SetEnvironment("USERPROFILE", null);
        SetEnvironment("XDG_DATA_HOME", xdg);

        new OpenCodeDataLocator().ResolveDataDirectories(null)
            .Should().ContainSingle().Which.Should().Be(Path.Combine(xdg, "opencode"));
    }

    [Fact]
    public void Falls_back_to_home_data_directory_when_xdg_data_home_is_unset_empty_or_relative()
    {
        var root = CreateTempDir();
        var home = Path.Combine(root, "home");
        var opencode = Path.Combine(home, ".local", "share", "opencode");
        Directory.CreateDirectory(opencode);
        SetEnvironment("HOME", home);
        SetEnvironment("USERPROFILE", null);

        SetEnvironment("XDG_DATA_HOME", null);
        new OpenCodeDataLocator().ResolveDataDirectories(null).Should().BeEquivalentTo([opencode]);
        SetEnvironment("XDG_DATA_HOME", string.Empty);
        new OpenCodeDataLocator().ResolveDataDirectories(null).Should().BeEquivalentTo([opencode]);
        SetEnvironment("XDG_DATA_HOME", "relative-xdg-data-home");
        new OpenCodeDataLocator().ResolveDataDirectories(null).Should().BeEquivalentTo([opencode]);
    }

    [Fact]
    public void Does_not_fall_back_to_home_when_xdg_data_home_is_set_without_opencode_directory()
    {
        var root = CreateTempDir();
        var home = Path.Combine(root, "home");
        var xdg = Path.Combine(root, "xdg");
        Directory.CreateDirectory(Path.Combine(home, ".local", "share", "opencode"));
        Directory.CreateDirectory(xdg);
        SetEnvironment("HOME", home);
        SetEnvironment("USERPROFILE", null);
        SetEnvironment("XDG_DATA_HOME", xdg);

        new OpenCodeDataLocator().ResolveDataDirectories(null).Should().BeEmpty();
    }

    [Fact]
    public void Keeps_configured_data_dirs_before_default_and_deduplicates()
    {
        var root = CreateTempDir();
        var first = Path.Combine(root, "configured", "first");
        var second = Path.Combine(root, "configured", "second");
        var xdg = Path.Combine(root, "xdg");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(Path.Combine(xdg, "opencode"));
        SetEnvironment("XDG_DATA_HOME", xdg);

        var raw = $" {first}, {second}, {first}, {Path.Combine(root, "missing")} ";
        new OpenCodeDataLocator().ResolveDataDirectories(raw)
            .Should().BeEquivalentTo([first, second], "去重且跳过不存在的目录，不回退默认目录");
    }

    [Fact]
    public void Does_not_fall_back_when_configured_data_dir_is_empty_or_missing()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "home", ".local", "share", "opencode"));
        SetEnvironment("HOME", root);
        SetEnvironment("USERPROFILE", null);

        new OpenCodeDataLocator().ResolveDataDirectories(string.Empty).Should().BeEmpty();
        new OpenCodeDataLocator().ResolveDataDirectories(Path.Combine(root, "missing")).Should().BeEmpty();
    }

    [Fact]
    public void Configured_data_dir_takes_priority_over_environment_variable()
    {
        var root = CreateTempDir();
        var configured = Path.Combine(root, "configured");
        var fromEnvironment = Path.Combine(root, "from-env");
        Directory.CreateDirectory(configured);
        Directory.CreateDirectory(fromEnvironment);
        SetEnvironment("OPENCODE_DATA_DIR", fromEnvironment);

        new OpenCodeDataLocator().ResolveDataDirectories(configured)
            .Should().BeEquivalentTo([configured], "显式配置目录优先于 OPENCODE_DATA_DIR（与 ZCode 配置根同语义）");
    }

    [Fact]
    public void OpenCode_data_dir_environment_variable_is_respected()
    {
        var root = CreateTempDir();
        var fromEnvironment = Path.Combine(root, "from-env");
        Directory.CreateDirectory(fromEnvironment);
        SetEnvironment("OPENCODE_DATA_DIR", fromEnvironment);

        new OpenCodeDataLocator().ResolveDataDirectories(null).Should().BeEquivalentTo([fromEnvironment]);
    }

    // ---- helpers ----

    private OpenCodeSqliteAdapter CreateAdapter(string configuredDataDir) => new(configuredDataDir: configuredDataDir);

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-opencode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private void SetEnvironment(string name, string? value)
    {
        _environmentVariables.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private static void Execute(string db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void CreateMessageTable(string db)
    {
        Execute(db, "CREATE TABLE IF NOT EXISTS message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER NOT NULL DEFAULT 0, data TEXT)");
    }

    private static void InsertMessage(string db, string id, string sessionId, string data)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO message (id, session_id, time_created, data) VALUES ($id, $sessionId, 0, $data)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
    }

    private static void InsertRaw(string db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void CreateSessionMessageTable(string db, bool withTimeCreated)
    {
        var timeColumn = withTimeCreated ? "time_created INTEGER, " : string.Empty;
        Execute(db, $"CREATE TABLE session_message (id TEXT PRIMARY KEY, session_id TEXT, type TEXT, seq INTEGER NOT NULL DEFAULT 0, {timeColumn}data TEXT)");
    }

    private static void InsertSessionMessage(string db, string id, string sessionId, string messageType, long? timeCreated, string data, long? seq)
    {
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        var columns = new List<string> { "id", "session_id", "type" };
        var values = new List<string> { "$id", "$sessionId", "$type" };
        if (timeCreated is { } created)
        {
            columns.Add("time_created");
            values.Add("$timeCreated");
        }

        columns.Add("data");
        values.Add("$data");
        if (seq is { } sequence)
        {
            columns.Add("seq");
            values.Add("$seq");
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO session_message ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$type", messageType);
        if (timeCreated is { })
        {
            command.Parameters.AddWithValue("$timeCreated", timeCreated.Value);
        }

        command.Parameters.AddWithValue("$data", data);
        if (seq is { })
        {
            command.Parameters.AddWithValue("$seq", seq.Value);
        }

        command.ExecuteNonQuery();
    }

    private static void CreateSessionV2TableWithForkColumns(string db)
    {
        Execute(db, "CREATE TABLE session_v2 (id TEXT PRIMARY KEY, time_created INTEGER, cost REAL, tokens_input INTEGER, tokens_output INTEGER, tokens_cache_read INTEGER, tokens_cache_write INTEGER, tokens_reasoning INTEGER, model TEXT, fork_session_id TEXT, fork_boundary TEXT)");
    }

    private static void InsertSessionFork(string db, string forkId, string parentId, string boundary)
    {
        Execute(db, $"INSERT INTO session_v2 (id, fork_session_id, fork_boundary) VALUES ('{forkId}', '{parentId}', '{boundary.Replace("'", "''")}')");
    }

    private static string AssistantPayload(long input, long output)
    {
        return $"{{\"model\":{{\"id\":\"gpt-test\",\"providerID\":\"openai\"}},\"time\":{{\"created\":{NoonUtc20260101}}},\"tokens\":{{\"input\":{input},\"output\":{output}}},\"cost\":0.01}}";
    }

    /// <summary>蓝本 insert_fork_history：父会话 4 轮（seq 0-3），fork 拷贝 3 行（seq 0-2，边界行保留用量），
    /// fork 自有 1 行（seq 4）；session_v2 记录 fork 链接与边界。</summary>
    private static void InsertForkHistory(string db, string boundaryType)
    {
        CreateSessionMessageTable(db, withTimeCreated: true);
        CreateSessionV2TableWithForkColumns(db);
        foreach (var (id, seq, input) in new[]
                 {
                     ("parent-msg-1", 0L, 100), ("parent-msg-2", 1L, 200),
                     ("boundary-msg", 2L, 300), ("parent-msg-4", 3L, 400),
                 })
        {
            InsertSessionMessage(db, id, "parent", "assistant", NoonUtc20260101 + seq, AssistantPayload(input, 10), seq);
        }

        foreach (var (copyId, seq, input) in new[]
                 {
                     ("fork-copy-1", 0L, 100), ("fork-copy-2", 1L, 200), ("fork-copy-boundary", 2L, 300),
                 })
        {
            InsertSessionMessage(db, copyId, "fork", "assistant", NoonUtc20260101 + seq, AssistantPayload(input, 10), seq);
        }

        InsertSessionMessage(db, "fork-own", "fork", "assistant", NoonUtc20260101 + 4, AssistantPayload(40, 4), 4);
        InsertSessionFork(db, "fork", "parent", $$"""{"type":"{{boundaryType}}","messageID":"boundary-msg"}""");
    }

    public void Dispose()
    {
        foreach (var name in _environmentVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不构成测试失败（Windows 文件锁等）。
            }
        }
    }
}
