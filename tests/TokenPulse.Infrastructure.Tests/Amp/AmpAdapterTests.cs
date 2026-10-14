namespace TokenPulse.Infrastructure.Tests.Amp;

using TokenPulse.Core.Models;
using TokenPulse.Core.Services;
using TokenPulse.Infrastructure.Amp;

/// <summary>
/// I-014: Amp 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage rust/adapters/amp 的 parser.rs / loader.rs / paths.rs，含蓝本内联测试的同名场景）。
/// </summary>
public class AmpAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- 账本路径（蓝本 parser.rs 内联测试同名场景） ----

    [Fact]
    public async Task Falls_back_to_total_tokens_when_amp_parts_are_missing()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-thread-a.json",
            """{"id":"thread-a","usageLedger":{"events":[{"id":"event-a","timestamp":"2026-01-02T00:00:00.000Z","model":"gpt-5","tokens":{"total":345}}]}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SessionId.Should().Be("thread-a");
        record.Model.Should().Be("gpt-5");
        record.OutputTokens.Should().Be(345, "output 为 0 时 total 缺口补入 output（蓝本 apply_total_token_fallback）");
        record.InputTokens.Should().Be(0);
    }

    [Fact]
    public async Task Ledger_events_attach_cache_from_assistant_messages_by_to_message_id()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-thread-a.json",
            """
            {
              "id": "thread-a",
              "usageLedger": {"events": [
                {"id":"event-a","timestamp":"2026-01-02T00:00:00.000Z","model":"gpt-5",
                 "tokens":{"input":800,"output":100},"toMessageId":11}
              ]},
              "messages": [
                {"role":"user","content":"hi"},
                {"role":"assistant","messageId":11,
                 "usage":{"cacheCreationInputTokens":986,"cacheReadInputTokens":11372}}
              ]
            }
            """);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(800);
        record.OutputTokens.Should().Be(100);
        record.CacheCreationTokens.Should().Be(986, "toMessageId 索引 assistant 消息的缓存四桶");
        record.CacheReadTokens.Should().Be(11_372);
    }

    [Fact]
    public async Task Ledger_events_take_precedence_over_messages_usage()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-thread-a.json",
            """
            {
              "id":"thread-a",
              "usageLedger":{"events":[
                {"id":"event-a","timestamp":"2026-01-02T00:00:00.000Z","model":"gpt-5","tokens":{"input":1,"output":2}}
              ]},
              "messages":[
                {"role":"assistant","usage":{
                  "model":"claude-haiku-4-5-20251001","inputTokens":99,"outputTokens":99,
                  "timestamp":"2026-01-19T11:42:10.652Z"}}
              ]
            }
            """);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("gpt-5", "账本存在即走账本路径");
        record.InputTokens.Should().Be(1);
    }

    [Fact]
    public async Task Ledger_with_present_but_empty_events_does_not_fall_back_to_messages()
    {
        // 蓝本 Some/None 语义角落：events 数组存在（空）即走账本路径、产出 0 条，不回退 messages。
        var root = CreateTempDataDir();
        WriteThread(root, "T-thread-a.json",
            """
            {
              "id":"thread-a",
              "usageLedger":{"events":[]},
              "messages":[{"role":"assistant","usage":{
                "model":"m","inputTokens":9,"outputTokens":9,"timestamp":"2026-01-19T11:42:10.652Z"}}]
            }
            """);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Ledger_events_without_model_or_tokens_are_skipped()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-thread-a.json",
            """
            {"id":"thread-a","usageLedger":{"events":[
              {"id":"e1","timestamp":"2026-01-02T00:00:00.000Z","tokens":{"input":1,"output":1}},
              {"id":"e2","timestamp":"2026-01-02T00:00:00.000Z","model":"gpt-5"},
              {"id":"e3","timestamp":"not-a-time","model":"gpt-5","tokens":{"input":5,"output":5}},
              {"id":"e4","timestamp":"2026-01-02T00:00:00.000Z","model":"gpt-5","tokens":{"input":7,"output":7}}
            ]}}
            """);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].InputTokens.Should().Be(7);
        result.Diagnostics.LinesSkipped.Should().Be(2, "缺 model 与缺 tokens 的事件计 skipped");
        result.Diagnostics.InvalidRecords.Should().Be(1, "时间戳不可解析的事件计 invalid（仓库口径）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    // ---- messages 路径（蓝本 parser.rs 内联测试同名场景） ----

    [Fact]
    public async Task Reads_usage_from_messages_when_ledger_is_missing()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-thread-a.json",
            """
            {
              "id":"T-thread-a",
              "messages":[
                {"role":"user","content":"hi"},
                {"role":"assistant","usage":{
                  "model":"claude-haiku-4-5-20251001","inputTokens":10,"outputTokens":178,
                  "cacheCreationInputTokens":986,"cacheReadInputTokens":11372,
                  "totalInputTokens":12368,"timestamp":"2026-01-19T11:42:10.652Z"}},
                {"role":"assistant","usage":{
                  "model":"claude-haiku-4-5-20251001","inputTokens":5,"outputTokens":42,
                  "cacheCreationInputTokens":0,"cacheReadInputTokens":12000,
                  "timestamp":"2026-01-19T11:43:00.000Z"}}
              ]
            }
            """);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        var first = result.Records[0];
        first.InputTokens.Should().Be(10);
        first.OutputTokens.Should().Be(178);
        first.CacheCreationTokens.Should().Be(986);
        first.CacheReadTokens.Should().Be(11_372);
        first.Model.Should().Be("claude-haiku-4-5-20251001");
        first.SessionId.Should().Be("T-thread-a");
        first.Timestamp.Should().Be(new DateTimeOffset(2026, 1, 19, 11, 42, 10, 652, TimeSpan.Zero));
        result.Records[1].InputTokens.Should().Be(5);
    }

    [Fact]
    public async Task Message_usage_falls_back_to_message_level_timestamp_and_model()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-thread-a.json",
            """{"id":"T-thread-a","messages":[{"role":"assistant","model":"gpt-5","timestamp":"2026-01-20T08:00:00.000Z","usage":{"inputTokens":10,"outputTokens":20}}]}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("gpt-5", "usage 缺 model 时回退消息级 model");
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 1, 20, 8, 0, 0, TimeSpan.Zero),
            "usage 缺 timestamp 时回退消息级 timestamp");
    }

    [Fact]
    public async Task Falls_back_to_total_tokens_in_messages_path()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-thread-a.json",
            """{"id":"T-thread-a","messages":[{"role":"assistant","usage":{"model":"m","totalTokens":345,"timestamp":"2026-01-19T11:42:10.652Z"}}]}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(345);
        record.InputTokens.Should().Be(0);
    }

    [Fact]
    public async Task Skips_messages_with_no_usage_tokens()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-thread-a.json",
            """{"id":"T-thread-a","messages":[{"role":"assistant","usage":{"model":"m","inputTokens":0,"outputTokens":0,"cacheCreationInputTokens":0,"cacheReadInputTokens":0,"timestamp":"2026-01-19T11:42:10.652Z"}}]}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.LinesSkipped.Should().Be(1);
    }

    [Fact]
    public async Task Empty_usage_ledger_falls_back_to_message_usage()
    {
        // 蓝本 empty_usage_ledger_falls_back_to_message_usage：usageLedger 对象无 events 键（lenient_array
        // → None）→ 回退 messages；usageLedger 非对象（lenient_object → None）同款。
        var root = CreateTempDataDir();
        WriteThread(root, "T-thread-a.json",
            """
            {"id":"T-thread-a","usageLedger":{},"messages":[{"role":"assistant","usage":{
              "model":"m","inputTokens":10,"outputTokens":178,"timestamp":"2026-01-19T11:42:10.652Z"}}]}
            """);
        WriteThread(root, "T-thread-b.json",
            """
            {"id":"T-thread-b","usageLedger":"not-an-object","messages":["garbage",{"role":"assistant","usage":{
              "model":"m","inputTokens":10,"outputTokens":178,"timestamp":"2026-01-19T11:42:10.652Z"}}]}
            """);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "空账本对象与非对象账本都回退 messages；非对象消息元素跳过");
        result.Records.Should().OnlyContain(record => record.InputTokens == 10 && record.OutputTokens == 178);
    }

    // ---- 非对象 tokens/usage 载荷（I-014 审查修复，蓝本 Option<Value> null/标量语义逐条） ----

    [Fact]
    public async Task Ledger_tokens_null_skips_event()
    {
        // 蓝本 Option<Value>：JSON null 反序列化为 None → 不过 `let Some(tokens)` 关，事件跳过（不崩溃）。
        var root = CreateTempDataDir();
        WriteThread(root, "T-null.json",
            """{"id":"t","usageLedger":{"events":[{"timestamp":"2026-01-19T10:00:00.000Z","model":"gpt-5","tokens":null}]}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.LinesSkipped.Should().Be(1);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Ledger_scalar_tokens_still_attaches_cache()
    {
        // 蓝本 Some(标量) 过 `let Some(tokens)` 关：Value::get 全 None → 四桶 0，但 toMessageId 缓存
        // 照常 attach——全 0 + 缓存可产出记录（审查特别提醒：不得把该场景做成"不产出"）。
        var root = CreateTempDataDir();
        WriteThread(root, "T-scalar.json",
            """
            {
              "id":"t",
              "usageLedger":{"events":[
                {"timestamp":"2026-01-19T10:00:00.000Z","model":"gpt-5","tokens":5,"toMessageId":11}
              ]},
              "messages":[
                {"role":"assistant","messageId":11,
                 "usage":{"cacheCreationInputTokens":986,"cacheReadInputTokens":11372}}
              ]
            }
            """);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(0);
        record.OutputTokens.Should().Be(0);
        record.CacheCreationTokens.Should().Be(986, "标量 tokens 的四桶为 0，缓存四桶照常 attach");
        record.CacheReadTokens.Should().Be(11_372);
    }

    [Fact]
    public async Task Message_usage_null_skips_message()
    {
        // 蓝本 message.usage Option<Value>：null → None → `let Some(usage)` 不过，消息跳过（不崩溃）。
        var root = CreateTempDataDir();
        WriteThread(root, "T-null-usage.json",
            """{"id":"t","messages":[{"role":"assistant","usage":null}]}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.LinesSkipped.Should().Be(1);
    }

    [Fact]
    public async Task Message_usage_scalar_falls_back_to_message_level_fields()
    {
        // 蓝本 Some(标量)：usage.get 全 None → timestamp/model 回退消息级字段、四桶 0——随后按
        // 全零口径跳过（回退链执行且不崩溃）。
        var root = CreateTempDataDir();
        WriteThread(root, "T-scalar-usage.json",
            """{"id":"t","messages":[{"role":"assistant","model":"gpt-5","timestamp":"2026-01-19T11:42:10.652Z","usage":5}]}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty("usage 标量的四桶为 0，全零跳过");
        result.Diagnostics.LinesSkipped.Should().Be(1, "回退链成功取到 timestamp/model，但 token 全零计 skipped");
    }

    [Fact]
    public async Task Cache_source_message_with_null_usage_registers_zero_entry()
    {
        // 蓝本 cache_tokens_by_message_id：usage None 的 assistant 消息按 (0,0) 登记该 messageId
        // （不崩溃、映射不缺失），账本事件经 toMessageId attach 到 0。
        var root = CreateTempDataDir();
        WriteThread(root, "T-null-cache.json",
            """
            {
              "id":"t",
              "usageLedger":{"events":[
                {"timestamp":"2026-01-19T10:00:00.000Z","model":"gpt-5","tokens":{"input":100,"output":10},"toMessageId":11}
              ]},
              "messages":[
                {"role":"user","content":"hi"},
                {"role":"assistant","messageId":11,"usage":null}
              ]
            }
            """);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(10);
        record.CacheCreationTokens.Should().Be(0, "usage:null 的缓存源按 (0,0) 登记");
        record.CacheReadTokens.Should().Be(0);
    }

    // ---- 文件级容错与发现 ----

    [Fact]
    public async Task Malformed_thread_files_are_counted_and_do_not_stop_the_scan()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-bad.json", "{not json");
        WriteThread(root, "T-array.json", "[1,2,3]");
        WriteThread(root, "T-no-id.json", """{"messages":[]}""");
        WriteThread(root, "T-ok.json",
            """{"id":"ok","usageLedger":{"events":[{"timestamp":"2026-01-02T00:00:00.000Z","model":"gpt-5","tokens":{"input":3,"output":4}}]}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.MalformedLines.Should().Be(2, "坏 JSON 与顶层数组各计 1（仓库口径显式计数）");
        result.Diagnostics.InvalidRecords.Should().Be(1, "有效 JSON 缺 thread id 计 invalid");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Records_are_sorted_by_timestamp_across_thread_files()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-later.json",
            """{"id":"later","usageLedger":{"events":[{"timestamp":"2026-01-20T10:00:00.000Z","model":"gpt-5","tokens":{"input":20,"output":2}}]}}""");
        WriteThread(root, "T-earlier.json",
            """{"id":"earlier","usageLedger":{"events":[{"timestamp":"2026-01-19T10:00:00.000Z","model":"gpt-5","tokens":{"input":10,"output":1}}]}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Select(record => record.InputTokens).Should().ContainInOrder(10, 20);
    }

    [Fact]
    public async Task Prices_records_via_builtin_snapshot()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-priced.json",
            """{"id":"priced","usageLedger":{"events":[{"timestamp":"2026-01-19T10:00:00.000Z","model":"gpt-5","tokens":{"input":10000,"output":1000,"total":11000}}]}}""");

        var result = await CreateAdapter(root).ScanAsync();
        var engine = new PricingEngine();

        var priced = engine.PriceRecord(result.Records.Should().ContainSingle().Subject);
        priced.Source.Should().Be(PricingSourceKind.Snapshot);
        // 10000×$1.25/M + 1000×$10/M = $0.0125 + $0.01 = $0.0225
        priced.CostUsd.Should().Be(0.0225m);
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    // ---- 目录发现与环境变量（蓝本 paths.rs 语义） ----

    [Fact]
    public async Task Collects_json_thread_files_under_threads_subdirectory_only()
    {
        var root = CreateTempDataDir();
        WriteThread(root, "T-a.json", """{"id":"a","usageLedger":{"events":[{"timestamp":"2026-01-19T10:00:00.000Z","model":"gpt-5","tokens":{"input":1,"output":1}}]}}""");
        File.WriteAllText(Path.Combine(root, "threads", "notes.txt"), "no");
        File.WriteAllText(Path.Combine(root, "outside.json"), "no");
        Directory.CreateDirectory(Path.Combine(root, "threads", "nested"));
        File.WriteAllText(Path.Combine(root, "threads", "nested", "T-nested.json"),
            """{"id":"nested","usageLedger":{"events":[{"timestamp":"2026-01-19T10:00:00.000Z","model":"gpt-5","tokens":{"input":2,"output":2}}]}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "threads 子树递归发现（含嵌套目录）");
        result.Diagnostics.FilesDiscovered.Should().Be(2, "threads 外的 json 与非 json 文件不发现");
    }

    [Fact]
    public async Task Amp_data_dir_environment_variable_is_respected_without_default_fallback()
    {
        var relocated = CreateTempDataDir();
        WriteThread(relocated, "T-relocated.json",
            """{"id":"relocated","usageLedger":{"events":[{"timestamp":"2026-01-19T10:00:00.000Z","model":"gpt-5","tokens":{"input":5,"output":1}}]}}""");

        try
        {
            Environment.SetEnvironmentVariable(AmpLogLocator.AmpDataDirEnvVariable, relocated);
            var result = await new AmpAdapter().ScanAsync();

            result.Records.Should().ContainSingle();
            result.Records[0].SessionId.Should().Be("relocated");
            result.Roots.Should().ContainSingle().Which.Should().Be(relocated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AmpLogLocator.AmpDataDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Set_but_empty_environment_variable_yields_no_data_without_fallback()
    {
        var defaultRoot = CreateTempDataDir();
        WriteThread(defaultRoot, "T-default.json",
            """{"id":"default","usageLedger":{"events":[{"timestamp":"2026-01-19T10:00:00.000Z","model":"gpt-5","tokens":{"input":5,"output":1}}]}}""");

        try
        {
            Environment.SetEnvironmentVariable(AmpLogLocator.AmpDataDirEnvVariable, "");
            var result = await new AmpAdapter().ScanAsync();

            result.Records.Should().BeEmpty();
            result.Roots.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AmpLogLocator.AmpDataDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Missing_configured_directory_yields_no_data()
    {
        var root = CreateTempDir();
        var missing = Path.Combine(root, "amp-missing");

        var result = await CreateAdapter(missing).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Roots.Should().ContainSingle().Which.Should().Be(missing);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    // ---- helpers ----

    private AmpAdapter CreateAdapter(string dataDir) => new(configuredDataDir: dataDir);

    private string CreateTempDataDir()
    {
        var path = Path.Combine(CreateTempDir(), "amp");
        Directory.CreateDirectory(Path.Combine(path, "threads"));
        return path;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-amp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static void WriteThread(string dataDir, string fileName, string content)
        => File.WriteAllText(Path.Combine(dataDir, "threads", fileName), content);

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
