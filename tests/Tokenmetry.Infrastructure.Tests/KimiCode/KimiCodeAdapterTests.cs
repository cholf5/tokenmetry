namespace Tokenmetry.Infrastructure.Tests.KimiCode;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.KimiCode;

/// <summary>
/// I-013: Kimi Code 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage rust/adapters/kimi 的 parser.rs / loader.rs / paths.rs，含蓝本内联测试的同名场景）。
/// </summary>
public class KimiCodeAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- 旧格式（蓝本 loader.rs loads_status_update_token_usage_from_wire_files） ----

    [Fact]
    public async Task Loads_status_update_token_usage_from_wire_files()
    {
        var root = CreateTempDataDir();
        File.WriteAllText(Path.Combine(root, "config.json"), """{"model":"kimi-k2"}""");
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            """{"type":"metadata","protocol_version":"1.3"}""",
            """{"timestamp":1770983426.420942,"message":{"type":"TurnBegin","payload":{"user_input":"hello"}}}""",
            """{"timestamp":1770983427.123,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":100,"output":50,"input_cache_read":10,"input_cache_creation":20},"message_id":"msg-1"}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Agent.Should().Be("Kimi");
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 2, 13, 11, 50, 27, 123, TimeSpan.Zero),
            "秒级时间戳截断毫秒（1770983427.123 → 1770983427123）");
        record.SessionId.Should().Be("session-a", "旧布局取 wire 所在目录名");
        record.ProjectPath.Should().Be("Kimi", "蓝本 LoadedEntry.project_path");
        record.Model.Should().Be("kimi-k2", "显示模型来自根目录 config.json");
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(50);
        record.CacheCreationTokens.Should().Be(20);
        record.CacheReadTokens.Should().Be(10);
        record.MessageId.Should().Be("msg-1", "旧格式 message_id 记录在统一记录上（仅作内容键分量）");
        record.CostUsd.Should().BeNull("成本恒 null 交定价引擎");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        result.Diagnostics.LinesSkipped.Should().Be(2, "metadata 与 TurnBegin 行无预过滤标记");
    }

    // ---- 新格式（蓝本 loader.rs loads_kimi_code_usage_record_format） ----

    [Fact]
    public async Task Loads_kimi_code_usage_record_format()
    {
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/workspace/session-b/agents/agent-1/wire.jsonl",
            """{"type":"usage.record","model":"kimi-code/kimi-for-coding","usage":{"inputOther":3064,"output":76,"inputCacheRead":14848,"inputCacheCreation":0},"usageScope":"turn","time":1782113184943}""",
            """{"type":"usage.record","model":"kimi-code/kimi-for-coding","usage":{"inputOther":5000,"output":200,"inputCacheRead":20000,"inputCacheCreation":100},"usageScope":"session","time":1782113185000}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SessionId.Should().Be("session-b", "agents 布局取 session 目录名");
        record.Model.Should().Be("kimi-for-coding", "剥离 kimi-code/ 前缀");
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 6, 22, 7, 26, 24, 943, TimeSpan.Zero));
        record.InputTokens.Should().Be(3064);
        record.OutputTokens.Should().Be(76);
        record.CacheReadTokens.Should().Be(14848);
        record.CacheCreationTokens.Should().Be(0);
        record.MessageId.Should().BeNull("新格式无 message id");
        result.Diagnostics.InvalidRecords.Should().Be(1, "session 级记录（累计总量）跳过");
    }

    [Fact]
    public async Task Strips_kimi_code_prefix_from_real_model_names()
    {
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/ws/session-c/agents/agent-1/wire.jsonl",
            """{"type":"usage.record","model":"kimi-code/kimi-k2.5","usage":{"inputOther":10,"output":5},"usageScope":"turn","time":1782113184943}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Model.Should().Be("kimi-k2.5");
    }

    // ---- total 兜底（蓝本 parser.rs falls_back_to_total_tokens_when_kimi_parts_are_missing） ----

    [Fact]
    public async Task Falls_back_to_total_tokens_when_kimi_parts_are_missing()
    {
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            """{"timestamp":1770983427.123,"message":{"type":"StatusUpdate","payload":{"token_usage":{"total":432}}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(432, "output 为 0 时 total 缺口补入 output");
        record.InputTokens.Should().Be(0);
    }

    // ---- 拒绝与坏行（蓝本 loader.rs skips_malformed_and_zero_token_wire_lines） ----

    [Fact]
    public async Task Skips_malformed_and_zero_token_wire_lines()
    {
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            "not json",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":0,"output":0,"input_cache_read":0,"input_cache_creation":0}}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.LinesSkipped.Should().Be(1, "not json 无预过滤标记");
        result.Diagnostics.InvalidRecords.Should().Be(1, "全零 token_usage 行拒绝");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Structural_rejections_are_counted_and_do_not_stop_the_scan()
    {
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":10,"output":5},"message_id":"ok"}}}""",
            """{"type":"usage.record","usageScope":"turn","usage":5,"note":"\"usage.record\" marker"}""",
            """{"timestamp":1770983427,"message":"oops not an object","token_usage":true}""",
            """[{"token_usage":{}}]""",
            """{"type":"usage.record","usageScope":"turn","usage":{"inputOther":20,"output":6},"time":1770983427000}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Diagnostics.MalformedLines.Should().Be(0, "全部行都是合法 JSON");
        result.Diagnostics.InvalidRecords.Should().Be(3, "usage 非对象 + message 非对象 + 顶层非对象");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Bad_json_lines_are_counted_as_malformed()
    {
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            "{\"token_usage\": broken",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":10,"output":5}}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.MalformedLines.Should().Be(1, "含标记的坏 JSON 行计 1");
    }

    // ---- 去重（蓝本 kimi_entry_key first-wins） ----

    [Fact]
    public async Task Entry_key_dedupes_same_message_id_first_wins()
    {
        var root = CreateTempDataDir();
        var statusLine = """{"timestamp":1770983427.123,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":100,"output":50,"input_cache_read":10,"input_cache_creation":20},"message_id":"msg-1"}}}""";
        WriteWire(root, "sessions/group/session-a/wire.jsonl", statusLine, statusLine);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    // ---- 路径推导（蓝本 parser.rs kimi_root_resolves_correctly_for_both_path_layouts） ----

    [Fact]
    public void Root_and_session_resolve_correctly_for_both_path_layouts()
    {
        var root = CreateTempDataDir();
        var oldPath = Path.Combine(root, "sessions", "group", "session-a", "wire.jsonl");
        var newPath = Path.Combine(root, "sessions", "ws", "session-b", "agents", "agent-1", "wire.jsonl");

        KimiLogLocator.RootFromWirePath(oldPath).Should().Be(root,
            "旧布局：自 wire 文件 4 次 GetDirectoryName（自 wire 所在目录 3 级）到根");
        KimiLogLocator.RootFromWirePath(newPath).Should().Be(root,
            "agents 布局：自 wire 所在目录 5 级（自 wire 文件 6 次 GetDirectoryName）到根");
        KimiLogLocator.SessionIdFromWirePath(oldPath).Should().Be("session-a");
        KimiLogLocator.SessionIdFromWirePath(newPath).Should().Be("session-b");
        KimiLogLocator.SessionIdFromWirePath("/").Should().Be("unknown");
    }

    // ---- 时间戳回退（蓝本 file_modified_timestamp → mtime / 纪元） ----

    [Fact]
    public async Task Missing_timestamp_falls_back_to_file_mtime()
    {
        var root = CreateTempDataDir();
        var file = Path.Combine(root, "sessions", "group", "session-a", "wire.jsonl");
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            """{"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":10,"output":5}}}}""");
        var mtime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, mtime);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should()
            .Be(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), "行内 timestamp 缺失回退文件 mtime");
    }

    // ---- config.json 模型回退（蓝本 read_model_from_config） ----

    [Fact]
    public async Task Missing_or_broken_config_falls_back_to_default_model()
    {
        var root = CreateTempDataDir();
        File.WriteAllText(Path.Combine(root, "config.json"), "{not json");
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":10,"output":5}}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Model.Should().Be("kimi-for-coding", "config 损坏回退缺省模型");
    }

    [Fact]
    public async Task Missing_config_file_falls_back_to_default_model()
    {
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":10,"output":5}}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Model.Should().Be("kimi-for-coding");
    }

    // ---- 目录发现（蓝本 paths.rs 内联测试） ----

    [Fact]
    public async Task Discovers_wire_jsonl_files_under_sessions_group_session()
    {
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/group/session/wire.jsonl", "{}\n");
        WriteWire(root, "sessions/group/session/other.jsonl", "{}\n");
        WriteWire(root, "sessions/nested/path/session/wire.jsonl", "{}\n");

        var result = await CreateAdapter(root).ScanAsync();

        result.Diagnostics.FilesDiscovered.Should().Be(1, "仅 3 段 wire.jsonl 被发现（other.jsonl 非 wire、4 段不合规）");
    }

    [Fact]
    public async Task Discovers_both_old_and_new_layouts_and_skips_non_wire_files()
    {
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/ws/session-c/agents/agent-1/wire.jsonl",
            """{"type":"usage.record","usageScope":"turn","usage":{"inputOther":10,"output":5},"time":1770983427000}""");
        WriteWire(root, "sessions/ws/session-c/agents/agent-1/other.jsonl", "{}\n");
        WriteWire(root, "sessions/group/session-d/wire.jsonl",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":10,"output":5}}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Diagnostics.FilesDiscovered.Should().Be(2, "旧（3 段）与新（5 段）布局都发现");
        result.Records.Should().HaveCount(2);
        // 记录按时间戳稳定排序（蓝本 sort_by_key；两条同刻保持文件发现序，g < w）。
        result.Records.Select(record => record.SessionId).Should().Equal("session-d", "session-c");
    }

    // ---- 环境变量（蓝本 paths.rs env 分支） ----

    [Fact]
    public async Task Kimi_data_dir_environment_variable_is_respected_without_default_fallback()
    {
        var relocated = CreateTempDataDir();
        WriteWire(relocated, "sessions/group/session-r/wire.jsonl",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":10,"output":5}}}}""");

        try
        {
            Environment.SetEnvironmentVariable(KimiLogLocator.KimiDataDirEnvVariable, relocated);
            var result = await new KimiCodeAdapter().ScanAsync();

            result.Records.Should().ContainSingle();
            result.Records[0].SessionId.Should().Be("session-r");
            result.Roots.Should().ContainSingle().Which.Should().Be(relocated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(KimiLogLocator.KimiDataDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Set_but_empty_environment_variable_yields_no_data_without_dual_default_fallback()
    {
        var defaultRoot = CreateTempDataDir();
        WriteWire(defaultRoot, "sessions/group/session-d/wire.jsonl",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":10,"output":5}}}}""");

        try
        {
            Environment.SetEnvironmentVariable(KimiLogLocator.KimiDataDirEnvVariable, "");
            var result = await new KimiCodeAdapter().ScanAsync();

            result.Records.Should().BeEmpty();
            result.Roots.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(KimiLogLocator.KimiDataDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Missing_configured_directory_yields_no_data()
    {
        var missing = Path.Combine(CreateTempDir(), "kimi-missing");

        var result = await CreateAdapter(missing).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Roots.Should().ContainSingle().Which.Should().Be(missing);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    // ---- 定价（内置快照按裸模型名命中；蓝本 prices_default_kimi_model 的候选计价归 I-024） ----

    [Fact]
    public async Task Prices_records_via_builtin_snapshot_moonshot_section()
    {
        var root = CreateTempDataDir();
        File.WriteAllText(Path.Combine(root, "config.json"), """{"model":"kimi-k2.5"}""");
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":100,"output":50,"input_cache_read":10,"input_cache_creation":20},"message_id":"msg-1"}}}""");

        var result = await CreateAdapter(root).ScanAsync();
        var engine = new PricingEngine();

        var priced = engine.PriceRecord(result.Records.Should().ContainSingle().Subject);
        priced.Source.Should().Be(PricingSourceKind.Snapshot);
        // 100×$0.6/M + 50×$3/M + 20×$0.75/M（input×1.25 推导，与 CLI 同式）+ 10×$0.1/M = 0.000226
        priced.CostUsd.Should().Be(0.000226m);
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    [Fact]
    public async Task Default_display_model_prices_against_the_dated_moonshot_model()
    {
        // 蓝本对缺省模型 kimi-for-coding 按时间戳选 moonshot/kimi-k2.5|k2.6 定价档
        // （calculate_kimi_cost model_candidates）。1770983427 秒早于切点 → k2.5：
        // 100×$0.6/M + 50×$3/M = 0.00021，显示名保持 kimi-for-coding。
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            """{"timestamp":1770983427,"message":{"type":"StatusUpdate","payload":{"token_usage":{"input_other":100,"output":50}}}}""");

        var result = await CreateAdapter(root).ScanAsync();
        var engine = new PricingEngine();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("kimi-for-coding", "蓝本 DEFAULT_MODEL 显示名不变");
        var priced = engine.PriceRecord(record);
        priced.Source.Should().Be(PricingSourceKind.Snapshot);
        priced.CostUsd.Should().Be(0.00021m);
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    [Fact]
    public async Task Kimi_for_coding_prices_against_the_dated_moonshot_model()
    {
        var beforeCutoff = CreateTempDataDir();
        WriteWire(beforeCutoff, "sessions/group/session-a/wire.jsonl",
            """{"type":"usage.record","model":"kimi-for-coding","usage":{"inputOther":1000,"output":100},"usageScope":"turn","time":1770983427123}""");

        var afterCutoff = CreateTempDataDir();
        WriteWire(afterCutoff, "sessions/group/session-a/wire.jsonl",
            """{"type":"usage.record","model":"kimi-for-coding","usage":{"inputOther":1000,"output":100},"usageScope":"turn","time":1782113184943}""");

        var before = (await CreateAdapter(beforeCutoff).ScanAsync()).Records.Single();
        var after = (await CreateAdapter(afterCutoff).ScanAsync()).Records.Single();

        before.Pricing!.ModelCandidates[0].Should().Be("moonshot/kimi-k2.5", "切点之前按 k2.5 计价");
        after.Pricing!.ModelCandidates[0].Should().Be("moonshot/kimi-k2.6", "切点及之后按 k2.6 计价");
        before.Pricing.ModelCandidates.Should().Equal(
            "moonshot/kimi-k2.5", "moonshot/kimi-for-coding", "kimi/kimi-for-coding", "kimi-for-coding");
        before.Model.Should().Be("kimi-for-coding", "展示口径不变");

        var engine = new PricingEngine();
        engine.PriceRecord(before).CostUsd.Should().NotBeNull("候选链第 0 位就该命中内置快照");
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_models_keep_the_provider_candidate_chain()
    {
        var root = CreateTempDataDir();
        WriteWire(root, "sessions/group/session-a/wire.jsonl",
            """{"type":"usage.record","model":"kimi-code/kimi-k2.5","usage":{"inputOther":1000,"output":100},"usageScope":"turn","time":1782113184943}""");

        var record = (await CreateAdapter(root).ScanAsync()).Records.Single();

        record.Pricing!.ModelCandidates.Should().Equal("moonshot/kimi-k2.5", "kimi/kimi-k2.5", "kimi-k2.5");
    }

    // ---- helpers ----

    private KimiCodeAdapter CreateAdapter(string dataDir) => new(configuredDataDir: dataDir);

    private string CreateTempDataDir()
    {
        var path = Path.Combine(CreateTempDir(), "kimi");
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-kimi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static void WriteWire(string root, string relativePath, params string[] lines)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Join("\n", lines) + "\n");
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
