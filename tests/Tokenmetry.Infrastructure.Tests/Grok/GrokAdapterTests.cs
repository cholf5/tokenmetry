namespace Tokenmetry.Infrastructure.Tests.Grok;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Grok;

/// <summary>
/// I-022: Grok Build CLI 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage rust/adapters/grok 的 parser.rs / loader.rs / paths.rs，含蓝本内联测试的同名场景：
/// token 三段拆分、reasoning 丢弃、ticks 成本、modelUsage 拆行与回退、时间戳优先级、URL 解码、
/// 去重双层、subagent-artifacts 无关（蓝本无此概念）、路径发现）。
/// </summary>
public class GrokAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- 蓝本 parser.rs 内联测试同名场景 ----

    [Fact]
    public void Splits_uncached_input_from_cache()
    {
        GrokLogParser.SplitInputTokens(100, 40, 0).Should().Be((60, 40, 0));
        GrokLogParser.SplitInputTokens(10, 40, 0).Should().Be((0, 10, 0), "cachedRead 大于 input 时钳到 input");
        GrokLogParser.SplitInputTokens(0, 5, 0).Should().Be((0, 0, 0));
    }

    [Fact]
    public void Splits_cache_creation_out_of_the_uncached_input()
    {
        GrokLogParser.SplitInputTokens(100, 40, 25).Should().Be((35, 40, 25));
        GrokLogParser.SplitInputTokens(100, 40, 0).Should().Be((60, 40, 0));
        GrokLogParser.SplitInputTokens(100, 40, 999).Should().Be((0, 40, 60), "cache-write 大于剩余 input 时钳住而非回绕");
    }

    [Fact]
    public async Task Turn_completed_model_usage_maps_tokens_without_double_count()
    {
        // 蓝本 turn_completed_model_usage_maps_tokens_without_double_count：inputTokens 含缓存需扣除、
        // reasoning 已含在 output 不加回（extra 恒 0）、summary cwd 覆盖项目路径。
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            TurnLine("evt-1", "grok-4.5-build", 100, 20, 40, 10, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0),
            new Summary("""{"info":{"id":"sess-1","cwd":"D:\\work\\proj"},"current_model_id":"grok-4.5"}"""));

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("grok-4.5-build", "展示 modelUsage 原始键");
        record.InputTokens.Should().Be(60);
        record.CacheReadTokens.Should().Be(40);
        record.CacheCreationTokens.Should().Be(0);
        record.OutputTokens.Should().Be(20);
        record.CostUsd.Should().BeNull("无 costUsdTicks → 交定价引擎");
        record.SessionId.Should().Be("sess-1");
        record.ProjectPath.Should().Be("D:\\work\\proj");
    }

    [Fact]
    public async Task Does_not_add_reasoning_tokens_to_the_total()
    {
        // 蓝本 does_not_add_reasoning_tokens_to_the_total：reasoning 42 单独成行、token 全零。
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            TurnLine("evt-r", "grok-4.5-build", 0, 0, 0, 42, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(0);
        record.OutputTokens.Should().Be(0);
    }

    [Fact]
    public async Task Falls_back_to_top_level_usage_when_model_usage_is_absent()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-top",
            """{"timestamp":1750000000,"params":{"sessionId":"sess-top","update":{"sessionUpdate":"turn_completed","usage":{"inputTokens":50,"outputTokens":5,"cachedReadTokens":10,"reasoningTokens":2}},"_meta":{"eventId":"evt-top"}}}""",
            new Summary("""{"info":{"id":"sess-top","cwd":"/tmp/proj"},"current_model_id":"grok-4.5-build"}"""));

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("grok-4.5-build", "summary current_model_id 作回退模型");
        record.InputTokens.Should().Be(40, "顶层 usage 同样做缓存扣除");
        record.CacheReadTokens.Should().Be(10);
        record.OutputTokens.Should().Be(5);
    }

    [Fact]
    public async Task Names_top_level_usage_unknown_when_summary_has_no_default_model()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-u",
            """{"timestamp":1750000000,"params":{"sessionId":"sess-u","update":{"sessionUpdate":"turn_completed","usage":{"inputTokens":10,"outputTokens":1,"cachedReadTokens":0,"reasoningTokens":0}},"_meta":{"eventId":"evt-u"}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Model.Should().Be("unknown");
    }

    [Fact]
    public async Task Prefers_agent_timestamp_ms_over_envelope_seconds()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            TurnLine("evt-ts", "grok-4.5-build", 10, 1, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: 1_785_328_986_355, ticks: 0));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should()
            .Be(DateTimeOffset.UnixEpoch.AddMilliseconds(1_785_328_986_355), "agentTimestampMs 毫秒优先");
    }

    [Fact]
    public async Task Converts_envelope_unix_seconds_to_millis_when_agent_ms_is_absent()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            TurnLine("evt-sec", "grok-4.5-build", 10, 1, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should()
            .Be(DateTimeOffset.UnixEpoch.AddSeconds(1_750_000_000));
    }

    [Fact]
    public async Task Uses_unix_epoch_when_no_timestamp_fields_are_present()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-e",
            """{"params":{"sessionId":"sess-e","update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4.5-build":{"inputTokens":1,"outputTokens":1,"cachedReadTokens":0,"reasoningTokens":0}}}},"_meta":{"eventId":"evt-e"}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should().Be(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Summary_cwd_overrides_path_derived_project_and_fills_session_when_line_omits_it()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            """{"timestamp":1750000000,"params":{"update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4.5-build":{"inputTokens":10,"outputTokens":1,"cachedReadTokens":0,"reasoningTokens":0}}}},"_meta":{"eventId":"evt-meta"}}}""",
            new Summary("""{"info":{"id":"canonical-session","cwd":"D:\\canonical\\cwd"},"git_root_dir":"should-not-win"}"""));

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SessionId.Should().Be("canonical-session", "行内无 sessionId 时回退 summary id");
        record.ProjectPath.Should().Be("D:\\canonical\\cwd", "info.cwd 优先于 git_root_dir");
    }

    [Fact]
    public async Task Line_session_id_beats_summary_id()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            TurnLine("evt-1", "grok-4.5-build", 100, 20, 40, 10, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0),
            new Summary("""{"info":{"id":"canonical-session","cwd":"D:\\canonical\\cwd"}}"""));

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SessionId.Should().Be("sess-1", "行内 params.sessionId 优先");
        record.ProjectPath.Should().Be("D:\\canonical\\cwd");
    }

    [Fact]
    public async Task Url_decodes_project_segment_when_summary_is_absent()
    {
        var root = CreateGrokHome();
        WriteSession(root, "D%3A%5Cwork%5Cproj/019fa1b1-0000-7000-8000-000000000001",
            """{"timestamp":1750000000,"params":{"update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4.5-build":{"inputTokens":10,"outputTokens":1,"cachedReadTokens":0,"reasoningTokens":0}}}},"_meta":{"eventId":"evt-url"}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SessionId.Should().Be("019fa1b1-0000-7000-8000-000000000001", "无 summary 时会话目录名为 id");
        record.ProjectPath.Should().Be("D:\\work\\proj", "URL 编码的 cwd 目录名轻量解码");
    }

    [Fact]
    public void Url_decode_recombines_wide_scalars_and_tolerates_invalid_bytes()
    {
        GrokLogParser.UrlDecodeLightweight("%2Ftmp%2F%E6%97%A5%E6%9C%AC").Should().Be("/tmp/日本",
            "三个三元组还原一个 UTF-8 标量");
        GrokLogParser.UrlDecodeLightweight("D%3A%5Cwork%5Cproj").Should().Be("D:\\work\\proj");
        GrokLogParser.UrlDecodeLightweight("%2Ftmp%2F%FFx").Should().Be("/tmp/\uFFFDx",
            "非法 UTF-8 字节降级为替换字符（不截断后续路径）");
    }

    [Fact]
    public async Task Url_decodes_multi_byte_project_segment()
    {
        var root = CreateGrokHome();
        WriteSession(root, "%2Fhome%2F%C3%A9projet/019fa1b1-0000-7000-8000-000000000002",
            """{"timestamp":1750000000,"params":{"update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4.5-build":{"inputTokens":10,"outputTokens":1,"cachedReadTokens":0,"reasoningTokens":0}}}},"_meta":{"eventId":"evt-utf8"}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.ProjectPath.Should().Be("/home/éprojet",
            "%C3%A9 为跨两个三元组的单个码点");
    }

    [Fact]
    public async Task Skips_turn_completed_without_usage_and_zero_rows()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            """{"timestamp":1750000001,"params":{"update":{"sessionUpdate":"tool_call"}}}""",
            """{"timestamp":1750000002,"params":{"update":{"sessionUpdate":"turn_completed"},"_meta":{"eventId":"no-usage"}}}""",
            """{"timestamp":1750000003,"params":{"update":{"sessionUpdate":"turn_completed","usage":{"inputTokens":0,"outputTokens":0,"cachedReadTokens":0,"reasoningTokens":0,"modelUsage":{"grok-4.5":{"inputTokens":0,"outputTokens":0,"cachedReadTokens":0,"reasoningTokens":0}}}},"_meta":{"eventId":"zero"}}}""",
            """{"this is not json""",
            TurnLine("evt-1", "grok-4.5-build", 100, 20, 40, 10, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(60);
    }

    [Fact]
    public async Task Multi_model_turn_emits_one_entry_per_model()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-m",
            """{"timestamp":1750000100,"params":{"sessionId":"sess-m","update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"model-b":{"inputTokens":20,"outputTokens":4,"cachedReadTokens":5,"reasoningTokens":0},"model-a":{"inputTokens":10,"outputTokens":2,"cachedReadTokens":0,"reasoningTokens":1}}}},"_meta":{"eventId":"evt-multi"}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records[0].Model.Should().Be("model-a", "modelUsage 键 Ordinal 排序");
        result.Records[0].InputTokens.Should().Be(10);
        result.Records[1].Model.Should().Be("model-b");
        result.Records[1].InputTokens.Should().Be(15, "20 − cachedRead 5");
        result.Records[1].CacheReadTokens.Should().Be(5);
    }

    [Fact]
    public async Task Dedupes_same_event_id_and_model()
    {
        var root = CreateGrokHome();
        var line = TurnLine("evt-1", "grok-4.5-build", 100, 20, 40, 10, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0);
        WriteSession(root, "proj/sess-1", line, line);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.LinesSkipped.Should().Be(1, "文件内同 eventId|模型 去重折叠行显式计数");
        result.Diagnostics.DuplicateRecords.Should().Be(0, "折叠发生在解析层（全局钩子无重复可折叠）");
    }

    [Fact]
    public async Task Dedupes_identical_rows_without_event_id_by_content_key()
    {
        var root = CreateGrokHome();
        var line = """{"timestamp":1750000000,"params":{"sessionId":"sess-d","update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4.5-build":{"inputTokens":100,"outputTokens":20,"cachedReadTokens":40,"reasoningTokens":10}}}}}}""";
        WriteSession(root, "proj/sess-d", line, line);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle("无 eventId 的内容键（含 reasoning）文件内 first-wins");
    }

    [Fact]
    public async Task Keeps_rows_without_event_id_that_differ_only_in_cache_creation()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-cc",
            """{"timestamp":1750000000,"params":{"sessionId":"sess-cc","update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4.5-build":{"inputTokens":100,"outputTokens":5,"cachedReadTokens":0,"cacheCreationTokens":20,"reasoningTokens":0}}}}}}""",
            """{"timestamp":1750000000,"params":{"sessionId":"sess-cc","update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4.5-build":{"inputTokens":80,"outputTokens":5,"cachedReadTokens":0,"reasoningTokens":0}}}}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "cache_creation 是内容键分量");
        result.Records[0].CacheCreationTokens.Should().Be(20);
        result.Records[1].CacheCreationTokens.Should().Be(0);
    }

    [Fact]
    public async Task Keeps_distinct_models_that_share_an_event_id()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-m",
            """{"timestamp":1750000100,"params":{"sessionId":"sess-m","update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"model-a":{"inputTokens":10,"outputTokens":1,"cachedReadTokens":0,"reasoningTokens":0},"model-b":{"inputTokens":20,"outputTokens":2,"cachedReadTokens":0,"reasoningTokens":0}}}},"_meta":{"eventId":"evt-shared"}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "全局去重键含模型分量");
    }

    [Fact]
    public async Task Reads_the_recorded_cost_usd_ticks()
    {
        // 蓝本 reads_the_recorded_cost_usd_ticks：185_192_000 ticks = $0.0185192（xAI 牌价）。
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            """{"timestamp":1750000000,"params":{"sessionId":"sess-1","update":{"sessionUpdate":"turn_completed","usage":{"inputTokens":18444,"outputTokens":130,"cachedReadTokens":11264,"reasoningTokens":73,"costUsdTicks":185192000,"modelUsage":{"grok-4.5-build":{"inputTokens":18444,"outputTokens":130,"cachedReadTokens":11264,"reasoningTokens":73,"costUsdTicks":185192000}}}},"_meta":{"eventId":"evt-cost"}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.CostUsd.Should().Be(0.0185192m, "ticks / 1e10 用 decimal 精确换算（不经 double）");
    }

    [Fact]
    public async Task Structural_shape_failures_are_counted_as_malformed()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            """{"timestamp":1750000000,"params":{"sessionId":"s","update":{"sessionUpdate":"turn_completed","usage":"scalar","modelUsage":{}},"_meta":{"eventId":"e1"}}}""",
            """{"timestamp":1750000000,"params":{"sessionId":"s","update":"not-object"}}""",
            """{"timestamp":1750000000,"params":{"sessionId":"s","update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":"not-object"}},"_meta":{"eventId":"e2"}}}""",
            """{"timestamp":1750000000,"params":{"sessionId":"s","update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4.5":"scalar-value"}}},"_meta":{"eventId":"e3"}}}""",
            """["top-level-array"]""",
            TurnLine("evt-ok", "grok-4.5-build", 5, 1, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.MalformedLines.Should().Be(3, "usage 标量 + modelUsage 标量 + modelUsage 值标量（serde 同拒绝面）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    // ---- 蓝本 loader.rs 内联测试同名场景 ----

    [Fact]
    public async Task Loads_session_tree_with_uncached_split()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-load",
            TurnLine("evt-load", "grok-4.5-build", 100, 20, 40, 10, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0),
            new Summary("""{"info":{"id":"sess-load","cwd":"/tmp/proj"}}"""));

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(1_750_000_000), "2025-06-15（UTC）");
        record.InputTokens.Should().Be(60);
        record.CacheReadTokens.Should().Be(40);
        record.OutputTokens.Should().Be(20);
        record.Model.Should().Be("grok-4.5-build");
        record.ProjectPath.Should().Be("/tmp/proj");
        record.MessageId.Should().Be("evt-load", "eventId 入 MessageId（全局去重键）");
        record.RequestId.Should().Be("evt-load");
    }

    [Fact]
    public async Task Dedupes_the_same_event_across_session_files()
    {
        var root = CreateGrokHome();
        var sharedEvent = TurnLine("evt-shared", "grok-4.5-build", 100, 20, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0);
        WriteSession(root, "proj/sess-a", sharedEvent);
        WriteSession(root, "proj/sess-b", sharedEvent,
            TurnLine("evt-other", "grok-4.5-build", 50, 5, 0, 0, envelopeSeconds: 1_750_000_100, agentMs: null, ticks: 0));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "同一服务端事件落入多个会话导出只计一次");
        result.Records.Sum(record => record.InputTokens).Should().Be(150);
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    [Fact]
    public async Task Keeps_usage_from_a_readable_session_when_a_sibling_file_is_corrupt()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-good",
            TurnLine("evt-good", "grok-4.5-build", 80, 8, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));
        WriteSession(root, "proj/sess-bad", "\0\0\0not-jsonl", "{broken");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(80,
            "一个损坏文件不能拖垮整个 home（坏行无预过滤标记 → 计 LinesSkipped）");
        result.Diagnostics.LinesSkipped.Should().Be(2);
    }

    [Fact]
    public async Task Keeps_event_id_less_turns_that_differ_only_in_reasoning()
    {
        var root = CreateGrokHome();
        var baseLine = """{"timestamp":1750000000,"params":{"sessionId":"sess-nr","update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4.5-build":{"inputTokens":100,"outputTokens":20,"cachedReadTokens":40,"reasoningTokens":REASONING}}}}}}""";
        WriteSession(root, "proj/sess-nr",
            baseLine.Replace("REASONING", "5"),
            baseLine.Replace("REASONING", "10"));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "reasoning 不入统一记录，内容键在解析层比较（含 reasoning 分量）");
        result.Records.Sum(record => record.InputTokens).Should().Be(120);
    }

    [Fact]
    public async Task Sorts_entries_by_timestamp_across_sessions()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-late",
            TurnLine("evt-late", "grok-4.5-build", 20, 2, 0, 0, envelopeSeconds: 1_750_100_000, agentMs: null, ticks: 0));
        WriteSession(root, "proj/sess-early",
            TurnLine("evt-early", "grok-4.5-build", 10, 1, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Select(record => record.MessageId).Should().ContainInOrder("evt-early", "evt-late");
    }

    // ---- 目录发现与环境变量（蓝本 paths.rs 语义） ----

    [Fact]
    public async Task Discovers_updates_jsonl_under_sessions_only()
    {
        var root = CreateGrokHome();
        WriteSession(root, "C%3A%5Cproj/019fa1b1-0000-7000-8000-000000000001",
            TurnLine("evt-1", "grok-4.5-build", 1, 1, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0),
            ("summary.json", "{}"), ("events.jsonl", "{}"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        File.WriteAllText(Path.Combine(root, "logs", "unified.jsonl"), "{}");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.FilesDiscovered.Should().Be(1, "events.jsonl 与 logs/unified.jsonl 不发现");
        GrokLogLocator.SiblingSummary(
            Path.Combine(root, "sessions", "C%3A%5Cproj", "019fa1b1-0000-7000-8000-000000000001", "updates.jsonl"))
            .Should().NotBeNull("兄弟 summary.json 被携带");
    }

    [Fact]
    public async Task Discovers_nested_session_trees_sorted_across_projects()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj-b/sess-2",
            TurnLine("evt-b", "m", 2, 1, 0, 0, envelopeSeconds: 1_750_000_100, agentMs: null, ticks: 0),
            new Summary("{}"));
        WriteSession(root, "proj-a/sess-1",
            TurnLine("evt-a", "m", 1, 1, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records[0].MessageId.Should().Be("evt-a", "发现按路径排序，加载序跨运行稳定");
    }

    [Fact]
    public async Task Grok_home_is_used_without_default_fallback()
    {
        var relocated = CreateGrokHome();
        WriteSession(relocated, "proj/session-a",
            TurnLine("evt-1", "grok-4.5-build", 5, 1, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));
        var defaultHome = CreateGrokHome();
        WriteSession(defaultHome, "proj/decoy",
            TurnLine("evt-decoy", "m", 9, 1, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));

        // GROK_HOME 经注入的 accessor 提供（定位器读环境的生产同一缝隙），不动进程环境（A-3，
        // 同 B-004 Copilot 处理）：xunit 并行窗口内进程级环境变量会与其他用例互踩。
        var result = await new GrokAdapter(locator: new GrokLogLocator(
            name => name == GrokLogLocator.GrokHomeEnvVariable ? relocated : null)).ScanAsync();

        result.Records.Should().ContainSingle().Which.MessageId.Should().Be("evt-1");
        result.Roots.Should().ContainSingle().Which.Should().Be(relocated);
    }

    [Fact]
    public async Task Empty_grok_home_falls_back_to_default_home()
    {
        // 蓝本 empty_grok_home_falls_back_to_default_home：GROK_HOME 空白 → 回退 ~/.grok。
        var fakeHome = CreateTempDir();
        var defaultRoot = Path.Combine(fakeHome, ".grok");
        WriteSession(defaultRoot, "home/session-a",
            TurnLine("evt-1", "grok-4.5-build", 5, 1, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));

        // 环境语义经注入 accessor 覆盖（A-3）：GROK_HOME 空白 + USERPROFILE/HOME 指向 fakeHome。
        // 此前直接改进程环境且 finally 置 null 不还原原值——对并行/后续用例是永久污染
        // （USERPROFILE/HOME 从此消失，覆盖面最宽，见 B-004 卡的全家族备注）。
        var result = await new GrokAdapter(locator: new GrokLogLocator(name =>
            name == GrokLogLocator.GrokHomeEnvVariable ? "  "
            : name is "USERPROFILE" or "HOME" ? fakeHome
            : null)).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].SourceFile.Should().Contain("session-a", "回退默认目录 ~/.grok/sessions");
    }

    [Fact]
    public async Task Missing_or_non_directory_grok_home_yields_no_data()
    {
        var missing = Path.Combine(CreateTempDir(), "does-not-exist");

        var result = await CreateAdapter(missing).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);

        var fileHome = Path.Combine(CreateTempDir(), "not-a-dir");
        File.WriteAllText(fileHome, "file");
        var fileResult = await CreateAdapter(fileHome).ScanAsync();

        fileResult.Records.Should().BeEmpty();
    }

    // ---- helpers ----

    private GrokAdapter CreateAdapter(string grokHome) => new(configuredDataDir: grokHome);

    private string CreateGrokHome()
    {
        var path = Path.Combine(CreateTempDir(), "grok-home");
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-grok-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    /// <summary>summary.json 内容标记（WriteSession 的 object 参数分支）。</summary>
    private sealed record Summary(string Content);

    /// <summary>
    /// 蓝本 pricing_candidates_strip_build_and_add_xai 移植锚点（I-024 接线 GK-1/GK-2）：
    /// [grok] 前缀剥离、-build 后缀剥离、xai/ 与 x-ai/ 前缀补全、去重保序。
    /// </summary>
    [Fact]
    public void Builds_pricing_candidates_with_build_strip_and_xai_prefixes()
    {
        GrokLogParser.BuildPricingCandidates("grok-4.5-build").Should().Equal(
            "grok-4.5-build",
            "xai/grok-4.5-build",
            "x-ai/grok-4.5-build",
            "grok-4.5",
            "xai/grok-4.5",
            "x-ai/grok-4.5");
        GrokLogParser.BuildPricingCandidates("[grok] grok-4").Should().Equal(
            "grok-4", "xai/grok-4", "x-ai/grok-4"); // 剥 [grok] 前缀后 stripped == normalized → 去重
        GrokLogParser.BuildPricingCandidates("   ").Should().BeEmpty("剥前缀/去空白后为空串 → 无候选");
    }

    /// <summary>
    /// I-024 接线端到端（GK-1 候选链 + exact 全候选优先）：无 ticks 的 "-build" 变体行剥后缀后
    /// 经 xai/grok-4.5 精确命中（快照条目 2026-10-12 取自 CLI 20.0.29 内嵌 models-dev 数据）；
    /// 此前记 0 + UnpricedModels fail-loud。
    /// </summary>
    [Fact]
    public async Task Prices_build_suffix_model_through_xai_candidate()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            TurnLine("evt-price", "grok-4.5-build", 1000, 100, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));

        var result = await CreateAdapter(root).ScanAsync();
        var record = result.Records.Should().ContainSingle().Subject;

        record.Model.Should().Be("grok-4.5-build", "分组/显示仍用原始键");
        record.Pricing.Should().NotBeNull();
        record.Pricing!.Walk.Should().Be(PricingCandidateWalk.ExactThenFamilyAcrossCandidates,
            "蓝本 calculate_grok_cost_at：exact 全候选优先、再 fuzzy（GK-1）");
        record.Pricing.ModelCandidates.Should().HaveCount(6);

        var priced = new PricingEngine().PriceRecord(record);
        priced.CostUsd.Should().Be(0.002m + 0.0006m, "1000×$2/M + 100×$6/M（快照 xai/grok-4.5 基础档，剥 -build 后命中）");
        priced.MatchedKey.Should().Be("xai/grok-4.5");
    }

    /// <summary>I-024 对齐锚点：真实未知模型（grokbuild-alpha-x1 形态）在候选链下仍未命中，
    /// 行为与接线前一致（Missing + UnpricedModels fail-loud）。</summary>
    [Fact]
    public async Task Truly_unknown_grok_model_stays_unpriced_through_candidate_chain()
    {
        var root = CreateGrokHome();
        WriteSession(root, "proj/sess-1",
            TurnLine("evt-mystery", "grokbuild-alpha-x1", 100, 10, 0, 0, envelopeSeconds: 1_750_000_000, agentMs: null, ticks: 0));

        var result = await CreateAdapter(root).ScanAsync();
        var record = result.Records.Should().ContainSingle().Subject;

        var engine = new PricingEngine();
        engine.PriceRecord(record).IsPriced.Should().BeFalse("候选链全未命中 → 与接线前同为 Missing");
        engine.GetUnpricedModels().Should().ContainSingle(info => info.Model == "grokbuild-alpha-x1");
    }


    private static void WriteSession(string grokHome, string relativeSessionPath, params object[] items)
    {
        var sessionDirectory = Path.Combine(grokHome, "sessions", relativeSessionPath);
        Directory.CreateDirectory(sessionDirectory);
        var lines = items.OfType<string>().ToList();
        File.WriteAllText(Path.Combine(sessionDirectory, "updates.jsonl"), string.Join("\n", lines) + "\n");
        foreach (var (name, content) in items.OfType<(string Name, string Content)>())
        {
            File.WriteAllText(Path.Combine(sessionDirectory, name), content);
        }
        foreach (var summary in items.OfType<Summary>())
        {
            File.WriteAllText(Path.Combine(sessionDirectory, "summary.json"), summary.Content);
        }
    }

    private static string TurnLine(
        string eventId, string model, long input, long output, long cache, long reasoning,
        long envelopeSeconds, long? agentMs, long ticks)
    {
        var meta = agentMs is { } ms
            ? $"\"_meta\":{{\"eventId\":\"{eventId}\",\"agentTimestampMs\":{ms}}}"
            : $"\"_meta\":{{\"eventId\":\"{eventId}\"}}";
        var costField = ticks > 0 ? $",\"costUsdTicks\":{ticks}" : string.Empty;
        return $"{{\"timestamp\":{envelopeSeconds},\"params\":{{\"sessionId\":\"sess-1\",\"update\":{{\"sessionUpdate\":\"turn_completed\",\"usage\":{{\"modelUsage\":{{\"{model}\":{{\"inputTokens\":{input},\"outputTokens\":{output},\"cachedReadTokens\":{cache},\"reasoningTokens\":{reasoning}{costField}}}}}}}}},{meta}}}}}";
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
