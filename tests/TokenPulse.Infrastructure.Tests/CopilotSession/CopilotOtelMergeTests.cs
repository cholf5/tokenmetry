namespace TokenPulse.Infrastructure.Tests.CopilotSession;

using System.Globalization;
using TokenPulse.Core.Models;
using TokenPulse.Core.Services;
using TokenPulse.Infrastructure.CopilotSession;
using TokenPulse.Infrastructure.Jsonl;

/// <summary>
/// I-023: Copilot CLI OpenTelemetry 合并与积分对账测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage rust/adapters/copilot 的 parser.rs otel 块 / loader.rs reconcile + credit_gap / paths.rs
/// otel 收集，蓝本内联测试锚点逐条对齐——同名场景在测试名中保留蓝本语义，蓝本 since/until 相关
/// 用例在桌面"全量出账"架构下的塌缩方式见各测试注释与卡片偏差清单）。
/// </summary>
public class CopilotOtelMergeTests : IDisposable
{
    private const long NanoAiuPerCredit = 1_000_000_000;

    private readonly List<string> _tempDirs = new();

    // ---- otel 记录解析与去重（蓝本 parser.rs otel 块内联测试同名场景） ----

    [Fact]
    public async Task Parses_copilot_chat_spans()
    {
        var home = CreateTempHome();
        WriteOtel(home, "copilot.jsonl",
            """{"type":"metric","name":"gen_ai.client.token.usage"}""",
            """{"type":"span","traceId":"trace-1","spanId":"span-1","name":"chat claude-sonnet-4","endTime":[1775934264,967317833],"attributes":{"gen_ai.operation.name":"chat","gen_ai.request.model":"claude-sonnet-4","gen_ai.response.model":"claude-sonnet-4","gen_ai.conversation.id":"conv-1","gen_ai.usage.input_tokens":19452,"gen_ai.usage.output_tokens":281,"gen_ai.usage.cache_read.input_tokens":123,"gen_ai.usage.cache_creation.input_tokens":25,"gen_ai.usage.reasoning.output_tokens":128}}""");

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 4, 11, 19, 4, 24, 967, TimeSpan.Zero),
            "endTime [秒, 纳秒] 数组换算毫秒");
        record.SessionId.Should().Be("conv-1");
        record.Model.Should().Be("claude-sonnet-4");
        record.InputTokens.Should().Be(19_304, "uncached = 19452 − (123 cacheRead + 25 cacheWrite)");
        record.OutputTokens.Should().Be(281);
        record.CacheCreationTokens.Should().Be(25);
        record.CacheReadTokens.Should().Be(123);
        record.TotalTokens.Should().Be(19_733);
        record.CostUsd.Should().BeNull("otel 行交定价引擎按模型定价（蓝本 cost-at-timestamp 口径）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
    }

    [Fact]
    public async Task Suppresses_lower_priority_records_for_same_response()
    {
        var home = CreateTempHome();
        WriteOtel(home, "copilot.jsonl",
            // agent summary span（最低优先级）：同 trace + 同 response id → 被 chat span 抑制
            """{"type":"span","traceId":"trace-dupe","spanId":"agent-1","name":"invoke_agent GitHub Copilot Chat","attributes":{"gen_ai.operation.name":"invoke_agent","gen_ai.response.model":"gpt-5.4-mini","gen_ai.conversation.id":"conv-dupe","gen_ai.response.id":"resp-dupe","gen_ai.usage.input_tokens":100,"gen_ai.usage.output_tokens":30}}""",
            // inference log（次低）：同 response id → 抑制
            """{"hrTime":[1775934263,0],"attributes":{"event.name":"gen_ai.client.inference.operation.details","gen_ai.response.model":"gpt-5.4-mini","gen_ai.response.id":"resp-dupe","gen_ai.usage.input_tokens":80,"gen_ai.usage.output_tokens":20},"_body":"GenAI inference: gpt-5.4-mini"}""",
            // chat span（最高优先级）：保留
            """{"type":"span","traceId":"trace-dupe","spanId":"chat-1","name":"chat gpt-5.4-mini","attributes":{"gen_ai.operation.name":"chat","gen_ai.response.model":"gpt-5.4-mini","gen_ai.conversation.id":"conv-dupe","gen_ai.response.id":"resp-dupe","gen_ai.usage.input_tokens":60,"gen_ai.usage.output_tokens":10}}""");

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle("chat span 保留，同 response 的 inference log 与 agent summary 被抑制");
        result.Records.Should().OnlyContain(record => record.InputTokens == 60 && record.OutputTokens == 10);
    }

    [Fact]
    public async Task Counts_a_re_exported_chat_span_once()
    {
        var home = CreateTempHome();
        WriteOtel(home, "copilot.jsonl",
            ChatSpan("trace-1", "span-1", 1_775_934_264, "resp-1", 100),
            ChatSpan("trace-1", "span-2", 1_775_934_300, "resp-2", 200),
            // CLI 会把部分调用以新 trace/span id 重导出（response id 与用量不变）：计一次，保留更早者。
            ChatSpan("trace-2", "span-3", 1_775_934_368, "resp-1", 100),
            ChatSpan("trace-1", "span-4", 1_775_934_370, "resp-2", 200),
            ChatSpan("trace-3", "span-5", 1_775_934_500, "resp-3", 300),
            ChatSpan("trace-1", "span-6", 1_775_934_400, "resp-3", 300));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(3, "resp-1 / resp-2 的重导出折叠，resp-3 保留更早的 span-6 导出");
        result.Records.Select(record => record.Timestamp).Should().BeEquivalentTo(new[]
        {
            new DateTimeOffset(2026, 4, 11, 19, 4, 24, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 11, 19, 5, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 11, 19, 6, 40, TimeSpan.Zero),
        });
    }

    [Fact]
    public async Task Keeps_calls_of_one_turn_that_share_a_response_id()
    {
        // VS Code 对一次 turn 的每次调用上报同一 response id：用量不同 → 各自保留（蓝本口径）。
        var home = CreateTempHome();
        WriteOtel(home, "copilot.jsonl",
            ChatSpan("trace-1", "span-1", 1_775_934_264, "turn-1", 1_000),
            ChatSpan("trace-1", "span-2", 1_775_934_270, "turn-1", 1_200),
            ChatSpan("trace-1", "span-3", 1_775_934_280, "turn-1", 1_400));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(3);
        result.Records.Select(record => record.InputTokens).Should().Equal(1_000, 1_200, 1_400);
    }

    [Fact]
    public async Task Does_not_double_count_reasoning_tokens()
    {
        // 无 total_tokens 属性：reasoning 不再额外计入（extra 恒 0），总量 = 四桶之和。
        var home = CreateTempHome();
        WriteOtel(home, "copilot.jsonl",
            """{"type":"span","traceId":"trace-1","spanId":"span-1","name":"chat test-model","endTime":[1775934264,0],"attributes":{"gen_ai.operation.name":"chat","gen_ai.response.model":"test-model","gen_ai.conversation.id":"conv-1","gen_ai.usage.input_tokens":100,"gen_ai.usage.output_tokens":50,"gen_ai.usage.cache_read.input_tokens":10,"gen_ai.usage.cache_creation.input_tokens":20,"gen_ai.usage.reasoning.output_tokens":5}}""");

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(70);
        record.OutputTokens.Should().Be(50);
        record.CacheCreationTokens.Should().Be(20);
        record.CacheReadTokens.Should().Be(10);
        record.TotalTokens.Should().Be(150, "reasoning 已含在桶内/无 total 属性时不双计（蓝本 totalTokens 150 口径）");
    }

    [Fact]
    public async Task Includes_separate_otel_reasoning_tokens_in_total_and_cost()
    {
        // total_tokens 显示 reasoning 另计：蓝本经 apply_total_token_fallback 产出 extra 分量并计入
        // 总量与计价；桌面统一记录无 extra 字段——分量在内部条目可观察、落记录时丢弃（卡片偏差清单）。
        var home = CreateTempHome();
        var line = """{"type":"span","traceId":"trace-1","spanId":"span-1","name":"chat test-model","endTime":[1775934264,0],"attributes":{"gen_ai.operation.name":"chat","gen_ai.response.model":"test-model","gen_ai.conversation.id":"conv-1","gen_ai.usage.input_tokens":100,"gen_ai.usage.output_tokens":50,"gen_ai.usage.cache_read.input_tokens":10,"gen_ai.usage.cache_creation.input_tokens":20,"gen_ai.usage.reasoning.output_tokens":5,"gen_ai.usage.total_tokens":155}}""";
        WriteOtel(home, "copilot.jsonl", line);

        var parser = new CopilotOtelParser(fallbackTimestamp: _ => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var parse = await parser.ParseAsync(new StringReader(line), Path.Combine(home, "otel", "copilot.jsonl"), CancellationToken.None);

        var entry = parse.Entries.Should().ContainSingle().Subject;
        entry.ExtraTotalTokens.Should().Be(5, "total 155 − 四桶 150 = extra 5（蓝本 fallback 口径）");
        entry.ReasoningOutputTokens.Should().Be(5);

        var result = await CreateAdapter(home).ScanAsync();
        result.Records.Should().ContainSingle().Which.TotalTokens.Should().Be(150,
            "桌面统一记录无 extra 字段，分量丢弃（fail-loud：真实数据出现此类行将由对账暴露）");
    }

    [Fact]
    public async Task Splits_otel_input_tokens_like_session_state()
    {
        // Copilot 把缓存读写计入两源的 input：同一次调用在 otel 与 session-state 记录同构的拆分。
        var home = CreateTempHome();
        WriteOtel(home, "copilot.jsonl",
            """{"type":"span","traceId":"trace-1","spanId":"span-1","name":"chat claude-haiku-4.5","endTime":[1775934264,0],"attributes":{"gen_ai.operation.name":"chat","gen_ai.response.model":"claude-haiku-4.5","gen_ai.conversation.id":"session-1","gen_ai.usage.input_tokens":20000,"gen_ai.usage.output_tokens":300,"gen_ai.usage.cache_read.input_tokens":12000,"gen_ai.usage.cache_write.input_tokens":7990}}""");
        WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-04-11T19:04:24.000Z",
                Model("claude-haiku-4.5", input: 20_000, output: 300, cacheRead: 12_000, cacheWrite: 7_990, requests: 1)));

        // 蓝本直测两个解析器（不经合并：otel 行与 shutdown 同刻，取优合并会把它抑制）。
        var otel = await new CopilotOtelParser().ParseAsync(
            new StringReader(File.ReadAllText(Path.Combine(home, "otel", "copilot.jsonl"))),
            Path.Combine(home, "otel", "copilot.jsonl"), CancellationToken.None);
        var sessionState = await ParseSessionState(
            Path.Combine(home, "session-state", "session-1", "events.jsonl"));

        (long Input, long Output, long CacheCreation, long CacheRead) Split(CopilotOtelEntry entry)
            => (entry.InputTokens, entry.OutputTokens, entry.CacheCreationTokens, entry.CacheReadTokens);

        otel.Entries.Should().ContainSingle();
        sessionState.Snapshots.Should().ContainSingle();
        Split(otel.Entries[0]).Should().Be((10, 300, 7_990, 12_000),
            "uncached = 20000 − (12000 cacheRead + 7990 cacheWrite)");
        Split(otel.Entries[0]).Should().Be(
            (sessionState.Snapshots[0].InputTokens, sessionState.Snapshots[0].OutputTokens,
                sessionState.Snapshots[0].CacheCreationTokens, sessionState.Snapshots[0].CacheReadTokens),
            "otel 与 session-state 对同一次调用的拆分同构");
    }

    [Fact]
    public async Task Falls_back_to_total_tokens_when_copilot_parts_are_missing()
    {
        var home = CreateTempHome();
        WriteOtel(home, "copilot.jsonl",
            """{"type":"span","traceId":"trace-1","spanId":"span-1","name":"chat test-model","endTime":[1775934264,0],"attributes":{"gen_ai.operation.name":"chat","gen_ai.response.model":"test-model","gen_ai.conversation.id":"conv-1","gen_ai.usage.total_tokens":567,"gen_ai.usage.reasoning_tokens":5}}""");

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(567, "四桶缺失时 total 兜底补 output（蓝本 fallback 口径）");
        record.InputTokens.Should().Be(0);
        record.TotalTokens.Should().Be(567);
    }

    // ---- otel 记录形态与容错角落（仓库口径显式化） ----

    [Fact]
    public async Task Counts_malformed_and_skipped_otel_lines_explicitly()
    {
        var home = CreateTempHome();
        WriteOtel(home, "copilot.jsonl",
            "[1,2]",
            """{"attributes":"not-an-object"}""",
            """{"type":"metric","name":"no-attributes-marker"}""",
            """{"attributes":{"gen_ai.operation.name":"chat"},"note":"unclassifiable"}""",
            """{"type":"span","traceId":"t","spanId":"s","name":"chat m","attributes":{"gen_ai.operation.name":"chat","gen_ai.conversation.id":"c","gen_ai.usage.input_tokens":0,"gen_ai.usage.output_tokens":0}}""",
            ChatSpan("trace-1", "span-1", 1_775_934_264, "resp-1", 10));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        var diagnostics = result.Diagnostics;
        diagnostics.MalformedLines.Should().Be(1, "attributes 非对象：serde 结构性失败计 malformed（顶层级非对象行同样，但本 fixture 的 [1,2] 无 attributes 字面量先被预过滤拦下）");
        diagnostics.LinesSkipped.Should().Be(4, "预过滤未命中（[1,2]） / 未归类四形态 / 总用量为 0 计 skipped");
        diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Resolves_otel_timestamps_across_exporter_shapes()
    {
        var home = CreateTempHome();
        WriteOtel(home, "copilot.jsonl",
            // [秒, 纳秒]（文件导出器）
            """{"type":"span","traceId":"t1","spanId":"s1","name":"chat m","endTime":[1775934264,500000000],"attributes":{"gen_ai.operation.name":"chat","gen_ai.conversation.id":"c1","gen_ai.usage.input_tokens":1,"gen_ai.usage.output_tokens":1}}""",
            // 标量毫秒（数量级猜测：≥1e11 为毫秒）
            """{"attributes":{"event.name":"gen_ai.client.inference.operation.details","gen_ai.conversation.id":"c2","gen_ai.usage.input_tokens":2,"gen_ai.usage.output_tokens":2},"timestamp":1775934264567,"_body":"GenAI inference: m"}""",
            // timeUnixNano（纳秒）
            """{"attributes":{"event.name":"copilot_chat.agent.turn","gen_ai.conversation.id":"c3","gen_ai.usage.input_tokens":3,"gen_ai.usage.output_tokens":3},"timeUnixNano":1775934264567890123}""");

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Select(record => record.Timestamp).Should().BeEquivalentTo(new[]
        {
            new DateTimeOffset(2026, 4, 11, 19, 4, 24, 500, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 11, 19, 4, 24, 567, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 11, 19, 4, 24, 567, TimeSpan.Zero),
        });
    }

    [Fact]
    public async Task Falls_back_to_file_mtime_for_otel_rows_without_timestamps()
    {
        var home = CreateTempHome();
        var fallback = new DateTimeOffset(2026, 5, 5, 5, 5, 5, TimeSpan.Zero);
        WriteOtel(home, "copilot.jsonl",
            """{"type":"span","traceId":"t1","spanId":"s1","name":"chat m","attributes":{"gen_ai.operation.name":"chat","gen_ai.conversation.id":"c","gen_ai.usage.input_tokens":1,"gen_ai.usage.output_tokens":1}}""");

        var adapter = new CopilotSessionAdapter(
            configuredHome: home,
            locator: HermeticLocator,
            otelParser: new CopilotOtelParser(fallbackTimestamp: _ => fallback));
        var result = await adapter.ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should().Be(fallback,
            "蓝本 file_modified_timestamp 口径（全部时间戳字段缺失时回退文件 mtime，测试注入固定值）");
    }

    // ---- otel × shutdown 取优合并（蓝本 loader.rs 内联测试同名场景；since/until 用例塌缩为全量口径） ----

    [Fact]
    public async Task Splits_cumulative_shutdowns_into_intervals_and_keeps_unmatched_otel_rows()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-04-15T09:52:27.352Z", Model("test-model", input: 10, output: 20)),
            Shutdown("shutdown-1", "2026-04-15T09:52:27.352Z", Model("test-model", input: 10, output: 20)),
            Shutdown("shutdown-2", "2026-04-15T09:53:27.352Z", Model("test-model", input: 30, output: 40)));
        WriteOtel(home, "otel.jsonl",
            OtelSpan("trace-duplicate", "span-duplicate", 1_776_246_780, "session-1", "test-model", 100, 200),
            OtelSpan("trace-post-shutdown", "span-post-shutdown", 1_776_246_840, "session-1", "test-model", 7, 8),
            OtelSpan("trace-other-model", "span-other-model", 1_775_934_264, "session-1", "other-model", 3, 4),
            OtelSpan("trace-other-session", "span-other-session", 1_775_934_264, "session-2", "test-model", 5, 6));

        var result = await CreateAdapter(home).ScanAsync();

        // 同 key 重复 shutdown 去重；差分区间（10/20、20/20）无请求数 → 不接管，区间照常；
        // 最新 shutdown（09:53:27）之后的 otel 行保留、之前的被抑制；其他模型 / 其他会话不受牵连。
        result.Records.Should().HaveCount(5);
        result.Records.Should().Contain(record => record.SessionId == "session-1" && record.Model == "test-model" && record.InputTokens == 10);
        result.Records.Should().Contain(record => record.SessionId == "session-1" && record.Model == "test-model" && record.InputTokens == 20);
        result.Records.Should().Contain(record => record.SessionId == "session-1" && record.Model == "test-model" && record.InputTokens == 7, "最新 shutdown 之后的 otel 行保留");
        result.Records.Should().Contain(record => record.SessionId == "session-1" && record.Model == "other-model" && record.InputTokens == 3, "其他模型行不受 (session, model) 接管影响");
        result.Records.Should().Contain(record => record.SessionId == "session-2" && record.InputTokens == 5, "其他会话行不受牵连");
        result.Records.Should().NotContain(record => record.InputTokens == 100, "最新 shutdown 之前的 otel 行被抑制");
    }

    [Fact]
    public async Task Suppresses_otel_rows_between_shutdowns_against_the_latest_shutdown()
    {
        // 蓝本 uses_shutdown_snapshot_as_of_until_for_otel_reconciliation 的桌面塌缩：无 --until 时
        // "最新可见 shutdown" = 最新 shutdown——两次 shutdown 之间的 otel 行被后者抑制（而非按前者）。
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-old", "2026-01-02T01:20:00.000Z",
                Model("test-model", input: 100, output: 50, cacheRead: 10, cacheWrite: 20)),
            Shutdown("shutdown-latest", "2026-01-03T01:20:00.000Z",
                Model("test-model", input: 200, output: 80, cacheRead: 20, cacheWrite: 30)));
        WriteOtel(home, "otel.jsonl",
            OtelSpan("trace-between", "span-between", 1_767_320_400, "session-1", "test-model", 11, 12));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(2, "两条 shutdown 区间；区间之间的 otel 行被最新 shutdown 抑制");
        result.Records.Should().Contain(record => record.InputTokens == 70 && record.OutputTokens == 50);
        result.Records.Should().Contain(record => record.InputTokens == 80 && record.OutputTokens == 30);
    }

    [Fact]
    public async Task Subtracts_the_pre_resume_shutdown_before_retaining_resumed_otel_rows()
    {
        // 蓝本 subtracts_the_pre_since_shutdown_before_retaining_resumed_otel_rows 的桌面塌缩：
        // 无 --since 时 shutdown 之前的区间照常出账（差分不变），最新 shutdown 之后的 otel 行保留。
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-old", "2026-01-02T01:20:00.000Z",
                Model("test-model", input: 100, output: 50, cacheRead: 10, cacheWrite: 20, requests: 1)),
            Shutdown("shutdown-latest", "2026-01-03T01:20:00.000Z",
                Model("test-model", input: 200, output: 80, cacheRead: 20, cacheWrite: 30, requests: 3)));
        WriteOtel(home, "otel.jsonl",
            OtelSpan("trace-resumed", "span-resumed", 1_767_406_800, "session-1", "test-model", 13, 14));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(3, "两条差分区间 + 最新 shutdown 之后保留的 otel 行");
        result.Records.Should().Contain(record => record.InputTokens == 70 && record.OutputTokens == 50
            && record.Timestamp == new DateTimeOffset(2026, 1, 2, 1, 20, 0, TimeSpan.Zero));
        result.Records.Should().Contain(record => record.InputTokens == 80 && record.OutputTokens == 30
            && record.CacheCreationTokens == 10 && record.CacheReadTokens == 10);
        result.Records.Should().Contain(record => record.InputTokens == 13 && record.OutputTokens == 14
            && record.Timestamp == new DateTimeOffset(2026, 1, 3, 2, 20, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Reports_otel_calls_of_a_process_that_ended_without_a_shutdown()
    {
        var home = CrashedProcessHome();

        var result = await CreateAdapter(home).ScanAsync();

        // 第一个进程两次调用后正常关闭；第二个进程三次调用后无 shutdown 终止；第三个进程恢复
        // 首 shutdown 分解、一次调用后以 3 个请求关闭——第二次进程的三次调用不存在于任何 shutdown。
        // otel 调用 7 > 请求数差分 3 → otel 从第一个有行的区间接管：区间全抑制、7 行全保留。
        result.Records.Select(record => (record.Timestamp, record.InputTokens)).Should().BeEquivalentTo(new[]
        {
            (new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero), 1L),
            (new DateTimeOffset(2026, 1, 2, 9, 30, 0, TimeSpan.Zero), 1L),
            (new DateTimeOffset(2026, 1, 2, 20, 0, 0, TimeSpan.Zero), 1L),
            (new DateTimeOffset(2026, 1, 2, 21, 0, 0, TimeSpan.Zero), 1L),
            (new DateTimeOffset(2026, 1, 3, 8, 0, 0, TimeSpan.Zero), 1L),
            (new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero), 1L),
            (new DateTimeOffset(2026, 1, 3, 11, 0, 0, TimeSpan.Zero), 1L),
        });
    }

    [Fact]
    public async Task Otel_reconciliation_comparison_covers_all_shutdowns()
    {
        // 蓝本 otel_reconciliation_does_not_depend_on_the_date_range 的桌面等价：接管比较覆盖全部
        // shutdown（与日期范围无关）——若无 01-03 的 shutdown 参与，01-02 区间（调用 2 == 请求 2）
        // 不会被接管；正是更晚的 shutdown（请求数差分 +1）触发接管。
        var home = CrashedProcessHome();

        var full = await CreateAdapter(home).ScanAsync();

        var eventsPath = Path.Combine(home, "session-state", "session-1", "events.jsonl");
        File.WriteAllText(eventsPath,
            ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00.000Z", 2) + "\n");
        var onlyEarlyShutdown = await CreateAdapter(home).ScanAsync();

        full.Records.Should().HaveCount(7, "全部 shutdown 参与：otel 接管，区间全抑制");
        onlyEarlyShutdown.Records.Should().Contain(record => record.InputTokens == 200
                && record.Timestamp == new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero),
            "仅早期 shutdown：otel 调用数不超过请求数差分 → 不接管、区间照常出账");
    }

    [Fact]
    public async Task Keeps_shutdown_usage_from_before_the_otel_export_started()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00.000Z", 2),
            ShutdownWithRequests("shutdown-2", "2026-01-03T10:00:00.000Z", 3));
        WriteOtel(home, "otel.jsonl",
            OtelCall("crashed-1", "2026-01-02T20:00:00.000Z"),
            OtelCall("crashed-2", "2026-01-02T21:00:00.000Z"),
            OtelCall("restored-1", "2026-01-03T09:00:00.000Z"));

        var result = await CreateAdapter(home).ScanAsync();

        // 首 shutdown 区间无 otel 行覆盖 → 照常出账；第二个区间起 otel 行数（3）> 请求数差分（1）→ 接管。
        result.Records.Should().HaveCount(4);
        result.Records.Should().Contain(record => record.Timestamp == new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero)
            && record.InputTokens == 200, "otel 导出开始前的 shutdown 用量保留");
        result.Records.Count(record => record.InputTokens == 1).Should().Be(3, "三个 otel 调用行全保留");
    }

    [Fact]
    public async Task Keeps_shutdown_intervals_when_otel_has_no_calls_they_miss()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00.000Z", 2),
            ShutdownWithRequests("shutdown-2", "2026-01-03T10:00:00.000Z", 3));
        WriteOtel(home, "otel.jsonl",
            OtelCall("first-1", "2026-01-02T09:00:00.000Z"),
            OtelCall("first-2", "2026-01-02T09:30:00.000Z"),
            OtelCall("second-1", "2026-01-03T09:00:00.000Z"),
            OtelCall("after-shutdown", "2026-01-03T11:00:00.000Z"));

        var result = await CreateAdapter(home).ScanAsync();

        // 首 shutdown 区间 otel 2 == 请求 2、累计 otel 3 == 累计请求差分 3 → 不接管：
        // 区间照常出账，最新 shutdown 之前的 otel 行被抑制、之后的保留。
        result.Records.Should().HaveCount(3);
        result.Records.Should().Contain(record => record.InputTokens == 200
            && record.Timestamp == new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero));
        result.Records.Should().Contain(record => record.InputTokens == 100
            && record.Timestamp == new DateTimeOffset(2026, 1, 3, 10, 0, 0, TimeSpan.Zero),
            "区间未被接管时照常出账");
        result.Records.Should().Contain(record => record.InputTokens == 1
            && record.Timestamp == new DateTimeOffset(2026, 1, 3, 11, 0, 0, TimeSpan.Zero),
            "最新 shutdown 之后的 otel 行保留");
    }

    [Fact]
    public async Task Does_not_double_count_calls_a_concurrent_process_shuts_down_with()
    {
        // 进程 A 在 shutdown-1 后两次调用；进程 B 恢复同一会话、零调用先关闭（沿用 shutdown-1 分解）；
        // A 的 shutdown 报告 A 的两次调用（请求数 3）。逐 shutdown 比较：otel 调用增量 1+2+0=3 vs
        // 请求数差分 1+0+2=3（first-1 同时计入两侧）→ 不接管，区间照常。
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00.000Z", 1),
            ShutdownWithRequests("shutdown-b", "2026-01-02T10:55:00.000Z", 1),
            ShutdownWithRequests("shutdown-a", "2026-01-02T11:00:00.000Z", 3));
        WriteOtel(home, "otel.jsonl",
            OtelCall("first-1", "2026-01-02T09:00:00.000Z"),
            OtelCall("a-1", "2026-01-02T10:40:00.000Z"),
            OtelCall("a-2", "2026-01-02T10:50:00.000Z"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records[0].InputTokens.Should().Be(100);
        result.Records[0].Timestamp.Should().Be(new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero));
        result.Records[1].InputTokens.Should().Be(200, "A 的两次调用由其 shutdown 区间出账（3 − 1 请求、300 − 100 input）");
        result.Records[1].Timestamp.Should().Be(new DateTimeOffset(2026, 1, 2, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Leaves_shutdowns_without_request_counts_to_the_shutdown_breakdown()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-01-02T10:00:00.000Z", Model("test-model", input: 100)));
        WriteOtel(home, "otel.jsonl",
            OtelCall("first-1", "2026-01-02T09:00:00.000Z"),
            OtelCall("first-2", "2026-01-02T09:30:00.000Z"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(100,
            "快照缺请求数时无法比较 → 不接管，区间留给 shutdown 分解、otel 行全被抑制");
    }

    // ---- credit gap（蓝本 loader.rs / parser.rs 内联测试同名场景；since/until 用例塌缩为全量口径） ----

    [Fact]
    public async Task Reports_session_credits_missing_from_model_metrics()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Checkpoint("checkpoint-1", "2026-01-02T10:00:00.000Z", 51),
            Checkpoint("checkpoint-2", "2026-01-02T10:10:00.000Z", 73),
            ShutdownWithCredits("shutdown-1", "2026-01-02T10:20:00.000Z", credits: 93, modelCredits: 42));

        var result = await CreateAdapter(home).ScanAsync();

        // checkpoint 早于最后 shutdown → 已含在其总额中；shutdown 时点未解释 51 credits → unknown $0.51。
        result.Records.Should().HaveCount(2);
        var interval = result.Records.Single(record => record.Model == "test-model");
        interval.InputTokens.Should().Be(100);
        interval.OutputTokens.Should().Be(50);
        var gap = result.Records.Single(record => record.Model == "unknown");
        gap.CostUsd.Should().Be(0.51m);
        gap.InputTokens.Should().Be(0);
        gap.OutputTokens.Should().Be(0);
        gap.Timestamp.Should().Be(new DateTimeOffset(2026, 1, 2, 10, 20, 0, TimeSpan.Zero));
        gap.SourceFile.Should().Contain("events.jsonl");
    }

    [Fact]
    public async Task Reports_checkpoint_credits_of_sessions_without_a_shutdown()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            ShutdownWithCredits("shutdown-1", "2026-01-02T10:00:00.000Z", credits: 10, modelCredits: 10),
            Checkpoint("checkpoint-1", "2026-01-02T11:00:00.000Z", 25),
            Checkpoint("checkpoint-2", "2026-01-03T11:00:00.000Z", 40));
        WriteEvents(home, "session-2",
            Checkpoint("checkpoint-3", "2026-01-03T12:00:00.000Z", 7));

        var result = await CreateAdapter(home).ScanAsync();

        // session-1：shutdown 后 checkpoint 的持续未解释部分逐快照成缺口（15 + 15 credits）；
        // session-2：checkpoint-only 会话全额出缺口（7 credits）。
        result.Records.Should().HaveCount(4);
        var gaps = result.Records.Where(record => record.Model == "unknown").OrderBy(record => record.Timestamp).ToList();
        gaps.Select(record => (record.SessionId, record.Timestamp, Cost: record.CostUsd!.Value)).Should().BeEquivalentTo(new[]
        {
            ("session-1", new DateTimeOffset(2026, 1, 2, 11, 0, 0, TimeSpan.Zero), 0.15m),
            ("session-1", new DateTimeOffset(2026, 1, 3, 11, 0, 0, TimeSpan.Zero), 0.15m),
            ("session-2", new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero), 0.07m),
        });
    }

    [Fact]
    public async Task Reports_resumed_session_credits_when_the_last_shutdown_has_no_model_metrics()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Checkpoint("checkpoint-1", "2026-01-02T10:00:00.000Z", 1_072),
            """{"type":"session.resume","timestamp":"2026-01-03T09:00:00.000Z","data":{}}""",
            """{"type":"session.shutdown","id":"shutdown-1","timestamp":"2026-01-03T10:00:00.000Z","data":{"totalNanoAiu":1072000000000,"modelMetrics":{}}}""");

        var result = await CreateAdapter(home).ScanAsync();

        // resume 后的 shutdown 只有会话总额（模型分解为空）：token 记录 0 条，全额 1072 credits 出缺口。
        var gap = result.Records.Should().ContainSingle().Subject;
        gap.Model.Should().Be("unknown");
        gap.CostUsd.Should().Be(10.72m);
        gap.Timestamp.Should().Be(new DateTimeOffset(2026, 1, 3, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Emits_each_snapshots_persistent_unexplained_share_in_full_range()
    {
        // 蓝本 subtracts_credit_snapshots_before_since 的桌面塌缩：无 --since 时每个快照"持续未解释"
        // 的份额全部出账（shutdown 时点 20 credits + checkpoint 时点 40 − 20 = 20 credits，总和守恒
        // 50 − 10 = 40 credits；蓝本带 since 时只出 01-03 的一段）。
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            ShutdownWithCredits("shutdown-1", "2026-01-02T10:00:00.000Z", credits: 30, modelCredits: 10),
            Checkpoint("checkpoint-1", "2026-01-03T10:00:00.000Z", 50));

        var result = await CreateAdapter(home).ScanAsync();

        var gaps = result.Records.Where(record => record.Model == "unknown").OrderBy(record => record.Timestamp).ToList();
        gaps.Select(record => record.CostUsd).Should().Equal(0.20m, 0.20m);
        gaps.Sum(record => record.CostUsd)!.Should().Be(0.40m, "缺口之和 = 会话总额 − 已归因模型积分（守恒）");
        result.Records.Should().Contain(record => record.Model == "test-model", "token 区间照常出账");
    }

    [Fact]
    public async Task Does_not_count_credits_that_a_later_shutdown_attributes()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Checkpoint("checkpoint-1", "2026-01-02T09:00:00.000Z", 30),
            ShutdownWithCredits("shutdown-1", "2026-01-02T10:00:00.000Z", credits: 30, modelCredits: 30),
            Checkpoint("checkpoint-2", "2026-01-02T11:00:00.000Z", 50),
            ShutdownWithCredits("shutdown-2", "2026-01-02T12:00:00.000Z", credits: 50, modelCredits: 35),
            ShutdownWithCredits("shutdown-3", "2026-01-02T13:00:00.000Z", credits: 50, modelCredits: 50));

        var result = await CreateAdapter(home).ScanAsync();

        // 每次 shutdown 逐步把 checkpoint 时的未解释部分归因到模型：最终全部解释 → 无缺口。
        // 无 --until 概念（蓝本 credit_gaps_do_not_depend_on_until 同口径塌缩）。
        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("test-model");
        record.InputTokens.Should().Be(100);
    }

    [Fact]
    public async Task Leaves_sessions_without_checkpoints_unchanged()
    {
        // 无 checkpoint 的会话来自重置双计数的旧版 Copilot：不做积分对账（即使 shutdown 间总额下降）。
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            ShutdownWithCredits("shutdown-1", "2026-01-02T10:00:00.000Z", credits: 60, modelCredits: 40),
            ShutdownWithCredits("shutdown-2", "2026-01-03T10:00:00.000Z", credits: 25, modelCredits: 25));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle().Which.Model.Should().Be("test-model");
    }

    [Fact]
    public async Task Leaves_sessions_with_incomplete_credit_totals_unchanged()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Checkpoint("checkpoint-1", "2026-01-02T09:00:00.000Z", 20),
            """{"type":"session.shutdown","id":"shutdown-1","timestamp":"2026-01-02T10:00:00.000Z","data":{"totalNanoAiu":93000000000,"modelMetrics":{"test-model":{"usage":{"inputTokens":100,"outputTokens":50}}}}}""",
            Checkpoint("checkpoint-2", "2026-01-02T11:00:00.000Z", 120));

        var result = await CreateAdapter(home).ScanAsync();

        // shutdown 的模型侧未上报积分（usage 存在但无 totalNanoAiu）→ 总额按缺失、会话豁免。
        result.Records.Should().ContainSingle().Which.Model.Should().Be("test-model");
    }

    [Fact]
    public async Task Leaves_credit_gaps_to_otel_when_a_session_has_otel_rows()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Checkpoint("checkpoint-1", "2026-01-02T10:00:00.000Z", 20));
        WriteOtel(home, "otel.jsonl",
            OtelSpan("trace-1", "span-1", 1_767_348_000, "session-1", "test-model", 11, 12));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("test-model");
        record.TotalTokens.Should().Be(23, "otel 行照常产出");
    }

    [Fact]
    public async Task Credit_gap_cost_is_logcost_authoritative()
    {
        // 蓝本 calculate_mode_does_not_price_credit_only_entries 的桌面塌缩：桌面无 calculate 模式，
        // credit gap 的内建成本（nanoAiu/1e11 decimal）即权威（定价引擎 LogCost 短路，不重算）。
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Checkpoint("checkpoint-1", "2026-01-02T10:00:00.000Z", 20));

        var result = await CreateAdapter(home).ScanAsync();
        var gap = result.Records.Should().ContainSingle().Subject;

        gap.Model.Should().Be("unknown");
        gap.CostUsd.Should().Be(0.20m, "2e9 nano-AIU / 1e11 = $0.20（decimal 精确，不经 double）");
        var priced = new PricingEngine().PriceRecord(gap);
        priced.Source.Should().Be(PricingSourceKind.LogCost);
        priced.CostUsd.Should().Be(0.20m);
    }

    // ---- 积分快照解析（蓝本 parser.rs session_state_tests 积分用例，直接测解析器） ----

    [Fact]
    public async Task Parses_credit_totals_from_checkpoints_and_shutdowns()
    {
        var home = CreateTempHome();
        var eventsFile = WriteEvents(home, "session-1",
            Checkpoint("checkpoint-1", "2026-04-15T09:50:00.000Z", 51.32283m),
            """{"type":"session.shutdown","id":"shutdown-1","timestamp":"2026-04-15T09:52:27.352Z","data":{"totalNanoAiu":93524970000.0,"modelMetrics":{"test-model":{"usage":{"inputTokens":100,"outputTokens":50},"totalNanoAiu":42202140000.0}}}}""",
            """{"type":"session.shutdown","id":"shutdown-2","timestamp":"2026-04-16T09:00:00.000Z","data":{"totalNanoAiu":93524970000,"modelMetrics":{}}}""");

        var parse = await ParseSessionState(eventsFile);

        parse.Snapshots.Should().ContainSingle().Which.NanoAiu.Should().Be(42_202_140_000,
            "模型侧 totalNanoAiu（浮点字面量）进快照条目");
        parse.CreditSnapshots.Select(snapshot => (snapshot.DedupKey, snapshot.IsShutdown, snapshot.TotalNanoAiu))
            .Should().BeEquivalentTo(new (string, bool, long?)[]
            {
                ("credits:session-1:checkpoint-1", false, 51_322_830_000),
                ("credits:session-1:shutdown-1", true, 93_524_970_000),
                ("credits:session-1:shutdown-2", true, 93_524_970_000),
            });
    }

    [Fact]
    public async Task Marks_shutdown_credits_unknown_when_a_total_is_missing()
    {
        var home = CreateTempHome();
        var eventsFile = WriteEvents(home, "session-1",
            """{"type":"session.shutdown","id":"without-model-total","timestamp":"2026-04-15T09:52:27.352Z","data":{"totalNanoAiu":93524970000,"modelMetrics":{"test-model":{"usage":{"inputTokens":100,"outputTokens":50}}}}}""",
            """{"type":"session.shutdown","id":"without-session-total","timestamp":"2026-04-16T09:52:27.352Z","data":{"modelMetrics":{}}}""");

        var parse = await ParseSessionState(eventsFile);

        parse.CreditSnapshots.Should().HaveCount(2);
        parse.CreditSnapshots[0].TotalNanoAiu.Should().BeNull("模型侧未上报积分 → shutdown 总额不可对账");
        parse.CreditSnapshots[1].TotalNanoAiu.Should().BeNull("会话缺 totalNanoAiu → 不可对账");
    }

    [Fact]
    public async Task Deduplicates_credit_snapshots_first_wins_per_event_id()
    {
        // 同事件 id 的积分快照按蓝本 seen first-wins 保留首个（与 token 快照的 latest-wins 相区别）。
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Checkpoint("cp-dup", "2026-04-15T09:50:00.000Z", 10),
            Checkpoint("cp-dup", "2026-04-15T09:55:00.000Z", 99));

        var result = await CreateAdapter(home).ScanAsync();

        // 同 id 重复的积分快照按蓝本 seen first-wins 保留首个：缺口按先到的 10 credits（09:50）出账，
        // 而非 latest-wins 的 99 credits（09:55）。
        var gap = result.Records.Should().ContainSingle().Subject;
        gap.Model.Should().Be("unknown");
        gap.CostUsd.Should().Be(0.10m);
        gap.Timestamp.Should().Be(new DateTimeOffset(2026, 4, 15, 9, 50, 0, TimeSpan.Zero));
    }

    // ---- 文件发现（蓝本 paths.rs 内联测试同名场景） ----

    [Fact]
    public void Discovers_session_state_and_otel_files_without_nested_session_files()
    {
        var home = CreateTempHome();
        var explicitFile = Path.Combine(CreateTempDir(), "explicit.jsonl");
        File.WriteAllText(explicitFile, "{}\n");
        Directory.CreateDirectory(Path.Combine(home, "otel", "nested"));
        File.WriteAllText(Path.Combine(home, "otel", "otel.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(home, "otel", "nested", "deep.jsonl"), "{}\n");
        Directory.CreateDirectory(Path.Combine(home, "otel", "not-jsonl"));
        File.WriteAllText(Path.Combine(home, "otel", "not-jsonl", "ignored.json"), "{}\n");
        WriteEvents(home, "session-a", "{}");
        File.WriteAllText(Path.Combine(home, "session-state", "session-a", "other.jsonl"), "{}\n");
        WriteEvents(home, "session-b", "{}");
        Directory.CreateDirectory(Path.Combine(home, "session-state", "nested", "session-c"));
        File.WriteAllText(Path.Combine(home, "session-state", "nested", "session-c", "events.jsonl"), "{}\n");

        // 环境经注入的 accessor 提供（生产接线的同一缝隙），不动进程环境：xunit 按类并行，
        // 进程级环境变量会泄入并行窗口内的其他 Copilot 扫描（B-004 间歇失败根因）。
        var files = new CopilotLogLocator(name
                => name == CopilotLogLocator.OtelExporterPathEnvVariable ? explicitFile : null)
            .CollectSourceFiles(home);

        files.Select(file => (file.Kind, file.Path)).Should().BeEquivalentTo(new[]
        {
            (CopilotSourceKind.Otel, explicitFile),
            (CopilotSourceKind.Otel, Path.Combine(home, "otel", "nested", "deep.jsonl")),
            (CopilotSourceKind.Otel, Path.Combine(home, "otel", "otel.jsonl")),
            (CopilotSourceKind.SessionState, Path.Combine(home, "session-state", "session-a", "events.jsonl")),
            (CopilotSourceKind.SessionState, Path.Combine(home, "session-state", "session-b", "events.jsonl")),
        }, options => options.WithoutStrictOrdering(), "otel/ 递归收集、非 jsonl 不收集、session-state 仅一级目录、按路径排序");
    }

    [Fact]
    public void Discovers_files_from_copilot_home_and_deduplicates_exporter_path()
    {
        var relocated = CreateTempHome();
        Directory.CreateDirectory(Path.Combine(relocated, "otel"));
        var shared = Path.Combine(relocated, "otel", "trace.jsonl");
        File.WriteAllText(shared, "{}\n");
        WriteEvents(relocated, "session-1", "{}");

        var files = new CopilotLogLocator(name => name switch
        {
            CopilotLogLocator.CopilotHomeEnvVariable => relocated,
            CopilotLogLocator.OtelExporterPathEnvVariable => shared,
            _ => null,
        }).CollectSourceFiles(configuredHome: null);

        files.Should().HaveCount(2, "COPILOT_HOME 重定位 + 导出变量与 otel/ 重复指向同文件去重");
        files.Should().OnlyContain(file => file.Path.StartsWith(relocated, StringComparison.Ordinal));
    }

    // ---- 增量扫描（适配器自有记账：变更 otel 文件整文件重解析、合并对池重算） ----

    [Fact]
    public async Task Incremental_rescan_picks_up_appended_otel_row_once()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-01-02T10:00:00.000Z", Model("m", input: 100, output: 10)));
        var otelPath = WriteOtel(home, "otel.jsonl", OtelCall("call-1", "2026-01-02T11:00:00.000Z"));
        var adapter = CreateAdapter(home);

        var first = await adapter.ScanAsync();
        File.AppendAllText(otelPath, OtelCall("call-2", "2026-01-02T12:00:00.000Z") + "\n");
        var second = await adapter.ScanAsync();
        var third = await adapter.ScanAsync();

        first.Records.Should().HaveCount(2, "shutdown 区间 + 最新 shutdown 后的 otel 行");
        second.Records.Should().HaveCount(3, "追加的 otel 行被拾取且不重复");
        second.Diagnostics.FilesRead.Should().Be(1, "只有变更文件被重解析");
        third.Records.Should().HaveCount(3, "重复扫描幂等（文件记账）");
        third.Diagnostics.FilesRead.Should().Be(0);
    }

    [Fact]
    public async Task Unreadable_otel_exporter_file_is_skipped_without_breaking_the_scan()
    {
        // I-023 独立审查 P1 回归：COPILOT_OTEL_FILE_EXPORTER_PATH 可指向任意路径（无读权限 / 被
        // 轮转）——打不开的文件计 LinesSkipped 且不落账（共享扫描器"不贡献记录"语义），池化以
        // 空 parse 参与，绝不以 KeyNotFoundException 冲出 ScanAsync 崩掉整次刷新。
        if (OperatingSystem.IsWindows())
        {
            // Unix 文件权限模型不可用（SetUnixFileMode 为 PlatformNotSupportedException）；仓库
            // 开发 / 验证环境为 macOS / Linux，Windows 下跳过断言。
            return;
        }

        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-01-02T10:00:00.000Z", Model("m", input: 100, output: 10)));
        WriteOtel(home, "otel.jsonl", OtelCall("call-1", "2026-01-02T11:00:00.000Z"));
        var lockedFile = Path.Combine(CreateTempDir(), "rotated-away.jsonl");
        File.WriteAllText(lockedFile, OtelCall("locked-1", "2026-01-02T12:00:00.000Z") + "\n");
        File.SetUnixFileMode(lockedFile, UnixFileMode.None);

        try
        {
            // 导出路径经注入 accessor 提供，不动进程环境（B-004：并行窗口内的跨类污染）。
            var result = await CreateAdapter(home, new CopilotLogLocator(name
                => name == CopilotLogLocator.OtelExporterPathEnvVariable ? lockedFile : null)).ScanAsync();

            // 其余数据正常出账：shutdown 区间 + otel/otel.jsonl 行（otel_after=10:00 之后保留）。
            result.Records.Should().HaveCount(2);
            result.Records.Should().Contain(record => record.SessionId == "session-1" && record.InputTokens == 100);
            result.Records.Should().Contain(record => record.InputTokens == 1
                && record.Timestamp == new DateTimeOffset(2026, 1, 2, 11, 0, 0, TimeSpan.Zero));
            result.Diagnostics.LinesSkipped.Should().Be(1, "打不开的导出文件计 LinesSkipped、不计 FilesRead");
            result.Diagnostics.FilesRead.Should().Be(2, "仅 session-state 与 otel 目录两个可读文件被解析");
            result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok, "读失败不是解析错误，不降级 Partial");

            // 失败条件持续存在时重复扫描：同样不崩溃、不重复计数（审查员指出的持续失败场景）。
            var second = await CreateAdapter(home, new CopilotLogLocator(name
                => name == CopilotLogLocator.OtelExporterPathEnvVariable ? lockedFile : null)).ScanAsync();
            second.Records.Should().HaveCount(2);
            second.Diagnostics.LinesSkipped.Should().Be(1);
        }
        finally
        {
            File.SetUnixFileMode(lockedFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ---- helpers ----

    /// <summary>
    /// 全类扫描不读进程环境（xunit 按类并行 + dotnet test 继承 shell 环境，任何泄漏进来的
    /// Copilot 环境变量都会击穿默认定位器的导出路径分支，B-004）；环境语义由文件发现专用用例
    /// 经显式注入的 accessor 覆盖。
    /// </summary>
    private static readonly CopilotLogLocator HermeticLocator = new(_ => null);

    private CopilotSessionAdapter CreateAdapter(string home, CopilotLogLocator? locator = null)
        => new(configuredHome: home, locator: locator ?? HermeticLocator);

    private static async Task<CopilotSessionFileParse> ParseSessionState(string eventsFile)
    {
        var parser = new CopilotSessionParser();
        return await parser.ParseAsync(new StringReader(File.ReadAllText(eventsFile)), eventsFile, CancellationToken.None);
    }

    private static string ChatSpan(string trace, string span, long endSeconds, string responseId, long input, long output = 10)
        => $$"""{"type":"span","traceId":"{{trace}}","spanId":"{{span}}","name":"chat test-model","endTime":[{{endSeconds}},0],"attributes":{"gen_ai.operation.name":"chat","gen_ai.response.model":"test-model","gen_ai.conversation.id":"conv-1","gen_ai.response.id":"{{responseId}}","gen_ai.usage.input_tokens":{{input}},"gen_ai.usage.output_tokens":{{output}} } }""";

    private static string OtelSpan(string trace, string span, long endSeconds, string conversationId, string model, long input, long output)
        => $$"""{"type":"span","traceId":"{{trace}}","spanId":"{{span}}","name":"chat {{model}}","endTime":[{{endSeconds}},0],"attributes":{"gen_ai.operation.name":"chat","gen_ai.response.model":"{{model}}","gen_ai.conversation.id":"{{conversationId}}","gen_ai.usage.input_tokens":{{input}},"gen_ai.usage.output_tokens":{{output}} } }""";

    /// <summary>单 token 的 otel 调用（蓝本 otel_call：秒级 endTime，input/output 各 1）。</summary>
    private static string OtelCall(string span, string timestamp)
    {
        var parsed = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var seconds = parsed.ToUnixTimeSeconds();
        return $$"""{"type":"span","traceId":"trace-1","spanId":"{{span}}","name":"chat test-model","endTime":[{{seconds}},0],"attributes":{"gen_ai.operation.name":"chat","gen_ai.response.model":"test-model","gen_ai.conversation.id":"session-1","gen_ai.usage.input_tokens":1,"gen_ai.usage.output_tokens":1 } }""";
    }

    private static string Shutdown(string id, string timestamp, params string[] models)
        => $$"""{"type":"session.shutdown","id":"{{id}}","timestamp":"{{timestamp}}","data":{"modelMetrics":{ {{string.Join(",", models)}} } } }""";

    private static string Model(string name, long input = 0, long output = 0, long cacheRead = 0,
        long cacheWrite = 0, long requests = 0)
        => $$""" "{{name}}":{"usage":{"inputTokens":{{input}},"outputTokens":{{output}},"cacheReadTokens":{{cacheRead}},"cacheWriteTokens":{{cacheWrite}}},"requests":{"count":{{requests}} } }""";

    private static string ShutdownWithRequests(string id, string timestamp, long requests)
        => Shutdown(id, timestamp, Model("test-model", input: requests * 100, output: requests * 10, requests: requests));

    private static string Checkpoint(string id, string timestamp, decimal credits)
        => FormattableString.Invariant(
            $$"""{"type":"session.usage_checkpoint","id":"{{id}}","timestamp":"{{timestamp}}","data":{"totalNanoAiu":{{credits * NanoAiuPerCredit:0}} } }""");

    private static string ShutdownWithCredits(string id, string timestamp, decimal credits, decimal? modelCredits)
    {
        var modelJson = modelCredits is { } model
            ? FormattableString.Invariant($",\"totalNanoAiu\":{model * NanoAiuPerCredit:0}")
            : string.Empty;
        return FormattableString.Invariant(
            $$"""{"type":"session.shutdown","id":"{{id}}","timestamp":"{{timestamp}}","data":{"totalNanoAiu":{{credits * NanoAiuPerCredit:0}},"modelMetrics":{"test-model":{"usage":{"inputTokens":100,"outputTokens":50}{{modelJson}} } } } }""");
    }

    /// <summary>蓝本 crashed_process_fixture：两 shutdown（请求 2 / 3）+ 七个 otel 调用。</summary>
    private string CrashedProcessHome()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            ShutdownWithRequests("shutdown-1", "2026-01-02T10:00:00.000Z", 2),
            ShutdownWithRequests("shutdown-2", "2026-01-03T10:00:00.000Z", 3));
        WriteOtel(home, "otel.jsonl",
            OtelCall("first-1", "2026-01-02T09:00:00.000Z"),
            OtelCall("first-2", "2026-01-02T09:30:00.000Z"),
            OtelCall("crashed-1", "2026-01-02T20:00:00.000Z"),
            OtelCall("crashed-2", "2026-01-02T21:00:00.000Z"),
            OtelCall("crashed-3", "2026-01-03T08:00:00.000Z"),
            OtelCall("restored-1", "2026-01-03T09:00:00.000Z"),
            OtelCall("after-shutdown", "2026-01-03T11:00:00.000Z"));
        return home;
    }

    private string CreateTempHome()
    {
        var home = Path.Combine(CreateTempDir(), "copilot");
        Directory.CreateDirectory(Path.Combine(home, "session-state"));
        Directory.CreateDirectory(Path.Combine(home, "otel"));
        return home;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-copilot-otel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    /// <summary>写入一个会话目录的 events.jsonl，返回文件路径。</summary>
    private static string WriteEvents(string copilotHome, string sessionName, params string[] lines)
    {
        var sessionDirectory = Path.Combine(copilotHome, "session-state", sessionName);
        Directory.CreateDirectory(sessionDirectory);
        var path = Path.Combine(sessionDirectory, "events.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static string WriteOtel(string copilotHome, string fileName, params string[] lines)
    {
        var path = Path.Combine(copilotHome, "otel", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论。
            }
        }
    }
}
