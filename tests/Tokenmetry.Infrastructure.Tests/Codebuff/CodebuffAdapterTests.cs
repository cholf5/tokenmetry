namespace Tokenmetry.Infrastructure.Tests.Codebuff;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Codebuff;

/// <summary>
/// I-018: Codebuff 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage rust/adapters/codebuff 的 parser.rs / loader.rs / paths.rs，含蓝本内联测试的同名场景）。
/// </summary>
public class CodebuffAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- 蓝本 loader.rs 内联测试同名场景 ----

    [Fact]
    public async Task Loads_assistant_usage_from_chat_messages()
    {
        // 蓝本 loads_assistant_usage_from_chat_messages：metadata.usage 四桶 + credits + 路径推导 session id
        // （channel = 项目目录上两级目录名，fixture 根目录名即 channel 位）。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/project-a/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"user","text":"hello"},{"id":"assistant-message","role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"claude-sonnet-4-20250514","usage":{"inputTokens":100,"outputTokens":50,"cacheCreationInputTokens":20,"cacheReadInputTokens":10}},"credits":1.25}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 1, 2, 3, 4, 6, TimeSpan.Zero));
        record.SessionId.Should().Be($"{Path.GetFileName(fixture)}/project-a/2026-01-02T03-04-05.000Z",
            "session id = {channel}/{project}/{chat 目录名}（蓝本 derive_context）");
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(50);
        record.CacheCreationTokens.Should().Be(20);
        record.CacheReadTokens.Should().Be(10);
        record.Model.Should().Be("claude-sonnet-4-20250514");
        record.CostUsd.Should().BeNull("成本统一交定价引擎");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
    }

    [Fact]
    public async Task Falls_back_to_run_state_provider_usage()
    {
        // 蓝本 falls_back_to_run_state_provider_usage：runState.messageHistory 倒序扫 assistant 条目的
        // providerOptions（codebuff.model 覆盖、prompt_tokens_details.cached_tokens → cache_read）。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/project-a/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"variant":"agent","metadata":{"runState":{"sessionState":{"mainAgentState":{"messageHistory":[{"role":"user","providerOptions":{}},{"role":"assistant","providerOptions":{"codebuff":{"model":"openai/gpt-5","usage":{"prompt_tokens":100,"completion_tokens":50,"prompt_tokens_details":{"cached_tokens":10}}}}}]}}}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("openai/gpt-5");
        record.InputTokens.Should().Be(100, "prompt_tokens 别名映射");
        record.OutputTokens.Should().Be(50, "completion_tokens 别名映射");
        record.CacheReadTokens.Should().Be(10, "prompt_tokens_details.cached_tokens 归 cache_read");
        record.CacheCreationTokens.Should().Be(0);
    }

    [Fact]
    public async Task Falls_back_to_total_tokens_when_codebuff_parts_are_missing()
    {
        // 蓝本 falls_back_to_total_tokens_when_codebuff_parts_are_missing（文件场景等价覆盖）：
        // total 789 → 缺口补 output，extra 恒 0。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"m","usage":{"totalTokens":789}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(789);
        record.InputTokens.Should().Be(0);
    }

    [Fact]
    public async Task Metadata_codebuff_usage_fills_zero_fields_of_direct_usage()
    {
        // 蓝本 extract_assistant_usage 合并序：metadata.usage 先到、metadata.codebuff.usage 逐字段兜底。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-02-03T04-05-06.000Z/chat-messages.json",
            """[{"variant":"ai","metadata":{"model":"claude-sonnet-4-20250514","usage":{"inputTokens":10,"outputTokens":5},"codebuff":{"usage":{"inputTokens":999,"outputTokens":888,"cacheCreationInputTokens":7,"cacheReadInputTokens":3}}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(10, "metadata.usage 先到者保留");
        record.OutputTokens.Should().Be(5);
        record.CacheCreationTokens.Should().Be(7, "codebuff.usage 仅填补零值字段");
        record.CacheReadTokens.Should().Be(3);
    }

    [Fact]
    public async Task Cached_tokens_created_alias_maps_to_cache_creation()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-02-03T04-05-06.000Z/chat-messages.json",
            """[{"role":"assistant","metadata":{"usage":{"inputTokens":1,"outputTokens":1,"cachedTokensCreated":6}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle().Which.CacheCreationTokens.Should().Be(6);
    }

    [Fact]
    public async Task First_positive_alias_wins_per_field()
    {
        // 蓝本 pick_u64：取第一个 >0 的别名键（0 值与浮点跳过试下一键）。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-02-03T04-05-06.000Z/chat-messages.json",
            """[{"role":"assistant","metadata":{"usage":{"inputTokens":0,"promptTokens":11,"outputTokens":0,"completionTokens":22}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(11, "inputTokens=0 跳过、promptTokens 生效");
        record.OutputTokens.Should().Be(22);
    }

    // ---- 去重（蓝本 dedup_key + HashMap insert last-wins） ----

    [Fact]
    public async Task Message_id_dedup_is_last_wins_across_sorted_files()
    {
        // 蓝本 deduped.insert（last-wins）：去重键含 session id（{channel}/{project}/{chat}，纯路径后缀
        // 函数），两个不同前缀的文件在 channel/project/chat 同名时同键——后到文件覆盖（与 Qwen/Kimi 的
        // first-wins 相反）。文件按路径排序：r1 在前、r2 在后。
        var baseDir = CreateTempDir();
        var first = Path.Combine(baseDir, "r1");
        var second = Path.Combine(baseDir, "r2");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        WriteChatFile(Path.Combine(first, "manicode"), "projects/p/chats/c/chat-messages.json",
            """[{"id":"shared","role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"m","usage":{"inputTokens":10,"outputTokens":1}}}]""");
        WriteChatFile(Path.Combine(second, "manicode"), "projects/p/chats/c/chat-messages.json",
            """[{"id":"shared","role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"m","usage":{"inputTokens":20,"outputTokens":2}}}]""");

        var locator = new CodebuffLogLocator(_ => $"{Path.Combine(first, "manicode")},{Path.Combine(second, "manicode")}");
        var result = await new CodebuffAdapter(locator: locator).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(20, "同键后到者覆盖（last-wins，蓝本 HashMap insert）");
        // A-1：路径断言用平台分隔符（POSIX 斜杠字面量在 Windows 下不成立）。
        record.SourceFile.Should().Contain($"{Path.DirectorySeparatorChar}r2{Path.DirectorySeparatorChar}", "存活记录来自排序在后的文件");
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    [Fact]
    public async Task Same_file_later_ordinal_wins_for_id_dedup()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"id":"dup","role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"m","usage":{"inputTokens":1,"outputTokens":1}}},{"id":"dup","role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"m","usage":{"inputTokens":2,"outputTokens":2}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(2);
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    [Fact]
    public async Task Distinct_usage_records_share_no_dedup_key_without_id()
    {
        // 无 id 的内容键含数组 ordinal 与五个计数分量（in/out/cr/cc/extra）：同刻同 token 的记录不折叠。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"m","usage":{"inputTokens":10,"outputTokens":1}}},{"role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"m","usage":{"inputTokens":10,"outputTokens":1}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().HaveCount(2, "ordinal 进入无 id 内容键");
        result.Diagnostics.DuplicateRecords.Should().Be(0);
    }

    // ---- 时间戳回退链 ----

    [Fact]
    public async Task Missing_timestamp_falls_back_to_chat_directory_then_mtime()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-03-04T05-06-07.000Z/chat-messages.json",
            """[{"role":"assistant","metadata":{"model":"m","usage":{"inputTokens":3,"outputTokens":3}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should()
            .Be(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
                "chat 目录名 03-04-05.000Z 还原为 03:04:05.000Z（蓝本 parse_codebuff_chat_id_timestamp）");
    }

    [Fact]
    public async Task Numeric_timestamp_below_threshold_is_seconds()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-03-04T05-06-07.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":1767294246,"metadata":{"model":"m","usage":{"inputTokens":1,"outputTokens":1}}},{"role":"assistant","timestamp":1767294246000,"metadata":{"model":"m","usage":{"inputTokens":2,"outputTokens":2}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records[0].Timestamp.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(1767294246),
            "<10^10 的数字时间戳按秒处理（蓝本 timestamp_value）");
        result.Records[1].Timestamp.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(1767294246),
            "≥10^10 按毫秒处理，两者同刻");
    }

    [Fact]
    public async Task Unparseable_string_timestamp_falls_back_to_chat_directory_timestamp()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-03-04T05-06-07.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"not-a-time","metadata":{"model":"m","usage":{"inputTokens":1,"outputTokens":1}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should()
            .Be(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
    }

    [Fact]
    public async Task Created_at_and_metadata_timestamp_participate_in_fallback_chain()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-03-04T05-06-07.000Z/chat-messages.json",
            """[{"role":"assistant","createdAt":"2026-05-06T07:08:09.000Z","metadata":{"model":"m","usage":{"inputTokens":1,"outputTokens":1}}}]""");
        WriteChatFile(fixture, "projects/p/chats/2026-03-04T05-06-08.000Z/chat-messages.json",
            """[{"role":"assistant","metadata":{"timestamp":"2026-06-07T08:09:10.000Z","model":"m","usage":{"inputTokens":2,"outputTokens":2}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records[0].Timestamp.Should().Be(new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero),
            "消息无 timestamp 时回退 createdAt");
        result.Records[1].Timestamp.Should().Be(new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero),
            "timestamp/createdAt 均缺时回退 metadata.timestamp");
    }

    // ---- 载荷类型角落（历史教训：JSON 载荷字段可为非对象） ----

    [Fact]
    public async Task Metadata_usage_null_or_scalar_is_tolerated()
    {
        // 蓝本 parse_usage_object(Option<&Value>)：null / 标量 / 数组 → as_object None → 全零 usage
        // （仅剩消息级 credits 可走 has_signal）。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant","metadata":{"model":"m","usage":null},"credits":2.5},{"role":"assistant","metadata":{"model":"m","usage":42}},{"role":"assistant","metadata":{"model":"m","usage":["a"]}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].InputTokens.Should().Be(0);
        result.Records[0].OutputTokens.Should().Be(0, "usage 标量的四桶全零");
        result.Diagnostics.LinesSkipped.Should().Be(2, "null 与标量 usage 无 credits 时无信号跳过");
    }

    [Fact]
    public async Task Metadata_non_object_is_ignored_entirely()
    {
        // metadata 非对象（标量/数组）→ 蓝本 object_field None → 整段取数链跳过。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant","metadata":"garbage"},{"role":"assistant","metadata":[{"model":"m","usage":{"inputTokens":9,"outputTokens":9}}]},{"role":"assistant","metadata":{"model":"m","usage":{"inputTokens":5,"outputTokens":5}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(5);
        result.Diagnostics.LinesSkipped.Should().Be(2);
    }

    [Fact]
    public async Task Non_object_messages_and_non_assistant_rows_are_skipped()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """["scalar",42,null,{"role":"user","metadata":{"model":"m","usage":{"inputTokens":5,"outputTokens":5}}},{"variant":"tool","metadata":{"model":"m","usage":{"inputTokens":5,"outputTokens":5}}},{"role":"assistant","metadata":{"model":"m","usage":{"inputTokens":5,"outputTokens":5}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.LinesSkipped.Should().Be(5, "非对象元素、user 行与未知 variant 行跳过（仓库口径显式计数）");
    }

    [Fact]
    public async Task Assistant_without_usage_signal_is_skipped()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant"},{"role":"assistant","metadata":{"model":"m","usage":{"inputTokens":0,"outputTokens":0}}},{"role":"assistant","credits":0,"metadata":{"model":"m"}},{"role":"assistant","credits":1.5,"metadata":{"model":"m"}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle().Which.OutputTokens.Should().Be(0,
            "credits>0 即有信号（蓝本 has_signal），四桶全零记录照常产出");
        result.Diagnostics.LinesSkipped.Should().Be(3);
    }

    [Fact]
    public async Task Usage_object_carries_model_and_credits()
    {
        // 蓝本 parse_usage_object 末段：usage 对象自身的 model/credits 并入（model 尚无值时生效）。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant","metadata":{"usage":{"model":"openai/gpt-5","credits":9.5,"inputTokens":4,"outputTokens":4}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("openai/gpt-5", "metadata.usage.model 兜底生效（metadata.model 缺失）");
        record.InputTokens.Should().Be(4);
    }

    [Fact]
    public async Task Run_state_user_rows_and_non_object_history_entries_are_ignored()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"variant":"agent","metadata":{"runState":{"sessionState":{"mainAgentState":{"messageHistory":["garbage",{"role":"assistant","providerOptions":{"codebuff":{"model":"openai/gpt-5","usage":{"prompt_tokens":7,"completion_tokens":7}}}},{"role":"assistant"},{"role":"assistant","providerOptions":"not-object"}]}}}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(7);
    }

    [Fact]
    public async Task Run_state_reverse_iteration_gives_last_assistant_precedence()
    {
        // 蓝本 history.iter().rev()：末位 assistant 条目字段先到先得（merge_fallback 保序语义）。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"variant":"agent","metadata":{"runState":{"sessionState":{"mainAgentState":{"messageHistory":[{"role":"assistant","providerOptions":{"codebuff":{"model":"openai/gpt-5","usage":{"prompt_tokens":100,"completion_tokens":10}}}},{"role":"assistant","providerOptions":{"codebuff":{"model":"openai/gpt-5","usage":{"prompt_tokens":50,"completion_tokens":5}}}}]}}}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(50, "倒序扫描：末位 assistant 的字段优先");
        record.OutputTokens.Should().Be(5);
    }

    [Fact]
    public async Task Run_state_codebuff_model_overrides_usage_model()
    {
        // 蓝本 entry_usage.model = codebuff.model.or(entry_usage.model)：codebuff.model 有值即覆盖
        // providerOptions.usage.model。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"variant":"agent","metadata":{"runState":{"sessionState":{"mainAgentState":{"messageHistory":[{"role":"assistant","providerOptions":{"usage":{"model":"openai/gpt-5","prompt_tokens":8,"completion_tokens":8},"codebuff":{"model":"openai/gpt-5-mini","usage":{"prompt_tokens":9,"completion_tokens":9}}}}]}}}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("openai/gpt-5-mini", "codebuff.model 覆盖 usage.model");
        record.InputTokens.Should().Be(8, "providerOptions.usage 的字段先到（codebuff.usage 仅兜底零值）");
    }

    [Fact]
    public async Task Run_state_fills_only_missing_fields_of_direct_usage()
    {
        // 蓝本 merge_fallback(usage, run_state_usage)：runState 仅兜底直接 usage 的零值字段。
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"variant":"agent","metadata":{"usage":{"inputTokens":10,"outputTokens":2},"runState":{"sessionState":{"mainAgentState":{"messageHistory":[{"role":"assistant","providerOptions":{"codebuff":{"usage":{"prompt_tokens":999,"completion_tokens":999,"cached_tokens_created":4}}}}]}}}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(10, "直接 usage 先到者保留");
        record.OutputTokens.Should().Be(2);
        record.CacheCreationTokens.Should().Be(4, "runState 的 cached_tokens_created 别名兜底 cache_creation");
    }

    // ---- 文件级容错与发现 ----

    [Fact]
    public async Task Malformed_and_non_array_files_are_counted_and_do_not_stop_the_scan()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json", "{not json");
        WriteChatFile(fixture, "projects/p/chats/2026-01-03T03-04-05.000Z/chat-messages.json", """{"object":"not-array"}""");
        WriteChatFile(fixture, "projects/p/chats/2026-01-04T03-04-05.000Z/chat-messages.json", "null");
        WriteChatFile(fixture, "projects/p/chats/2026-01-05T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-05T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":1,"outputTokens":1}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.MalformedLines.Should().Be(3, "坏 JSON、顶层对象与顶层 null 各计 1（serde Vec<Value> 同拒绝面）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Only_chat_messages_json_files_are_discovered()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"m","usage":{"inputTokens":1,"outputTokens":1}}}]""");
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/other.json", """[{"role":"assistant","metadata":{"usage":{"inputTokens":9,"outputTokens":9}}}]""");
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/notes.txt", "no");
        WriteChatFile(fixture, "projects/p/outside.json", "no");
        // 递归嵌套（蓝本 collect_files_with_extension 深度不限）。
        WriteChatFile(fixture, "projects/p/chats/2026-01-06T00-00-00.000Z/nested/deep/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-06T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":2,"outputTokens":2}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().HaveCount(2, "递归发现但仅文件名恰为 chat-messages.json 者解析");
        result.Diagnostics.FilesDiscovered.Should().Be(2);
    }

    [Fact]
    public async Task Default_model_applies_when_metadata_has_no_model()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"usage":{"inputTokens":1,"outputTokens":1}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Should().ContainSingle().Which.Model.Should().Be("codebuff-unknown",
            "蓝本 DEFAULT_CODEBUFF_MODEL");
    }

    [Fact]
    public async Task Records_are_sorted_by_timestamp_across_files()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-09T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-09T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":20,"outputTokens":2}}}]""");
        WriteChatFile(fixture, "projects/p/chats/2026-01-08T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-08T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":10,"outputTokens":1}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();

        result.Records.Select(record => record.InputTokens).Should().ContainInOrder(10, 20);
    }

    // ---- 定价 ----

    [Fact]
    public async Task Prices_records_via_builtin_snapshot()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-19T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-19T00:00:00.000Z","metadata":{"model":"claude-sonnet-4-20250514","usage":{"inputTokens":10000,"outputTokens":1000,"cacheCreationInputTokens":200,"cacheReadInputTokens":3000}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();
        var engine = new PricingEngine();

        var priced = engine.PriceRecord(result.Records.Should().ContainSingle().Subject);
        priced.Source.Should().Be(PricingSourceKind.Snapshot, "裸 anthropic 日期后缀名由家族模式兜底命中");
        // 10000×$3/M + 1000×$15/M + 200×$3.75/M + 3000×$0.3/M = 0.03 + 0.015 + 0.00075 + 0.0009
        priced.CostUsd.Should().Be(0.04665m);
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_model_stays_unpriced()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-19T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-19T00:00:00.000Z","metadata":{"model":"totally-unknown-model","usage":{"inputTokens":10,"outputTokens":10}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();
        var engine = new PricingEngine();

        engine.PriceRecord(result.Records.Should().ContainSingle().Subject).CostUsd.Should().BeNull();
        engine.GetUnpricedModels().Should().ContainSingle().Which.Model.Should().Be("totally-unknown-model");
    }

    /// <summary>
    /// I-024 接线（CB-1 候选重试）：infer_provider 推断 provider 后拼 "{provider}/{model}" 候选，
    /// 引擎逐候选定价——grok-4.5 裸名经新补快照条目 xai/grok-4.5（2026-10-12 取自 CLI 20.0.29
    /// 内嵌 models-dev 数据）命中；此前快照无该模型 → 记 0 + UnpricedModels fail-loud。
    /// </summary>
    [Fact]
    public async Task Carries_inferred_provider_candidates_and_prices_model()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"grok-4.5","usage":{"inputTokens":1000,"outputTokens":100}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();
        var record = result.Records.Should().ContainSingle().Subject;

        CodebuffChatParser.InferProvider("grok-4.5").Should().Be("xai");
        record.Pricing.Should().NotBeNull();
        record.Pricing!.ModelCandidates.Should().Equal("grok-4.5", "xai/grok-4.5");
        record.Model.Should().Be("grok-4.5", "分组/显示仍用裸模型名");

        var priced = new PricingEngine().PriceRecord(record);
        priced.CostUsd.Should().Be(0.002m + 0.0006m, "1000×$2/M + 100×$6/M（快照 xai/grok-4.5 基础档）");
        priced.MatchedKey.Should().Be("xai/grok-4.5");
    }

    /// <summary>
    /// 蓝本 infer_provider 全分支移植锚点（I-024 接线）：前缀 → provider 映射逐字对齐
    /// （claude-·anthropic/·anthropic. / gpt-·o1·o3·o4·openai/ / gemini·google/ / grok·xai/ /
    /// openrouter/ / 其余 unknown），大小写不敏感（蓝本 to_ascii_lowercase）。
    /// </summary>
    [Fact]
    public void Infers_provider_from_model_prefixes_per_blueprint()
    {
        CodebuffChatParser.InferProvider("claude-sonnet-4-5").Should().Be("anthropic");
        CodebuffChatParser.InferProvider("anthropic/gpt-x").Should().Be("anthropic");
        CodebuffChatParser.InferProvider("anthropic.claude").Should().Be("anthropic");
        CodebuffChatParser.InferProvider("gpt-5.1").Should().Be("openai");
        CodebuffChatParser.InferProvider("o1-mini").Should().Be("openai");
        CodebuffChatParser.InferProvider("o3").Should().Be("openai");
        CodebuffChatParser.InferProvider("o4-mini").Should().Be("openai");
        CodebuffChatParser.InferProvider("openai/codex").Should().Be("openai");
        CodebuffChatParser.InferProvider("gemini-2.5-pro").Should().Be("google");
        CodebuffChatParser.InferProvider("google/gemini-x").Should().Be("google");
        CodebuffChatParser.InferProvider("grok-4").Should().Be("xai");
        CodebuffChatParser.InferProvider("xai/grok-4.5").Should().Be("xai");
        CodebuffChatParser.InferProvider("openrouter/deepseek-v4").Should().Be("openrouter");
        CodebuffChatParser.InferProvider("kimi-k2.6").Should().Be("unknown");
        CodebuffChatParser.InferProvider("GPT-5").Should().Be("openai", "蓝本先 to_ascii_lowercase");
        // 自带 "{provider}/" 前缀的模型不再拼重复限定键（蓝本 model.starts_with("{provider}/") 同式）。
        CodebuffChatParser.BuildModelCandidates("xai/grok-4.5").Should().Equal("xai/grok-4.5");
        CodebuffChatParser.BuildModelCandidates("kimi-k2.6").Should().Equal("kimi-k2.6"); // provider unknown → 不重试
    }

    /// <summary>I-024 对齐锚点：Codebuff 蓝本不做 "-build" 后缀剥离（那是 Grok GK-1 的链）——
    /// grok-4.5-build 在 codebuff 候选链下未命中，行为与接线前一致（Missing fail-loud）。</summary>
    [Fact]
    public async Task Build_suffix_models_stay_unpriced_without_build_strip()
    {
        var fixture = CreateFixtureRoot();
        WriteChatFile(fixture, "projects/p/chats/2026-01-02T03-04-05.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-02T03:04:06.000Z","metadata":{"model":"grok-4.5-build","usage":{"inputTokens":100,"outputTokens":10}}}]""");

        var result = await CreateAdapter(fixture).ScanAsync();
        var record = result.Records.Should().ContainSingle().Subject;

        record.Pricing!.ModelCandidates.Should().Equal("grok-4.5-build", "xai/grok-4.5-build");
        var engine = new PricingEngine();
        engine.PriceRecord(record).IsPriced.Should().BeFalse("蓝本 codebuff 无 -build 剥离，两个候选都未命中");
        engine.GetUnpricedModels().Should().ContainSingle(info => info.Model == "grok-4.5-build");
    }

    // ---- 目录发现与环境变量（蓝本 paths.rs 语义） ----

    [Fact]
    public async Task Codebuff_data_dir_environment_variable_is_respected_without_default_fallback()
    {
        var relocated = CreateFixtureRoot();
        WriteChatFile(relocated, "projects/relocated/chats/2026-01-19T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-19T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":5,"outputTokens":1}}}]""");

        try
        {
            Environment.SetEnvironmentVariable(CodebuffLogLocator.CodebuffDataDirEnvVariable, relocated);
            var result = await new CodebuffAdapter().ScanAsync();

            result.Records.Should().ContainSingle();
            result.Records[0].SessionId.Should().Be("codebuff/relocated/2026-01-19T00-00-00.000Z");
            result.Roots.Should().ContainSingle().Which.Should().Be(Path.Combine(relocated, "projects"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodebuffLogLocator.CodebuffDataDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Set_but_empty_environment_variable_yields_no_data_without_default_fallback()
    {
        // 蓝本 env 分支提前选定：空串生效为空列表，即使默认 channel 布局存在也不回退。
        var fakeHome = CreateTempDir();
        WriteChatFile(Path.Combine(fakeHome, ".config", "manicode"),
            "projects/p/chats/2026-01-19T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-19T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":5,"outputTokens":1}}}]""");

        var locator = new CodebuffLogLocator(name => name == CodebuffLogLocator.CodebuffDataDirEnvVariable ? "" : fakeHome);
        var roots = locator.ResolveProjectRoots(null);
        var files = locator.CollectChatFiles(roots);

        roots.Should().BeEmpty();
        files.Should().BeEmpty();
    }

    [Fact]
    public async Task Environment_variable_accepts_multiple_comma_separated_roots()
    {
        var first = CreateFixtureRoot();
        var second = CreateFixtureRoot();
        WriteChatFile(first, "projects/a/chats/2026-01-02T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-02T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":1,"outputTokens":1}}}]""");
        WriteChatFile(second, "projects/b/chats/2026-01-03T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-03T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":2,"outputTokens":2}}}]""");

        var locator = new CodebuffLogLocator(_ => $"{first},{second}");
        var result = await new CodebuffAdapter(locator: locator).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Roots.Should().HaveCount(2, "逗号分隔多根逐个派生 projects/ 子目录");
    }

    [Fact]
    public async Task Root_named_projects_is_used_directly()
    {
        // 蓝本 root.file_name() == "projects" → 原样作为项目根（不再拼接 projects/）。
        var projectsDir = Path.Combine(CreateTempDir(), "projects");
        Directory.CreateDirectory(projectsDir);
        WriteChatFile(projectsDir, "p/chats/2026-01-02T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-02T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":3,"outputTokens":3}}}]""");

        var locator = new CodebuffLogLocator(_ => projectsDir);
        var result = await new CodebuffAdapter(locator: locator).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Roots.Should().ContainSingle().Which.Should().Be(projectsDir);
    }

    [Fact]
    public async Task Root_named_projects_recognized_with_casing_mismatch()
    {
        // B-012（Windows 冒烟后续）：Windows/macOS 文件系统大小写不敏感——根目录键入大小写与
        // 磁盘不符时 Exists 通过、旧 Ordinal 文件名比较失败 → 判定走 projects/ 拼接分支 → Codebuff
        // 源静默空。键入大小写与磁盘不符也要按"根即 projects"识别（卡面场景：键入 ...\PROJECTS，
        // 盘上 projects；键入路径原样返回、不做规范化是既有口径）。Linux 大小写敏感、无此场景。
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var projectsDir = Path.Combine(CreateTempDir(), "projects");
        Directory.CreateDirectory(projectsDir);
        WriteChatFile(projectsDir, "p/chats/2026-01-02T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-02T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":3,"outputTokens":3}}}]""");
        var typedRoot = Path.Combine(Path.GetDirectoryName(projectsDir)!, "PROJECTS");

        var locator = new CodebuffLogLocator(_ => typedRoot);
        var result = await new CodebuffAdapter(locator: locator).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Roots.Should().ContainSingle().Which.Should().Be(typedRoot);
    }

    [Fact]
    public async Task Default_channel_directories_are_enumerated_under_config_home()
    {
        // 蓝本默认分支：~/.config/<channel> ×3 枚举（存在 projects/ 的 channel 生效），多 channel 并行出数。
        var fakeHome = CreateTempDir();
        WriteChatFile(Path.Combine(fakeHome, ".config", "manicode"),
            "projects/p/chats/2026-01-02T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-02T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":1,"outputTokens":1}}}]""");
        WriteChatFile(Path.Combine(fakeHome, ".config", "manicode-dev"),
            "projects/q/chats/2026-01-03T00-00-00.000Z/chat-messages.json",
            """[{"role":"assistant","timestamp":"2026-01-03T00:00:00.000Z","metadata":{"model":"m","usage":{"inputTokens":2,"outputTokens":2}}}]""");
        Directory.CreateDirectory(Path.Combine(fakeHome, ".config", "manicode-staging"));

        var locator = new CodebuffLogLocator(name => name == "USERPROFILE" || name == "HOME" ? fakeHome : null);
        var result = await new CodebuffAdapter(locator: locator).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records.Select(record => record.SessionId).Should().Contain(
            "manicode/p/2026-01-02T00-00-00.000Z",
            "manicode-dev/q/2026-01-03T00-00-00.000Z");
        result.Roots.Should().HaveCount(2, "staging 无 projects/ 子目录 → 派生后丢弃");
    }

    [Fact]
    public async Task Missing_default_directories_yield_no_data()
    {
        // 默认分支：~/.config/<channel> 不存在 → 派生的 projects/ 不存在 → 无数据（不抛错）。
        var fakeHome = CreateTempDir();
        var locator = new CodebuffLogLocator(name => name == "USERPROFILE" || name == "HOME" ? fakeHome : null);

        var roots = locator.ResolveProjectRoots(null);

        roots.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_configured_directory_yields_no_data()
    {
        var missing = Path.Combine(CreateTempDir(), "codebuff-missing");

        var result = await CreateAdapter(missing).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    // ---- helpers ----

    private CodebuffAdapter CreateAdapter(string dataDir) => new(configuredDataDir: dataDir);

    private string CreateFixtureRoot()
    {
        var path = Path.Combine(CreateTempDir(), "codebuff");
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-codebuff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static void WriteChatFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
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
