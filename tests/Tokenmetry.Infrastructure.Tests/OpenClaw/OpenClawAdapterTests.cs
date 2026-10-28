using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.Tests.OpenClaw;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.OpenClaw;

/// <summary>
/// I-020: OpenClaw 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage/rust/adapters/openclaw/ 的 paths.rs / parser.rs / loader.rs / sqlite.rs 内联测试，
/// 同名场景逐一对齐：归档会话文件判定、model_change/model-snapshot 状态跟踪、lenient 字段角落、
/// total 兜底、SQLite 直读与坏行、多根发现与 env 语义、双格式优先级去重）。
/// </summary>
public class OpenClawAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly List<string> _environmentVariables = new();

    // ---- 蓝本 paths.rs 内联测试同名场景 ----

    [Fact]
    public void Detects_archived_session_files_as_openclaw_sessions()
    {
        OpenClawLogLocator.IsOpenClawSessionFile("a.jsonl").Should().BeTrue();
        OpenClawLogLocator.IsOpenClawSessionFile("a.jsonl.deleted.1700000000000").Should().BeTrue();
        OpenClawLogLocator.IsOpenClawSessionFile("a.jsonl.reset.2026-03-20T06-34-44.520Z").Should().BeTrue();
        OpenClawLogLocator.IsOpenClawSessionFile("a.json").Should().BeFalse();
        OpenClawLogLocator.IsOpenClawSessionFile("a.jsonlfoo").Should().BeFalse("后缀须恰为 .jsonl 或归档后缀");
        OpenClawLogLocator.IsOpenClawSessionFile("plain.txt").Should().BeFalse();
    }

    [Theory]
    [InlineData("abc.jsonl", "abc")]
    [InlineData("abc.jsonl.deleted.1700000000000", "abc")]
    [InlineData("abc.jsonl.reset.2026-03-20T06-34-44.520Z", "abc")]
    [InlineData("a.b.jsonl", "a.b")]
    [InlineData(".jsonl", ".jsonl")]
    [InlineData("no-extension", "no-extension")]
    public void Extracts_session_id_from_file_name(string fileName, string expected)
    {
        OpenClawLogLocator.SessionIdFromFileName(
            Path.Combine(CreateTempDir(), "nested", fileName)).Should().Be(expected);
    }

    // ---- 蓝本 parser.rs 内联测试同名场景 ----

    [Fact]
    public async Task Malformed_message_does_not_drop_model_tracking_line()
    {
        // model_change 行的 message 为非对象：行不被丢弃，模型/provider 状态照常更新，
        // 后续 usage 行仍取到跟踪模型（lenient_object 语义）。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session.jsonl",
            """{"type":"model_change","modelId":"gpt-5.2","provider":"openai","message":"not-an-object"}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":10,"output":20}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("[openclaw] gpt-5.2");
        record.InputTokens.Should().Be(10);
        record.OutputTokens.Should().Be(20);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
    }

    [Fact]
    public async Task Falls_back_to_total_tokens_when_openclaw_parts_are_missing()
    {        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"totalTokens":222}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(222, "total 缺口在 output 为 0 时优先补 output");
        record.InputTokens.Should().Be(0);
    }

    [Fact]
    public async Task Decorated_model_keeps_raw_pricing_identity_as_pricing_debt()
    {
        // 蓝本 calculates_decorated_deepseek_models_with_the_raw_pricing_identity 的桌面等价：
        // 统一记录带 "[openclaw] " 前缀；内建成本直采（Auto）；无内建成本时交定价引擎
        // （裸模型计价候选接线为 I-024 已登记欠账，桌面引擎按装饰名不命中 → CostUsd null fail-loud）。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session.jsonl",
            """{"type":"message","message":{"role":"assistant","model":"deepseek-v4-flash","usage":{"input":1000000,"output":0,"totalTokens":1000000,"cost":{"total":0.44}},"timestamp":"2026-08-17T01:00:00Z"}}""",
            """{"type":"message","message":{"role":"assistant","model":"no-cost-model","usage":{"input":1,"output":2},"timestamp":"2026-08-17T01:00:01Z"}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records[0].Model.Should().Be("[openclaw] deepseek-v4-flash");
        result.Records[0].CostUsd.Should().Be(0.44m, "usage.cost.total 内建成本直采");
        result.Records[1].CostUsd.Should().BeNull("无内建成本交定价引擎（装饰名未命中为 I-024 欠账）");
    }

    [Theory]
    [InlineData("1769753935279")]
    [InlineData("0")]
    public async Task Timestamp_accepts_numeric_millis(string rawMillis)
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            $$$"""{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":{{{rawMillis}}}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should().Be(
            DateTimeOffset.UnixEpoch.AddMilliseconds(long.Parse(rawMillis)), "数值毫秒直接换算（UTC 时刻）");
    }

    [Fact]
    public async Task Timestamp_accepts_strict_rfc3339_text_with_offset()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":"2026-08-17T09:00:00+08:00"}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should().Be(
            new DateTimeOffset(2026, 8, 17, 1, 0, 0, TimeSpan.Zero), "偏移换算为 UTC 时刻");
    }

    [Fact]
    public async Task Null_message_timestamp_falls_back_to_root_level_timestamp()
    {
        // 蓝本 message.timestamp.as_ref().or(record.timestamp.as_ref())：null 视为缺失落到根级。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"message","timestamp":1769753935279,"message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":null}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should().Be(
            DateTimeOffset.UnixEpoch.AddMilliseconds(1_769_753_935_279));
    }

    [Fact]
    public async Task Unparsable_message_timestamp_falls_back_to_file_mtime_not_root_timestamp()
    {
        // 元素选取先于解析：message.timestamp 存在但不可解析 → 直接回退文件 mtime（根级不参与）。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"message","timestamp":1769753935279,"message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":"not-a-timestamp"}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var timestamp = result.Records.Should().ContainSingle().Subject.Timestamp;
        timestamp.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5), "回退到刚写入文件的 mtime");
        timestamp.Should().NotBe(DateTimeOffset.UnixEpoch.AddMilliseconds(1_769_753_935_279));
    }

    [Fact]
    public async Task Negative_timestamp_falls_back_to_file_mtime()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":-5}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should()
            .BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5), "负毫秒不可用，回退 mtime");
    }

    [Fact]
    public async Task Non_object_data_falls_back_to_root_level_model_fields()
    {
        // 蓝本 deserialize_model_source：data 非对象 → None → 根级 modelId/provider 参与回退。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"model_change","data":"corrupt","modelId":"root-model","provider":"root-provider"}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Model.Should().Be("[openclaw] root-model");
    }

    [Fact]
    public async Task Empty_data_object_blocks_root_level_model_fallback()
    {
        // 蓝本 model_change_source 的 match：data 块存在（即使全空）即只读 data 内字段。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"model_change","data":{},"modelId":"root-model"}""",
            """{"type":"message","message":{"role":"assistant","model":"inline-model","usage":{"input":1,"output":1}}}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":2,"output":2}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records[0].Model.Should().Be("[openclaw] inline-model", "行内模型优先");
        result.Records[1].Model.Should().Be("[openclaw] unknown", "data 空对象不产生状态更新，根级字段不回退");
    }

    [Fact]
    public async Task Model_snapshot_custom_type_updates_state()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"custom","customType":"model-snapshot","data":{"modelId":"snapshot-model","provider":"snap"}}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Model.Should().Be("[openclaw] snapshot-model");
    }

    [Fact]
    public async Task Model_tracking_state_resets_per_session_file()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/first.jsonl",
            """{"type":"model_change","modelId":"gpt-5.2"}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1}}}""");
        WriteSession(root, "agents/main/sessions/second.jsonl",
            """{"type":"message","message":{"role":"assistant","usage":{"input":2,"output":2}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records.Single(record => record.SessionId == "first").Model.Should().Be("[openclaw] gpt-5.2");
        result.Records.Single(record => record.SessionId == "second").Model.Should().Be("[openclaw] unknown",
            "跟踪状态仅在单文件内存活（蓝本 parse_session_file 局部变量）");
    }

    [Fact]
    public async Task Lenient_token_fields_reject_unexpected_shapes_as_zero()
    {
        // lenient_u64：字符串 / 负数 / 浮点 / null → 0（as_u64 语义）。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"message","message":{"role":"assistant","usage":{"input":"10","output":-5,"cacheRead":3.7,"cacheWrite":null,"totalTokens":4}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(0);
        record.OutputTokens.Should().Be(4, "output 0 时 total 缺口补 output");
        record.CacheReadTokens.Should().Be(0);
        record.CacheCreationTokens.Should().Be(0);
    }

    [Fact]
    public async Task Zero_embedded_cost_is_kept_as_log_cost()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1,"cost":{"total":0}},"timestamp":1769753935279}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.CostUsd.Should().Be(0m, "Auto 对 Some(0.0) 记 0 不再计价");
    }

    [Fact]
    public async Task Jsonl_records_drop_the_event_id_from_the_message_identity()
    {
        // 蓝本 parse_message_entry 丢弃 message_id：JSONL 时代记录无稳定 id，统一记录 MessageId 恒 null。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"id":"evt-1","type":"message","message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":1769753935279}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.MessageId.Should().BeNull("JSONL 侧不使用事件 id（蓝本显式丢弃）");
    }

    [Fact]
    public async Task Usage_line_without_usage_payload_is_counted_not_recorded()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"message","message":{"role":"assistant","model":"m"},"timestamp":1769753935279}""",
            """{"type":"message","message":{"role":"user","usage":{"input":1,"output":1}},"timestamp":1769753935279}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":0,"output":0}},"timestamp":1769753935279}""",
            """{"type":"summary","note":"mentions usage"}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":5,"output":5},"timestamp":1769753935279}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(5);
        result.Diagnostics.LinesSkipped.Should().Be(4, "缺 usage 块 / 非 assistant / 全零 / 非 message 型行");
        result.Diagnostics.InvalidRecords.Should().Be(0);
    }

    [Fact]
    public async Task Unusable_message_block_is_counted_as_invalid_record()
    {
        // message 非对象 / role 非(null/字符串) / usage 非(null/对象) → message 反序列化失败。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"type":"message","usage":null,"message":"corrupted","timestamp":1769753935279}""",
            """{"type":"message","message":{"role":123,"usage":{"input":1,"output":1}},"timestamp":1769753935279}""",
            """{"type":"message","message":{"role":"user","usage":"scalar"},"timestamp":1769753935279}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":5,"output":5}},"timestamp":1769753935279}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(5);
        result.Diagnostics.InvalidRecords.Should().Be(3, "可用性判据先于 role/usage 语义分支（serde 同拒绝面）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Structural_failures_are_counted_as_malformed()
    {
        // serde 结构性失败：坏 JSON、顶层非对象、type/customType 非(null/字符串)。
        // 注意行预过滤：无标记行计 LinesSkipped，结构失败面只覆盖含标记的行。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session-a.jsonl",
            """{"brok"usage"en""",
            """["usage"]""",
            """{"type":123,"usage":{}}""",
            """{"type":"message","customType":false,"message":{"role":"assistant","usage":{"input":1,"output":1}}}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":5,"output":5}},"timestamp":1769753935279}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(5);
        result.Diagnostics.MalformedLines.Should().Be(4);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    // ---- 蓝本 loader.rs 内联测试同名场景 ----

    [Fact]
    public async Task Loads_assistant_usage_and_uses_model_change_events()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/abc.jsonl",
            """{"type":"model_change","provider":"openai-codex","modelId":"gpt-5.2"}""",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1660,"output":55,"cacheRead":108928,"cost":{"total":0.02}},"timestamp":1769753935279}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(DateTimeOffset.UnixEpoch.AddMilliseconds(1_769_753_935_279),
            "毫秒 → UTC 时刻（2026-01-30）");
        record.SessionId.Should().Be("abc");
        record.Model.Should().Be("[openclaw] gpt-5.2");
        record.InputTokens.Should().Be(1660);
        record.OutputTokens.Should().Be(55);
        record.CacheReadTokens.Should().Be(108_928);
        record.CacheCreationTokens.Should().Be(0);
        record.CostUsd.Should().Be(0.02m);
        record.Agent.Should().Be("OpenClaw");
        record.ProjectPath.Should().Be("OpenClaw");
    }

    [Fact]
    public async Task Deduplicates_repeated_openclaw_records()
    {
        var root = CreateOpenClawRoot();
        var line = """{"type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":1,"output":1,"totalTokens":2},"timestamp":1769753935279}}""";
        WriteSession(root, "agents/main/sessions/session.jsonl", line, line);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.DuplicateRecords.Should().Be(1, "内容键（migration_id + 成本）first-wins 折叠");
    }

    [Fact]
    public async Task Creates_sqlite_records_when_no_jsonl_sessions_exist()
    {
        var root = CreateOpenClawRoot();
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-sqlite", 1_769_753_935_279, """{"id":"evt-1","type":"model_change","modelId":"deepseek-v4-flash","provider":"deepseek"}"""),
            ("session-sqlite", 1_769_753_935_279, """{"id":"evt-2","type":"message","message":{"role":"assistant","usage":{"input":100,"output":50,"cost":{"total":1.80}},"timestamp":1769753935279}}"""));

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(DateTimeOffset.UnixEpoch.AddMilliseconds(1_769_753_935_279));
        record.SessionId.Should().Be("session-sqlite", "会话 id 取 transcript_events 列值");
        record.Model.Should().Be("[openclaw] deepseek-v4-flash", "模型来自 model_change 状态");
        record.CostUsd.Should().Be(1.80m);
        record.MessageId.Should().Be("evt-2", "SQLite 行以事件 id 作消息级去重标识");
    }

    [Fact]
    public async Task Prefers_sqlite_over_migrated_jsonl_duplicates()
    {
        // 本卡核心：同一事件的迁移 SQLite 副本（带稳定 id + 修正成本）替换 JSONL 遗留副本。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session.jsonl",
            """{"type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":10,"output":20,"cost":{"total":0.50}},"timestamp":1769753935279}}""");
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session", 1_769_753_935_279, """{"id":"evt-sqlite","type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":10,"output":20,"cost":{"total":0.51}},"timestamp":1769753935279}}"""));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        var record = result.Records[0];
        record.MessageId.Should().Be("evt-sqlite", "SQLite 行胜出");
        record.CostUsd.Should().Be(0.51m, "provider 结算成本随行替换（migration_id 不含成本）");
        result.Diagnostics.DuplicateRecords.Should().Be(1, "迁移副本替换显式计数");
    }

    [Fact]
    public async Task Keeps_both_copies_when_usage_identity_differs()
    {
        // 迁移身份（会话 + 时间戳 + 模型 + 用量）不同 → 两条都保留。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session.jsonl",
            """{"type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":10,"output":20,"cost":{"total":0.50}},"timestamp":1769753935279}}""");
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session", 1_769_753_935_279, """{"id":"evt-x","type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":99,"output":20,"cost":{"total":0.51}},"timestamp":1769753935279}}"""));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Diagnostics.DuplicateRecords.Should().Be(0);
    }

    [Fact]
    public async Task Deduplicates_sqlite_rows_by_event_id_across_databases()
    {
        var root = CreateOpenClawRoot();
        var eventJson = """{"id":"evt-shared","type":"message","message":{"role":"assistant","model":"m","usage":{"input":1,"output":1},"timestamp":1769753935279}}""";
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-a", 1_769_753_935_279, eventJson));
        CreateAgentDatabase(Path.Combine(root, "agents", "worker", "agent", "openclaw-agent.sqlite"),
            ("session-a", 1_769_753_935_279, eventJson));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle("openclaw:<session>:<event id> 去重跨库生效");
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    [Fact]
    public async Task Deduplicates_id_less_sqlite_rows_by_content_key()
    {
        var root = CreateOpenClawRoot();
        var eventJson = """{"type":"message","message":{"role":"assistant","model":"m","usage":{"input":1,"output":1},"timestamp":1769753935279}}""";
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-a", 1_769_753_935_279, eventJson),
            ("session-a", 1_769_753_935_279, eventJson));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle("无事件 id 的行回退内容键 first-wins");
    }

    [Fact]
    public async Task Sqlite_replaces_jsonl_copy_that_carries_the_same_event_id()
    {
        // JSONL 行带 id（蓝本 JSONL 路径仍丢弃）→ 身份匹配走迁移键而非消息 id。
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session.jsonl",
            """{"id":"evt-9","type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":10,"output":20,"cost":{"total":0.50}},"timestamp":1769753935279}}""");
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session", 1_769_753_935_279, """{"id":"evt-9","type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":10,"output":20,"cost":{"total":0.51}},"timestamp":1769753935279}}"""));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.CostUsd.Should().Be(0.51m);
    }

    [Fact]
    public async Task Sorts_merged_records_by_timestamp()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/b-late.jsonl",
            """{"type":"message","message":{"role":"assistant","usage":{"input":2,"output":2},"timestamp":1773964800000}}""");
        WriteSession(root, "agents/main/sessions/a-early.jsonl",
            """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":1769753935279}}""");
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-mid", 1_769_753_935_279, """{"type":"message","message":{"role":"assistant","usage":{"input":3,"output":3},"timestamp":1770000000000}}"""));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Select(record => record.InputTokens).Should().ContainInOrder([1, 3, 2],
            "蓝本 sort_by_key(timestamp) 稳定排序");
    }

    // ---- 蓝本 sqlite.rs 内联测试同名场景 ----

    [Fact]
    public async Task Skips_malformed_rows_without_dropping_the_session()
    {
        var root = CreateOpenClawRoot();
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-a", 1_769_753_935_279, """{"id":"evt-1","type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":10,"output":20},"timestamp":1769753935279}}"""),
            ("session-a", 1_769_753_935_279, "not json"),
            ("session-a", 1_769_753_935_279, """{"id":"evt-3","type":"message","message":{"role":"assistant","model":"gpt-5.2","usage":{"input":30,"output":40},"timestamp":1769753935279}}"""));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records[0].InputTokens.Should().Be(10);
        result.Records[1].InputTokens.Should().Be(30);
        result.Diagnostics.MalformedLines.Should().Be(1);
    }

    [Fact]
    public async Task Ignores_databases_without_transcript_events()
    {
        var root = CreateOpenClawRoot();
        var dbPath = Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE other (id TEXT)";
            command.ExecuteNonQuery();
        }

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData,
            "缺 transcript_events 表按蓝本 debug 静默跳过，不算问题（不降级 Partial）");
    }

    [Fact]
    public async Task Discovers_agent_databases_in_session_order()
    {
        var root = CreateOpenClawRoot();
        CreateAgentDatabase(Path.Combine(root, "agents", "worker", "agent", "openclaw-agent.sqlite"),
            ("session-w", 1_769_753_935_279, """{"type":"message","message":{"role":"assistant","usage":{"input":2,"output":2},"timestamp":1769753935279}}"""));
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-m", 1_769_753_935_279, """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":1769753935279}}"""));
        Directory.CreateDirectory(Path.Combine(root, "agents", "empty", "agent"));
        File.WriteAllText(Path.Combine(root, "agents", "empty", "agent", "not-a-database.sqlite"), "");

        var databases = OpenClawSqliteReader.CollectAgentDatabases(root);

        databases.Should().HaveCount(2);
        databases[0].Should().EndWith(Path.Combine("agents", "main", "agent", "openclaw-agent.sqlite"), "按路径排序");
        databases[1].Should().EndWith(Path.Combine("agents", "worker", "agent", "openclaw-agent.sqlite"));
    }

    [Fact]
    public void Ignores_symlinked_database_paths()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = CreateOpenClawRoot();
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "openclaw-agent.sqlite"), "");
        // agents/file-link/agent 为真实目录、库文件本身是符号链接（文件链接不发现）。
        var fileLinkAgent = Path.Combine(root, "agents", "file-link", "agent");
        Directory.CreateDirectory(fileLinkAgent);
        File.CreateSymbolicLink(Path.Combine(fileLinkAgent, "openclaw-agent.sqlite"),
            Path.Combine(outside, "openclaw-agent.sqlite"));
        // agents/directory-link 目录本身是符号链接（目录链接不发现）。
        Directory.CreateSymbolicLink(Path.Combine(root, "agents", "directory-link"), outside);

        OpenClawSqliteReader.CollectAgentDatabases(root).Should().BeEmpty();
    }

    [Fact]
    public async Task Sqlite_session_boundary_resets_model_tracking_state()
    {
        var root = CreateOpenClawRoot();
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-a", 1, """{"type":"model_change","modelId":"gpt-5.2"}"""),
            ("session-a", 2, """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":1769753935279}}"""),
            ("session-b", 3, """{"type":"message","message":{"role":"assistant","usage":{"input":2,"output":2},"timestamp":1769753935279}}"""));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records.Single(record => record.SessionId == "session-a").Model.Should().Be("[openclaw] gpt-5.2");
        result.Records.Single(record => record.SessionId == "session-b").Model.Should().Be("[openclaw] unknown",
            "SQLite 行按 session_id 分组连续，会话边界重置跟踪状态");
    }

    [Fact]
    public async Task Sqlite_uses_created_at_when_event_has_no_timestamp()
    {
        var root = CreateOpenClawRoot();
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-a", 1_769_753_935_279, """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1}}}"""));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should().Be(
            DateTimeOffset.UnixEpoch.AddMilliseconds(1_769_753_935_279), "created_at 列作回退时间戳");
    }

    [Fact]
    public async Task Sqlite_real_created_at_is_truncated_and_negative_clamped_to_epoch()
    {
        var root = CreateOpenClawRoot();
        var dbPath = Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite");
        CreateAgentDatabase(dbPath,
            ("session-a", -5, """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1}}}"""),
            ("session-b", 1_769_753_935_279, """{"type":"message","message":{"role":"assistant","usage":{"input":2,"output":2}}}"""));
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE transcript_events SET created_at = 1769753935279.6 WHERE session_id = 'session-b'";
            command.ExecuteNonQuery();
        }

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Single(record => record.SessionId == "session-a").Timestamp.Should().Be(DateTimeOffset.UnixEpoch,
            "负 created_at 按 max(0) 归纪元");
        result.Records.Single(record => record.SessionId == "session-b").Timestamp.Should().Be(
            DateTimeOffset.UnixEpoch.AddMilliseconds(1_769_753_935_279), "REAL 截断毫秒");
    }

    [Fact]
    public async Task Sqlite_non_text_identity_columns_are_counted_invalid()
    {
        var root = CreateOpenClawRoot();
        var dbPath = Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite");
        CreateAgentDatabase(dbPath,
            ("session-a", 1_769_753_935_279, """{"type":"message","message":{"role":"assistant","usage":{"input":1,"output":1},"timestamp":1769753935279}}"""));
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            var command = connection.CreateCommand();
            // BLOB 不受 TEXT 亲和转换：rusqlite read::<String> 同样失败 → 行拒绝。
            command.CommandText = "INSERT INTO transcript_events (session_id, seq, event_json, created_at) VALUES (x'7f7f7f', 9, '{\"type\":\"message\"}', 0)";
            command.ExecuteNonQuery();
        }

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.InvalidRecords.Should().Be(1, "session_id 非 TEXT → 行拒绝（rusqlite 严格类型同拒绝面）");
    }

    // ---- 目录发现与环境变量（蓝本 paths.rs 语义） ----

    [Fact]
    public async Task Discovers_archived_and_nested_session_files_recursively()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/abc.jsonl",
            SessionLine(1769753935279, input: 1));
        WriteSession(root, "agents/main/sessions/archive/def.jsonl.deleted.1700000000000",
            SessionLine(1769753935300, input: 2));
        WriteSession(root, "transcripts/ghi.jsonl.reset.2026-03-20T06-34-44.520Z",
            SessionLine(1769753935400, input: 3));
        File.WriteAllText(Path.Combine(root, "notes.json"), "{}");
        File.WriteAllText(Path.Combine(root, "agents", "main", "sessions", "readme.txt"), "{}");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(3, "整根递归（含非 sessions/ 路径），归档后缀识别，非 .jsonl 后缀不发现");
        result.Diagnostics.FilesDiscovered.Should().Be(3);
        result.Records.Select(record => record.SessionId).Should().Equal("abc", "def", "ghi");
    }

    [Fact]
    public async Task Openclaw_dir_env_comma_list_is_trimmed_and_requires_existing_directories()
    {
        var first = CreateOpenClawRoot();
        WriteSession(first, "agents/main/sessions/one.jsonl", SessionLine(1769753935279, input: 1));
        var second = CreateOpenClawRoot();
        WriteSession(second, "agents/main/sessions/two.jsonl", SessionLine(1769753935280, input: 2));
        var missing = Path.Combine(CreateTempDir(), "missing");

        var locator = new OpenClawLogLocator(_ => $"  {first} ,,{missing} , {second} , {first} ");

        var roots = locator.ResolveDataDirectories(null);

        roots.Should().Equal(new[] { first, second }, "逗号分隔、去空白、丢弃不存在目录、按序去重");
    }

    [Fact]
    public async Task Blank_openclaw_dir_env_falls_back_to_default_homes()
    {
        // 蓝本 env 分支：已设置但 trim 为空 → 条件不成立 → 回退默认四根（与 Kimi/Gemini 的
        // "空串即配置生效"不同）。
        var fakeHome = CreateTempDir();
        WriteSession(Path.Combine(fakeHome, ".openclaw"), "agents/main/sessions/a.jsonl", SessionLine(1769753935279, input: 1));
        WriteSession(Path.Combine(fakeHome, ".moldbot"), "agents/main/sessions/b.jsonl", SessionLine(1769753935280, input: 2));
        Directory.CreateDirectory(Path.Combine(fakeHome, ".clawdbot")); // 存在但无会话文件

        var locator = new OpenClawLogLocator(name => name == OpenClawLogLocator.OpenClawDirEnvVariable ? "   " : fakeHome);

        var roots = locator.ResolveDataDirectories(null);

        roots.Should().HaveCount(3, ".openclaw / .clawdbot / .moldbot 存在者保留，.moltbot 缺失跳过");
        Path.GetFileName(roots[0]).Should().Be(".openclaw");
        Path.GetFileName(roots[1]).Should().Be(".clawdbot");
        Path.GetFileName(roots[2]).Should().Be(".moldbot");
    }

    [Fact]
    public async Task Openclaw_dir_env_is_used_without_default_fallback()
    {
        var relocated = CreateOpenClawRoot();
        WriteSession(relocated, "agents/main/sessions/relocated.jsonl", SessionLine(1769753935279, input: 1));
        var defaultHome = CreateTempDir();
        WriteSession(Path.Combine(defaultHome, ".openclaw"), "agents/main/sessions/decoy.jsonl", SessionLine(1769753935280, input: 9));

        SetEnvironment(OpenClawLogLocator.OpenClawDirEnvVariable, relocated);
        SetEnvironment("USERPROFILE", defaultHome);
        SetEnvironment("HOME", defaultHome);

        var result = await new OpenClawAdapter().ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(1);
        result.Roots.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(relocated));
    }

    [Fact]
    public async Task Missing_or_file_configured_root_yields_no_data()
    {
        var result = await CreateAdapter(Path.Combine(CreateTempDir(), "does-not-exist")).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);

        var fileRoot = Path.Combine(CreateTempDir(), "not-a-dir");
        File.WriteAllText(fileRoot, "file");
        var fileResult = await CreateAdapter(fileRoot).ScanAsync();

        fileResult.Records.Should().BeEmpty();
        fileResult.Roots.Should().BeEmpty("配置根不存在即丢弃（existing_path_list 过滤）");
    }

    [Fact]
    public async Task Multi_root_scan_reads_roots_in_order_and_dedupes_across_roots()
    {
        var first = CreateOpenClawRoot();
        var second = CreateOpenClawRoot();
        var sharedLine = SessionLine(1769753935279, input: 7, model: "shared-model");
        // 同名会话文件（同 session id）携带同一行 → 跨根内容键（migration_id 含会话）去重。
        WriteSession(first, "agents/main/sessions/shared.jsonl", sharedLine);
        WriteSession(second, "agents/main/sessions/shared.jsonl", sharedLine,
            SessionLine(1769753935280, input: 8, model: "second-only"));

        var result = await CreateAdapter($"{first},{second}").ScanAsync();

        result.Records.Should().HaveCount(2, "first 先入胜出，second 的重复行折叠、独有行保留");
        result.Records.Sum(record => record.InputTokens).Should().Be(15);
        result.Diagnostics.DuplicateRecords.Should().Be(1);
        result.Roots.Should().HaveCount(2);
    }

    // ---- helpers ----

    // ---- I-042 延伸：SQLite 边车感知文件戳探针（JSONL 侧沿用共享扫描器增量） ----

    private const string OpenClawSqliteEvent =
        """{"id":"evt-1","type":"message","message":{"role":"assistant","usage":{"input":100,"output":50},"timestamp":1769753935279}}""";

    [Fact]
    public async Task ChangeProbe_UnchangedSecondScan_SkipsRereadAndReusesCachedRecords()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session.jsonl",
            SessionLine(1_700_000_000_000, input: 10, output: 5));
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-sqlite", 1_769_753_935_279, OpenClawSqliteEvent));
        var adapter = CreateAdapter(root);

        var first = await adapter.ScanAsync();
        first.Diagnostics.FilesRead.Should().Be(2, "首轮无记忆：SQLite 库 + JSONL 各读一次");
        first.Diagnostics.FilesChanged.Should().Be(1);
        first.Records.Should().HaveCount(2);

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesChanged.Should().Be(0, "SQLite 库戳与 JSONL 文件戳均未动");
        second.Diagnostics.FilesRead.Should().Be(0, "零变更轮零读取：SQLite 记忆复用 + 扫描器零变更缓存");
        second.Records.Should().HaveCount(2, "记录集来自上轮真实读取的复用，注册表累计口径不变");
        second.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        second.Diagnostics.FilesDiscovered.Should().Be(2);
    }

    [Fact]
    public async Task ChangeProbe_SqliteRowAppended_DetectedAsChange()
    {
        var root = CreateOpenClawRoot();
        var db = Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite");
        WriteSession(root, "agents/main/sessions/session.jsonl",
            SessionLine(1_700_000_000_000, input: 10, output: 5));
        CreateAgentDatabase(db, ("session-sqlite", 1_769_753_935_279, OpenClawSqliteEvent));
        var adapter = CreateAdapter(root);
        (await adapter.ScanAsync()).Records.Should().HaveCount(2);

        // 重建库文件（新增一行事件）：戳必变。
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(db);
        CreateAgentDatabase(db,
            ("session-sqlite", 1_769_753_935_279, OpenClawSqliteEvent),
            ("session-sqlite", 1_769_753_935_280, OpenClawSqliteEvent
                .Replace("evt-1", "evt-2")
                .Replace("1769753935279", "1769753935280")));

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesChanged.Should().Be(1, "SQLite 库内容变化（文件戳必变）");
        second.Records.Should().HaveCount(3);
    }

    [Fact]
    public async Task ChangeProbe_JsonlAppended_DetectedAsChange()
    {
        var root = CreateOpenClawRoot();
        var sessionFile = Path.Combine(root, "agents", "main", "sessions", "session.jsonl");
        WriteSession(root, "agents/main/sessions/session.jsonl",
            SessionLine(1_700_000_000_000, input: 10, output: 5));
        CreateAgentDatabase(Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite"),
            ("session-sqlite", 1_769_753_935_279, OpenClawSqliteEvent));
        var adapter = CreateAdapter(root);
        (await adapter.ScanAsync()).Records.Should().HaveCount(2);

        WriteSession(root, "agents/main/sessions/session.jsonl",
            SessionLine(1_700_000_000_000, input: 10, output: 5),
            SessionLine(1_700_000_060_000, input: 20, output: 8));

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesChanged.Should().Be(1, "JSONL 文件戳变化（共享扫描器记账）");
        second.Records.Should().HaveCount(3);
    }

    [Fact]
    public async Task ChangeProbe_ReusedRoundKeepsPartialStatusAndBadRowCounts()
    {
        // 复用轮拿到的诊断必须带着上轮的真实坏行事实：落全零占位会让设置页看到
        // 坏行数忽 3 忽 0、Partial 被洗成 Ok（“坏数据自己好了”）。
        var root = CreateOpenClawRoot();
        var dbPath = Path.Combine(root, "agents", "main", "agent", "openclaw-agent.sqlite");
        CreateAgentDatabase(dbPath, ("session-sqlite", 1_769_753_935_279, OpenClawSqliteEvent));
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            var command = connection.CreateCommand();
            // session_id 非 TEXT（BLOB）→ 行拒绝（与 rusqlite 严格类型同拒绝面）。
            command.CommandText =
                "INSERT INTO transcript_events (session_id, seq, event_json, created_at) VALUES (x'7f7f7f', 9, '{\"type\":\"message\"}', 0)";
            command.ExecuteNonQuery();
        }

        var adapter = CreateAdapter(root);
        var first = await adapter.ScanAsync();
        first.Diagnostics.InvalidRecords.Should().Be(1);
        first.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesRead.Should().Be(0, "零变更轮零读取：SQLite 记忆复用");
        second.Diagnostics.InvalidRecords.Should().Be(1, "坏行计数随记录集一起复用（相邻两轮同口径）");
        second.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial, "复用轮不得把 Partial 洗成 Ok");
    }

    private OpenClawAdapter CreateAdapter(string root) => new(configuredDataDir: root);

    private string CreateOpenClawRoot()
    {
        var path = Path.Combine(CreateTempDir(), "openclaw-root");
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-openclaw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private void SetEnvironment(string name, string? value)
    {
        _environmentVariables.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// I-024 接线（OC-1 两级链）：装饰名 "[openclaw] m" 仅精确参与（ExactThenFamily 走向的第一档），
    /// 未命中回退裸模型名（raw pricing identity，蓝本 calculates_decorated_deepseek_models_with_the_
    /// raw_pricing_identity 同构）——无内建成本的可计价模型行此前记 0 + UnpricedModels（欠账登记）。
    /// </summary>
    [Fact]
    public async Task Prices_decorated_model_through_raw_identity_candidate()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session.jsonl",
            SessionLine(1_700_000_000_000, input: 1_000_000, output: 0, model: "claude-sonnet-4-5"));

        var result = await CreateAdapter(root).ScanAsync();
        var record = result.Records.Should().ContainSingle().Subject;

        record.Model.Should().Be("[openclaw] claude-sonnet-4-5", "分组/显示保留装饰名（蓝本命中装饰名时保留装饰名分组）");
        record.Pricing.Should().NotBeNull();
        record.Pricing!.ModelCandidates.Should().Equal("[openclaw] claude-sonnet-4-5", "claude-sonnet-4-5");
        record.Pricing.Walk.Should().Be(PricingCandidateWalk.ExactThenFamilyAcrossCandidates,
            "装饰名仅精确参与，家族兜底只落在裸名档（蓝本装饰名走 find_exact）");

        var priced = new PricingEngine().PriceRecord(record);
        priced.CostUsd.Should().Be(3m, "裸名精确命中 anthropic/claude-sonnet-4-5（input 1M × $3/M）");
        priced.MatchedKey.Should().Be("anthropic/claude-sonnet-4-5");
    }

    /// <summary>I-024 对齐锚点：真实未知模型（mystery-model-x 形态）在两级链下仍未命中，
    /// 行为与接线前一致（Missing + UnpricedModels fail-loud）。</summary>
    [Fact]
    public async Task Truly_unknown_decorated_model_stays_unpriced()
    {
        var root = CreateOpenClawRoot();
        WriteSession(root, "agents/main/sessions/session.jsonl",
            SessionLine(1_700_000_000_000, input: 10, output: 5, model: "mystery-model-x"));

        var result = await CreateAdapter(root).ScanAsync();
        var record = result.Records.Should().ContainSingle().Subject;

        var engine = new PricingEngine();
        engine.PriceRecord(record).IsPriced.Should().BeFalse("装饰名与裸名都未命中 → 与接线前同为 Missing");
        engine.GetUnpricedModels().Should().ContainSingle(info => info.Model == "[openclaw] mystery-model-x");
    }

    private static string SessionLine(long timestampMillis, long input, long output = 1, string model = "gpt-5.2")
        => $$$"""{"type":"message","message":{"role":"assistant","model":"{{{model}}}","usage":{"input":{{{input}}},"output":{{{output}}}},"timestamp":{{{timestampMillis}}}}}""";

    private static void WriteSession(string root, string relativePath, params string[] lines)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Join("\n", lines) + "\n");
    }

    private static void CreateAgentDatabase(string databasePath, params (string SessionId, long CreatedAt, string EventJson)[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE transcript_events (session_id TEXT NOT NULL, seq INTEGER NOT NULL, event_json TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (session_id, seq))";
            create.ExecuteNonQuery();
        }

        var seqBySession = new Dictionary<string, int>();
        foreach (var (sessionId, createdAt, eventJson) in rows)
        {
            var seq = seqBySession.TryGetValue(sessionId, out var value) ? value + 1 : 0;
            seqBySession[sessionId] = seq;
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO transcript_events (session_id, seq, event_json, created_at) VALUES ($sessionId, $seq, $eventJson, $createdAt)";
            insert.Parameters.AddWithValue("$sessionId", sessionId);
            insert.Parameters.AddWithValue("$seq", seq);
            insert.Parameters.AddWithValue("$eventJson", eventJson);
            insert.Parameters.AddWithValue("$createdAt", createdAt);
            insert.ExecuteNonQuery();
        }
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
                // 清理失败不影响测试结论。
            }
        }
    }
}
