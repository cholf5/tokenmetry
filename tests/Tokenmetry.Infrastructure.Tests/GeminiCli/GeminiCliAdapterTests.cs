namespace Tokenmetry.Infrastructure.Tests.GeminiCli;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.GeminiCli;
using Tokenmetry.Infrastructure.JsonSource;

/// <summary>
/// I-010: Gemini CLI 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage rust/adapters/gemini 的 parser.rs / loader.rs / paths.rs，含蓝本内联测试的同名场景）。
/// </summary>
public class GeminiCliAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- 目录发现（蓝本 paths.rs 内联测试同名场景） ----

    [Fact]
    public async Task Discovers_json_and_jsonl_logs_only()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/a.json", "{}");
        WriteFile(root, "chats/b.jsonl", "{}\n");
        WriteFile(root, "chats/ignore.txt", "no");

        var result = await CreateAdapter(root).ScanAsync();

        result.Diagnostics.FilesDiscovered.Should().Be(2);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData, "空对象文件不产出记录");
        result.Roots.Should().ContainSingle().Which.Should().Be(root);
    }

    [Fact]
    public async Task Gemini_data_dir_environment_variable_is_respected_without_default_fallback()
    {
        // 蓝本 paths.rs：环境变量已设置即只用配置（目录逐个存在性过滤），默认 ~/.gemini/tmp 不再并入。
        var defaultRoot = CreateTempDir();
        WriteFile(defaultRoot, ".gemini/tmp/default.json", "{}");
        var relocated = CreateTempDir();
        // 行内不带 sessionId：记录会话 id 回退文件名（蓝本 parse_jsonl_file 初始 session_id = file stem）。
        WriteFile(relocated, "chats/relocated.jsonl",
            """{"type":"gemini","timestamp":"2026-05-17T11:07:32.000Z","model":"gemini-2.5-pro","tokens":{"input":10,"output":1}}""");

        try
        {
            Environment.SetEnvironmentVariable(GeminiLogLocator.GeminiDataDirEnvVariable, relocated);
            var result = await new GeminiCliAdapter().ScanAsync();

            result.Records.Should().ContainSingle();
            result.Records[0].SessionId.Should().Be("relocated");
            result.Roots.Should().ContainSingle().Which.Should().Be(relocated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GeminiLogLocator.GeminiDataDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Set_but_empty_environment_variable_yields_no_data_without_fallback()
    {
        // 蓝本 env::var 对空串返回 Ok：配置生效（分割后无有效目录），不回退默认目录。
        var defaultRoot = CreateTempDir();
        WriteFile(defaultRoot, ".gemini/tmp/default.jsonl",
            GeminiLine("msg-1", "2026-05-17T11:07:32.000Z", model: "m", input: 10, output: 1));

        try
        {
            Environment.SetEnvironmentVariable(GeminiLogLocator.GeminiDataDirEnvVariable, "");
            var result = await new GeminiCliAdapter().ScanAsync();

            result.Records.Should().BeEmpty();
            result.Roots.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(GeminiLogLocator.GeminiDataDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Antigravity_data_dir_does_not_leak_into_gemini_discovery()
    {
        // 蓝本 antigravity_override_does_not_replace_gemini_discovery：GEMINI_DATA_DIR 与
        // ANTIGRAVITY_DATA_DIR 并存时互不影响，Gemini 只扫自己的根（SQLite 数据归 I-021）。
        var root = CreateTempDir();
        WriteFile(root, "gemini/chats/a.json",
            """{"sessionId":"a","messages":[{"type":"gemini","model":"m","tokens":{"input":5,"output":1}}]}""");
        WriteFile(root, "antigravity/conversations/a.db", "not a Gemini log");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].SessionId.Should().Be("a");
    }

    // ---- JSONL 直接事件（蓝本 loader.rs loads_jsonl_token_events_and_separates_cached_input） ----

    [Fact]
    public async Task Loads_jsonl_token_events_and_separates_cached_input()
    {
        // 蓝本旗舰场景：total == 含 cached 的四桶和 → cached 从 input 拆出（normalize_session_input）；
        // thoughts 计入 extra（桌面统一记录并入 OutputTokens，见卡片偏差②）。
        var root = CreateTempDir();
        WriteFile(root, "project/chats/session-a.jsonl",
            """
            {"sessionId":"session-a","projectHash":"project-a","startTime":"2026-05-17T11:07:00.000Z"}
            {"id":"msg-a","timestamp":"2026-05-17T11:07:32.000Z","type":"gemini","model":"gemini-3-flash-preview","tokens":{"input":15327,"output":23,"cached":11526,"thoughts":919,"tool":7,"total":16276}}
            """);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Agent.Should().Be("Gemini CLI");
        record.ProjectPath.Should().Be("Gemini");
        record.SessionId.Should().Be("session-a");
        record.Model.Should().Be("gemini-3-flash-preview");
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 17, 11, 7, 32, TimeSpan.Zero));
        record.InputTokens.Should().Be(3_808, "15327 − min(15327, 11526) + tool 7");
        record.OutputTokens.Should().Be(23 + 919, "display output 23 + thoughts 919（并入 OutputTokens）");
        record.CacheReadTokens.Should().Be(11_526);
        record.CacheCreationTokens.Should().Be(0);
        record.TotalTokens.Should().Be(16_276, "四桶之和与蓝本 totalTokens（display+extra）一致");
        record.CostUsd.Should().BeNull("成本由定价引擎计算");

        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        result.Diagnostics.FilesRead.Should().Be(1);
        result.Diagnostics.LinesSkipped.Should().Be(1, "meta 行无 type/stats 不产事件，按仓库口径计 skipped（蓝本静默）");
        result.Diagnostics.DuplicateRecords.Should().Be(0);
    }

    [Fact]
    public async Task Jsonl_session_and_model_carry_over_across_lines()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/session-x.jsonl",
            """
            {"sessionId":"carried-session"}
            {"model":"gemini-2.5-pro"}
            {"type":"gemini","timestamp":"2026-05-17T11:07:32.000Z","tokens":{"input":100,"output":10}}
            """);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SessionId.Should().Be("carried-session", "sessionId 跨行滚动");
        record.Model.Should().Be("gemini-2.5-pro", "current_model 跨行滚动（缺省回退文件名）");
    }

    [Fact]
    public async Task Jsonl_event_id_is_replaced_in_place_by_later_occurrence()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/session-x.jsonl",
            """
            {"sessionId":"s","type":"gemini","id":"msg-1","timestamp":"2026-05-17T11:00:00.000Z","model":"m","tokens":{"input":10,"output":1}}
            {"sessionId":"s","type":"gemini","id":"msg-1","timestamp":"2026-05-17T11:05:00.000Z","model":"m","tokens":{"input":20,"output":2}}
            {"sessionId":"s","type":"gemini","id":"msg-2","timestamp":"2026-05-17T11:06:00.000Z","model":"m","tokens":{"input":30,"output":3}}
            """);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "同 id 事件就地替换（蓝本 direct_event_indexes）");
        result.Records[0].InputTokens.Should().Be(20, "后行覆盖前行");
        result.Records[0].Timestamp.Should().Be(new DateTimeOffset(2026, 5, 17, 11, 5, 0, TimeSpan.Zero));
        result.Records[1].InputTokens.Should().Be(30);
        result.Diagnostics.DuplicateRecords.Should().Be(1, "id 替换按重复折叠计数");
    }

    // ---- total 兜底（蓝本 parser.rs falls_back_to_total_tokens_when_gemini_parts_are_missing） ----

    [Fact]
    public async Task Falls_back_to_total_tokens_when_gemini_parts_are_missing()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/session-t.jsonl",
            """{"sessionId":"s","type":"gemini","timestamp":"2026-05-17T11:00:00.000Z","model":"m","tokens":{"total":654}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(654, "output 为 0 时 total 缺口补入 output（蓝本 apply_total_token_fallback）");
        record.InputTokens.Should().Be(0);
    }

    [Fact]
    public async Task Total_fallback_keeps_extra_when_output_is_known()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/session-t.jsonl",
            """{"sessionId":"s","type":"gemini","timestamp":"2026-05-17T11:00:00.000Z","model":"m","tokens":{"input":100,"output":50,"total":175}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(75, "output 已知时缺口并入 reasoning（50 + 25）");
        record.TotalTokens.Should().Be(175);
    }

    [Fact]
    public async Task All_zero_events_are_dropped()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/session-t.jsonl",
            """{"sessionId":"s","type":"gemini","timestamp":"2026-05-17T11:00:00.000Z","tokens":{"input":0,"output":0}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.LinesSkipped.Should().Be(1);
    }

    // ---- type 判别（蓝本 parser.rs type_discriminator_is_untrimmed_and_tolerates_non_strings） ----

    [Fact]
    public async Task Type_discriminator_is_untrimmed_and_tolerates_non_strings()
    {
        // " gemini "（带空白）不得命中直接事件分支；type 为数字时整行走 stats 分支。
        var root = CreateTempDir();
        WriteFile(root, "chats/padded.json",
            """{"sessionId":"s","type":" gemini ","timestamp":"2026-05-17T11:00:00.000Z","stats":{"input":10,"output":5,"total":15}}""");
        WriteFile(root, "chats/numeric.json",
            """{"sessionId":"s","type":5,"timestamp":"2026-05-17T11:00:00.000Z","stats":{"input":7,"output":2,"total":9}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "两个文件都走 stats 分支（各自产出 unknown 模型事件）");
        result.Records.Should().OnlyContain(record => record.Model == "unknown", "stats 兜底模型名");
        // 文件按路径序（numeric < padded）收集、同时间戳稳定排序：numeric.json（input 7）在前。
        result.Records[0].InputTokens.Should().Be(7);
        result.Records[1].InputTokens.Should().Be(10);
    }

    // ---- 整文件 JSON：messages 数组路径 ----

    [Fact]
    public async Task Parses_messages_array_with_session_level_timestamp_fallback()
    {
        var root = CreateTempDir();
        WriteFile(root, "project/chats/session-a.json",
            """
            {
              "sessionId": "session-a",
              "startTime": "2026-05-17T11:07:00.000Z",
              "messages": [
                {"type": "user", "content": "hello"},
                {"id": "msg-a1", "timestamp": "2026-05-17T11:07:32.000Z", "type": "gemini", "model": "gemini-2.5-pro",
                 "tokens": {"input": 1000, "output": 20, "cached": 500, "thoughts": 0, "tool": 5, "total": 1025}},
                {"id": "msg-a2", "type": "gemini", "model": "gemini-2.5-flash",
                 "tokens": {"input": 150, "output": 10}}
              ]
            }
            """);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "user 消息跳过，两条 gemini 消息出账");
        var first = result.Records.Single(record => record.Model == "gemini-2.5-pro");
        first.InputTokens.Should().Be(505, "total==含 cached 的四桶和 → cached 拆出：1000−500 + tool 5");
        first.CacheReadTokens.Should().Be(500);
        first.OutputTokens.Should().Be(20);
        var second = result.Records.Single(record => record.Model == "gemini-2.5-flash");
        second.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 17, 11, 7, 0, TimeSpan.Zero),
            "消息无 timestamp 时回退会话级 startTime（蓝本 session_timestamp）");
        second.InputTokens.Should().Be(150);
    }

    // ---- 整文件 JSON：stats 路径 ----

    [Fact]
    public async Task Parses_stats_models_breakdown_with_overlap_subtraction()
    {
        var root = CreateTempDir();
        WriteFile(root, "project/chats/session-c.json",
            """
            {
              "sessionId": "session-c",
              "timestamp": "2026-05-18T09:30:00.000Z",
              "stats": {
                "models": {
                  "gemini-2.5-pro": {"tokens": {"input": 2000, "output": 100, "cached": 400, "total": 2500}},
                  "gemini-2.5-flash": {"tokens": {"input": 500, "output": 30, "total": 530}}
                }
              }
            }
            """);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        var pro = result.Records.Single(record => record.Model == "gemini-2.5-pro");
        pro.InputTokens.Should().Be(1_600, "stats 路径恒 subtract_cached_overlap：2000−400");
        pro.CacheReadTokens.Should().Be(400);
        pro.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 18, 9, 30, 0, TimeSpan.Zero));
        // 蓝本统计口径：stats 的 total（2500）按原 input 计，扣减后四桶和 2100 不足 total，缺口 400
        // 并入 extra（reasoning 展示余量）——桌面并入 OutputTokens（偏差②），totalTokens 恒 2500。
        pro.OutputTokens.Should().Be(100 + 400);
        pro.TotalTokens.Should().Be(2_500);
        var flash = result.Records.Single(record => record.Model == "gemini-2.5-flash");
        flash.InputTokens.Should().Be(500);
    }

    [Fact]
    public async Task Stats_without_models_falls_back_to_whole_block_with_unknown_model()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/session-d.json",
            """{"model":"gemini-2.5-pro","timestamp":"2026-05-18T10:00:00.000Z","stats":{"input":300,"output":40,"cached":100,"total":440}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("gemini-2.5-pro", "无 models 明细时用顶层 model 提示");
        record.InputTokens.Should().Be(200, "300−100（subtract_cached_overlap）");
        record.CacheReadTokens.Should().Be(100);
        // 蓝本统计口径：stats total（440）按原 input 计，扣减后四桶和 340 不足 total，缺口 100 并入
        // extra——桌面并入 OutputTokens（偏差②），totalTokens 恒 440。
        record.OutputTokens.Should().Be(40 + 100);
        record.TotalTokens.Should().Be(440);
    }

    [Fact]
    public async Task Result_nested_stats_is_used_when_top_level_missing()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/session-e.json",
            """{"sessionId":"s","result":{"stats":{"input":60,"output":6,"total":66}},"timestamp":"2026-05-18T10:00:00.000Z"}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].InputTokens.Should().Be(60);
        result.Records[0].Model.Should().Be("unknown");
    }

    // ---- stats:null 语义（I-010 审查修复：顶层 stats 为 serde Option<Value>，null → None → 回退 result.stats） ----

    [Fact]
    public async Task Null_top_level_stats_falls_back_to_result_stats()
    {
        // 蓝本 serde Option<Value>："stats": null 反序列化为 None → 回退 result.stats（与"键存在即生效"相反）。
        var root = CreateTempDir();
        WriteFile(root, "chats/session-null.json",
            """{"sessionId":"s","stats":null,"result":{"stats":{"input":300,"output":25,"cached":100}},"timestamp":"2026-05-18T10:00:00.000Z"}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(200, "用 result.stats：300−100（overlap 扣减）");
        record.CacheReadTokens.Should().Be(100);
        record.OutputTokens.Should().Be(25);
    }

    [Fact]
    public async Task Missing_stats_with_result_stats_uses_nested_block()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/session-absent.json",
            """{"sessionId":"s","result":{"stats":{"input":60,"output":6}},"timestamp":"2026-05-18T10:00:00.000Z"}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].InputTokens.Should().Be(60);
    }

    [Fact]
    public async Task Neither_stats_nor_result_stats_yields_no_events()
    {
        // 两者皆无（或 result.stats 亦为 null——Value::get 返回 Some(&Null)，解析为空、不再二次回退）：
        // 蓝本 parse_stats_events(None) → 空，不产出记录。
        var root = CreateTempDir();
        WriteFile(root, "chats/session-neither.json",
            """{"sessionId":"s","stats":null,"result":{"stats":null},"timestamp":"2026-05-18T10:00:00.000Z"}""");
        WriteFile(root, "chats/session-none.json",
            """{"sessionId":"s","timestamp":"2026-05-18T10:00:00.000Z"}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.LinesSkipped.Should().Be(2, "两个文件均无可用 stats（仓库口径显式计数）");
    }

    // ---- 容错与诊断 ----

    [Fact]
    public async Task Malformed_files_and_lines_are_counted_and_do_not_stop_the_scan()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/bad.json", "{not json");
        WriteFile(root, "chats/array.json", "[1,2,3]");
        WriteFile(root, "chats/mixed.jsonl",
            "not json\n" + GeminiLine("msg-ok", "2026-05-17T11:07:32.000Z", model: "m", input: 10, output: 1) + "\n");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.MalformedLines.Should().Be(3, "坏 .json 整文件、顶层数组、坏 JSONL 行各计 1（仓库口径显式计数）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Records_are_sorted_by_timestamp_across_files()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/later.jsonl",
            GeminiLine("msg-2", "2026-05-18T10:00:00.000Z", model: "m", input: 20, output: 2));
        WriteFile(root, "chats/earlier.jsonl",
            GeminiLine("msg-1", "2026-05-17T10:00:00.000Z", model: "m", input: 10, output: 1));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Select(record => record.InputTokens).Should().ContainInOrder(10, 20);
    }

    // ---- 定价（内置快照新增 google 段） ----

    [Fact]
    public async Task Prices_records_via_builtin_snapshot_google_section()
    {
        var root = CreateTempDir();
        WriteFile(root, "chats/priced.jsonl",
            GeminiLine("msg-1", "2026-05-17T11:07:32.000Z", model: "gemini-2.5-pro", input: 10_000, output: 1_000, cached: 0)
            + "\n" + GeminiLine("msg-2", "2026-05-17T12:07:32.000Z", model: "gemini-2.5-flash", input: 5_000, output: 500, cached: 0));

        var result = await CreateAdapter(root).ScanAsync();
        var engine = new PricingEngine();
        var pro = result.Records.Single(record => record.Model == "gemini-2.5-pro");
        var proPriced = engine.PriceRecord(pro);

        proPriced.Source.Should().Be(PricingSourceKind.Snapshot);
        // 10000×$1.25/M + 1000×$10/M + 0 = $0.0125 + $0.01 = $0.0225
        proPriced.CostUsd.Should().Be(0.0225m);
        engine.GetUnpricedModels().Should().BeEmpty();

        // 5000×$0.3/M + 500×$2.5/M = $0.0015 + $0.00125 = $0.00275
        engine.PriceRecord(result.Records.Single(record => record.Model == "gemini-2.5-flash")).CostUsd.Should().Be(0.00275m);
    }

    // ---- 缺省目录 ----

    [Fact]
    public async Task Missing_configured_directory_yields_no_data()
    {
        var root = CreateTempDir();
        var missing = Path.Combine(root, "gemini-missing");

        var result = await CreateAdapter(missing).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Roots.Should().ContainSingle().Which.Should().Be(missing);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
        result.Diagnostics.FilesDiscovered.Should().Be(0);
    }

    // ---- helpers ----

    private static string GeminiLine(
        string id, string timestamp, string model, long input, long output, long cached = 0)
        => "{\"sessionId\":\"s\",\"id\":\"" + id + "\",\"timestamp\":\"" + timestamp
           + "\",\"type\":\"gemini\",\"model\":\"" + model
           + "\",\"tokens\":{\"input\":" + input + ",\"output\":" + output + ",\"cached\":" + cached
           + ",\"thoughts\":0,\"tool\":0,\"total\":" + (input + output + cached) + "}}";

    // ---- I-042 延伸：文件戳探针 + 零变更轮结果记忆化（与 IncrementalJsonlScanner 同构） ----

    private const string SessionAOneEvent =
        """
        {"sessionId":"session-a","projectHash":"project-a","startTime":"2026-05-17T11:07:00.000Z"}
        {"id":"msg-a","timestamp":"2026-05-17T11:07:32.000Z","type":"gemini","model":"gemini-3-flash-preview","tokens":{"input":15327,"output":23,"cached":11526,"thoughts":919,"tool":7,"total":16276}}
        """;

    private const string SessionATwoEvents =
        """
        {"sessionId":"session-a","projectHash":"project-a","startTime":"2026-05-17T11:07:00.000Z"}
        {"id":"msg-a","timestamp":"2026-05-17T11:07:32.000Z","type":"gemini","model":"gemini-3-flash-preview","tokens":{"input":15327,"output":23,"cached":11526,"thoughts":919,"tool":7,"total":16276}}
        {"id":"msg-b","timestamp":"2026-05-17T11:08:32.000Z","type":"gemini","model":"gemini-3-flash-preview","tokens":{"input":10,"output":20,"total":30}}
        """;

    [Fact]
    public async Task ChangeProbe_UnchangedSecondScan_SkipsRereadAndReusesCachedRecords()
    {
        var root = CreateTempDir();
        WriteFile(root, "project/chats/session-a.jsonl", SessionAOneEvent);
        var adapter = CreateAdapter(root);

        var first = await adapter.ScanAsync();
        first.Diagnostics.FilesRead.Should().Be(1, "首轮无记忆，全量读取");
        first.Diagnostics.FilesChanged.Should().Be(1);

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesChanged.Should().Be(0, "文件戳（mtime + 大小）未动");
        second.Diagnostics.FilesRead.Should().Be(0, "零变更轮零读取：记录复用上轮结果");
        second.Records.Should().ContainSingle("记录集来自上轮真实读取的复用，注册表累计口径不变");
        second.Records[0].SessionId.Should().Be("session-a");
        second.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        second.Diagnostics.FilesDiscovered.Should().Be(1);
    }

    [Fact]
    public async Task ChangeProbe_AppendedLogContent_DetectedAsChange()
    {
        var root = CreateTempDir();
        WriteFile(root, "project/chats/session-a.jsonl", SessionAOneEvent);
        var adapter = CreateAdapter(root);
        (await adapter.ScanAsync()).Records.Should().ContainSingle();

        WriteFile(root, "project/chats/session-a.jsonl", SessionATwoEvents);

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesChanged.Should().Be(1, "文件内容变化（大小 + mtime）");
        second.Records.Should().HaveCount(2);
    }

    [Fact]
    public async Task ChangeProbe_RemovedAndAddedLogs_TriggerFullRescan()
    {
        var root = CreateTempDir();
        WriteFile(root, "project/chats/session-a.jsonl", SessionAOneEvent);
        WriteFile(root, "project/chats/session-b.jsonl",
            """
            {"sessionId":"session-b","projectHash":"project-a","startTime":"2026-05-17T12:00:00.000Z"}
            {"id":"msg-b","timestamp":"2026-05-17T12:00:32.000Z","type":"gemini","model":"gemini-3-flash-preview","tokens":{"input":10,"output":20,"total":30}}
            """);
        var adapter = CreateAdapter(root);
        (await adapter.ScanAsync()).Records.Should().HaveCount(2);

        File.Delete(Path.Combine(root, "project", "chats", "session-b.jsonl"));
        WriteFile(root, "project/chats/session-c.jsonl",
            """
            {"sessionId":"session-c","projectHash":"project-a","startTime":"2026-05-17T13:00:00.000Z"}
            {"id":"msg-c","timestamp":"2026-05-17T13:00:32.000Z","type":"gemini","model":"gemini-3-flash-preview","tokens":{"input":10,"output":20,"total":30}}
            """);

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesDiscovered.Should().Be(2);
        second.Diagnostics.FilesChanged.Should().Be(1, "文件集变化 = 账面变化");
        second.Records.Select(record => record.SessionId).Should().BeEquivalentTo("session-a", "session-c");
    }

    private GeminiCliAdapter CreateAdapter(string dataDir) => new(configuredDataDir: dataDir);

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-gemini-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static void WriteFile(string root, string relativePath, string content)
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
