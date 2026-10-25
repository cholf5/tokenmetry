namespace Tokenmetry.Infrastructure.Tests.CopilotSession;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.CopilotSession;

/// <summary>
/// I-008: Copilot CLI session-state 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage rust/adapters/copilot 的 parser.rs parse_session_state_file 与 loader.rs
/// reconcile_session_state_entries，含蓝本内联测试的同名场景）。
/// </summary>
public class CopilotSessionAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- 解析与拆分（蓝本 parser.rs session_state_tests 同名场景） ----

    [Fact]
    public async Task Parses_session_shutdown_model_metrics_with_uncached_split()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-04-15T09:52:27.352Z",
                Model("test-model", input: 100, output: 50, cacheRead: 10, cacheWrite: 20, reasoning: 5)));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Agent.Should().Be("Copilot CLI");
        record.ProjectPath.Should().Be("GitHub Copilot CLI");
        record.SessionId.Should().Be("session-1");
        record.Model.Should().Be("test-model");
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 4, 15, 9, 52, 27, 352, TimeSpan.Zero));
        // uncached input = inputTokens − (cacheRead + cacheWrite) = 100 − 30 = 70；
        // reasoning 已含在 output 内不单列；cacheWrite 落 CacheCreationTokens。
        record.InputTokens.Should().Be(70);
        record.OutputTokens.Should().Be(50);
        record.CacheCreationTokens.Should().Be(20);
        record.CacheReadTokens.Should().Be(10);
        record.TotalTokens.Should().Be(150);
        record.CostUsd.Should().BeNull("成本由定价引擎按区间时间戳计算（CLI cost-at-timestamp 口径）");

        var diagnostics = result.Diagnostics;
        diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        diagnostics.FilesDiscovered.Should().Be(1);
        diagnostics.FilesRead.Should().Be(1);
        diagnostics.LinesSkipped.Should().Be(0);
        diagnostics.MalformedLines.Should().Be(0);
        diagnostics.InvalidRecords.Should().Be(0);
        diagnostics.DuplicateRecords.Should().Be(0);
    }

    [Fact]
    public async Task Reads_events_while_agent_holds_write_handle()
    {
        // B-010（Windows 冒烟后续）：agent 持写句柄追加 events.jsonl 期间采集不静默缺数据。
        // 旧 StreamReader(String)（内部 FileShare.Read）对写句柄即 sharing violation——Unix 空操作恒绿。
        var home = CreateTempHome();
        var path = WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-04-15T09:52:27.352Z",
                Model("test-model", input: 100, output: 50, cacheRead: 10, cacheWrite: 20)));
        using var agentHandle = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.FilesRead.Should().Be(1);
    }

    [Fact]
    public async Task Normalizes_copilot_internal_model_ids()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-04-15T09:52:27.352Z",
                Model(" claude-opus-4.7-1m-internal ", input: 10, output: 1),
                Model("claude-opus-4.6-1m", input: 20, output: 2),
                Model("gpt-5.4", input: 30, output: 3)));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Select(record => record.Model).Should().BeEquivalentTo(
            new[] { "claude-opus-4.7", "claude-opus-4.6", "gpt-5.4" },
            "后缀剥离先 -1m-internal 后 -1m、仅一次，前后空白去除");
    }

    [Fact]
    public async Task Skips_malformed_lines_and_non_shutdown_events()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            // 坏 JSON：须先通过行级预过滤（含 shutdown 标记）才进入解析、计 malformed（蓝本预过滤口径）。
            "{\"type\":\"session.shutdown\",\"timestamp\":\"2026-04-15T09:52:27.352Z",
            """{"type":"tool","data":{"message":"session.shutdown"}}""",
            // 无总额的 checkpoint：积分对账无可消费内容（蓝本"A checkpoint without a total carries
            // nothing"），计 skipped；带总额的 checkpoint 自 I-023 起产出积分快照、不再计 skipped。
            """{"type":"session.usage_checkpoint","id":"cp-1","timestamp":"2026-04-15T09:50:00.000Z","data":{"promptCacheBreakState":[]}}""",
            Shutdown("shutdown-1", "2026-04-15T09:52:27.352Z",
                Model("test-model", input: 1, output: 2)));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(1);
        record.OutputTokens.Should().Be(2);
        var diagnostics = result.Diagnostics;
        // 坏 JSON 计 malformed；普通事件行与无总额 checkpoint 行计 skipped。
        diagnostics.MalformedLines.Should().Be(1);
        diagnostics.LinesSkipped.Should().Be(2);
        diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Parses_each_model_and_ignores_request_cost_and_empty_usage()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-04-15T09:52:27.352Z",
                RawModel("first-model", """{"usage":{"inputTokens":10,"outputTokens":20},"requests":{"count":1,"cost":999}}"""),
                RawModel("empty-model", """{"usage":{"inputTokens":0,"outputTokens":0}}"""),
                RawModel("second-model", """{"usage":{"cacheReadTokens":3,"cacheWriteTokens":4}}""")));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Select(record => record.Model).Should().BeEquivalentTo(
            new[] { "first-model", "second-model" },
            "全零用量且无请求数的条目跳过（蓝本口径）");
        var second = result.Records.Single(record => record.Model == "second-model");
        second.CacheCreationTokens.Should().Be(4);
        second.CacheReadTokens.Should().Be(3);
        second.InputTokens.Should().Be(0, "inputTokens 全为缓存切片时 uncached input 为 0（saturating）");
    }

    [Fact]
    public async Task Skips_lines_without_parseable_shutdown_timestamp_as_invalid()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            """{"type":"session.shutdown","id":"no-time","data":{"modelMetrics":{"m":{"usage":{"inputTokens":5}}}}}""",
            """{"type":"session.shutdown","id":"bad-time","timestamp":"not-a-time","data":{"modelMetrics":{"m":{"usage":{"inputTokens":5}}}}}""",
            Shutdown("shutdown-1", "2026-04-15T09:52:27.352Z", Model("m", input: 5)));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.InvalidRecords.Should().Be(2, "缺时间戳 / 时间戳不可解析的 shutdown 计 invalid（仓库口径）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    // ---- 累计快照差分（蓝本 loader.rs reconcile / 内联测试同名场景） ----

    [Fact]
    public async Task Splits_cumulative_shutdowns_into_intervals_without_double_counting()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-04-15T09:52:27.352Z", Model("test-model", input: 10, output: 20)),
            Shutdown("shutdown-1", "2026-04-15T09:52:27.352Z", Model("test-model", input: 10, output: 20)),
            Shutdown("shutdown-2", "2026-04-15T09:53:27.352Z", Model("test-model", input: 30, output: 40)));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(2, "同 id 重复 shutdown 去重；第二条是差分区间");
        result.Records[0].InputTokens.Should().Be(10, "首条累计快照原样出账");
        result.Records[0].OutputTokens.Should().Be(20);
        result.Records[1].InputTokens.Should().Be(20, "30 − 10（差分）");
        result.Records[1].OutputTokens.Should().Be(20, "40 − 20（差分）");
    }

    [Fact]
    public async Task Keeps_same_event_id_across_different_session_directories()
    {
        // dedup key 形状角落：key 为 shutdown:{session}:{id}:{model}，同 id 快照分属两个会话目录
        // 即不同键，两侧各自保留（文件内差分 == 蓝本池化的等价性依赖此键形状）。
        var home = CreateTempHome();
        WriteEvents(home, "session-a",
            Shutdown("shared-id", "2026-04-15T09:00:00.000Z", Model("m", input: 100, output: 10)));
        WriteEvents(home, "session-b",
            Shutdown("shared-id", "2026-04-15T10:00:00.000Z", Model("m", input: 200, output: 20)));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records.Should().Contain(record => record.SessionId == "session-a" && record.InputTokens == 100);
        result.Records.Should().Contain(record => record.SessionId == "session-b" && record.InputTokens == 200);
    }

    [Fact]
    public async Task Deduplicates_reused_event_id_keeping_latest_timestamp()
    {
        // dedup key 形状角落：同 id 复用于不同时间戳（键相同）只留最新快照（蓝本 <= 判定），
        // 差分基于留下的序列：仅剩 10:00 快照，作为首条原样出账。
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("reused", "2026-04-15T09:00:00.000Z", Model("m", input: 100, output: 10)),
            Shutdown("reused", "2026-04-15T10:00:00.000Z", Model("m", input: 300, output: 30)));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].InputTokens.Should().Be(300);
        result.Records[0].OutputTokens.Should().Be(30);
        result.Records[0].Timestamp.Should().Be(new DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Splits_resumed_shutdowns_across_dates_with_conserved_totals()
    {
        // 蓝本 splits_resumed_shutdowns_across_dates_without_double_counting：
        // resume 后的 shutdown 携带全部历史累计，差分把增量归属到后一条快照的日期，总量守恒。
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-old", "2026-01-02T01:20:00.000Z",
                Model("test-model", input: 100, output: 50, cacheRead: 10, cacheWrite: 20, requests: 1)),
            Shutdown("shutdown-latest", "2026-01-03T01:20:00.000Z",
                Model("test-model", input: 200, output: 80, cacheRead: 20, cacheWrite: 30, requests: 3)));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(2);
        var first = result.Records.Single(record => record.Timestamp.Date == new DateTime(2026, 1, 2));
        first.InputTokens.Should().Be(70);
        first.OutputTokens.Should().Be(50);
        first.CacheCreationTokens.Should().Be(20);
        first.CacheReadTokens.Should().Be(10);
        var second = result.Records.Single(record => record.Timestamp.Date == new DateTime(2026, 1, 3));
        second.InputTokens.Should().Be(80, "uncached 150 − 70（差分）");
        second.OutputTokens.Should().Be(30);
        second.CacheCreationTokens.Should().Be(10);
        second.CacheReadTokens.Should().Be(10);
        // 总量守恒：区间之和 == 末条累计快照的 uncached 值（resume 不重复出账）。
        result.Records.Sum(record => record.InputTokens).Should().Be(150);
        result.Records.Sum(record => record.OutputTokens).Should().Be(80);
    }

    [Fact]
    public async Task Keeps_one_entry_for_a_single_shutdown_snapshot()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("shutdown-1", "2026-01-02T01:20:00.000Z",
                Model("test-model", input: 100, output: 50, requests: 1)));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(50);
    }

    [Fact]
    public async Task Groups_intervals_per_session_and_model()
    {
        // 两个会话目录 × 两个模型：差分按 (session, model) 独立进行，互不串账。
        var home = CreateTempHome();
        WriteEvents(home, "session-a",
            Shutdown("a-1", "2026-04-15T09:00:00.000Z", Model("m1", input: 100, output: 10)),
            Shutdown("a-2", "2026-04-15T10:00:00.000Z", Model("m1", input: 150, output: 15),
                Model("m2", input: 500, output: 50)));
        WriteEvents(home, "session-b",
            Shutdown("b-1", "2026-04-15T09:30:00.000Z", Model("m1", input: 7, output: 7)));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(4);
        result.Records.Should().Contain(record => record.SessionId == "session-a" && record.Model == "m1" && record.InputTokens == 100);
        result.Records.Should().Contain(record => record.SessionId == "session-a" && record.Model == "m1" && record.InputTokens == 50);
        result.Records.Should().Contain(record => record.SessionId == "session-a" && record.Model == "m2" && record.InputTokens == 500);
        result.Records.Should().Contain(record => record.SessionId == "session-b" && record.Model == "m1" && record.InputTokens == 7);
        result.Diagnostics.FilesDiscovered.Should().Be(2);
    }

    [Fact]
    public async Task Drops_zero_token_intervals_but_keeps_them_as_subtraction_baseline()
    {
        // 全零累计快照（只有请求数）不产出零 token 记录（统一记录无请求数字段，聚合零贡献），
        // 但仍作为差分基线：后续快照对它做减法（蓝本 has_usage 含 request_count，此处为口径偏差，见卡片 Dev Log）。
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("s-1", "2026-04-15T09:00:00.000Z", RawModel("m", """{"usage":{"inputTokens":0,"outputTokens":0},"requests":{"count":5}}""")),
            Shutdown("s-2", "2026-04-15T10:00:00.000Z", Model("m", input: 100, output: 10, requests: 6)));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(100, "全零基线不减账");
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.Zero));
    }

    // ---- 模型后缀剥离影响分组（后缀变体并入同组差分） ----

    [Fact]
    public async Task Merges_suffix_variants_into_one_model_group_for_differencing()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("s-1", "2026-04-15T09:00:00.000Z", Model("gpt-5.1-1m", input: 1000, output: 100)),
            Shutdown("s-2", "2026-04-15T10:00:00.000Z", Model("gpt-5.1", input: 3000, output: 300)));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(2, "-1m 变体与基础模型同组累计差分（蓝本 normalize 后分组）");
        result.Records.Should().OnlyContain(record => record.Model == "gpt-5.1");
        result.Records[1].InputTokens.Should().Be(2000);
    }

    // ---- 定价（区间时间戳 + 剥离后模型名命中快照） ----

    [Fact]
    public async Task Prices_intervals_by_snapshot_pricing_at_interval_timestamp()
    {
        var home = CreateTempHome();
        WriteEvents(home, "session-1",
            Shutdown("s-1", "2026-04-15T09:00:00.000Z", Model("claude-sonnet-4", input: 10_000, output: 1_000,
                cacheRead: 4_000, cacheWrite: 2_000)));

        var scan = await CreateAdapter(home).ScanAsync();
        var engine = new PricingEngine();
        var record = scan.Records.Should().ContainSingle().Subject;
        var priced = engine.PriceRecord(record);

        priced.Source.Should().Be(PricingSourceKind.Snapshot);
        // input 10000 含缓存读写：uncached = 10000 − 4000 − 2000 = 4000；
        // 4000×$3/M + 1000×$15/M + 2000×$3.75/M + 4000×$0.3/M = 0.0357（与 CLI 同参数口径一致）。
        priced.CostUsd.Should().Be(0.0357m);
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    // ---- 目录发现与环境变量（蓝本 paths.rs 内联测试同名场景） ----

    [Fact]
    public async Task Discovers_session_state_files_without_nested_session_files()
    {
        var home = CreateTempHome();
        Directory.CreateDirectory(Path.Combine(home, "session-state", "session-a", "nested"));
        WriteEvents(home, "session-a", Shutdown("s-1", "2026-04-15T09:00:00.000Z", Model("m", input: 1, output: 1)));
        File.WriteAllText(Path.Combine(home, "session-state", "session-a", "other.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(home, "session-state", "session-a", "nested", "events.jsonl"),
            Shutdown("s-hidden", "2026-04-15T09:00:00.000Z", Model("m", input: 99, output: 99)) + "\n");

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle("仅 session-state 的一级会话目录的 events.jsonl 被发现（非递归）");
        result.Records[0].InputTokens.Should().Be(1);
        result.Diagnostics.FilesDiscovered.Should().Be(1);
    }

    [Fact]
    public async Task Copilot_home_environment_variable_is_respected()
    {
        var defaultHome = CreateTempHome();
        var relocated = CreateTempHome();
        WriteEvents(relocated, "session-relocated",
            Shutdown("s-1", "2026-04-15T09:00:00.000Z", Model("m", input: 5, output: 1)));
        WriteEvents(defaultHome, "session-default",
            Shutdown("s-1", "2026-04-15T09:00:00.000Z", Model("m", input: 7, output: 1)));

        // COPILOT_HOME 经注入的 accessor 提供（定位器读环境的生产同一缝隙），不动进程环境：
        // xunit 按类并行，进程级环境变量会与并行窗口内的其他 Copilot 用例互踩（B-004）。
        var adapter = new CopilotSessionAdapter(locator: new CopilotLogLocator(
            name => name == CopilotLogLocator.CopilotHomeEnvVariable ? relocated : null));
        var result = await adapter.ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].SessionId.Should().Be("session-relocated");
        result.Roots.Should().ContainSingle().Which.Should().Be(relocated);
    }

    [Fact]
    public async Task Missing_home_yields_no_data()
    {
        var root = CreateTempDir();
        var missingHome = Path.Combine(root, "copilot-missing");
        var result = await CreateAdapter(missingHome).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Roots.Should().ContainSingle().Which.Should().Be(missingHome,
            "configuredHome 非空即生效（目录可以不存在），无 session-state 即无数据");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
        result.Diagnostics.FilesDiscovered.Should().Be(0);
    }

    // ---- 增量扫描（共享引擎记账） ----

    [Fact]
    public async Task Incremental_rescan_picks_up_appended_snapshot_once()
    {
        var home = CreateTempHome();
        var eventsPath = WriteEvents(home, "session-1",
            Shutdown("s-1", "2026-04-15T09:00:00.000Z", Model("m", input: 100, output: 10)));
        var adapter = CreateAdapter(home);

        var first = await adapter.ScanAsync();
        var appended = Shutdown("s-2", "2026-04-15T10:00:00.000Z", Model("m", input: 250, output: 40)) + "\n";
        File.AppendAllText(eventsPath, appended);

        var second = await adapter.ScanAsync();

        first.Records.Should().ContainSingle();
        second.Records.Should().HaveCount(2, "追加的累计快照差分出新区间，且不重复出账旧区间");
        second.Records[1].InputTokens.Should().Be(150);
        second.Records[1].OutputTokens.Should().Be(30);
        second.Diagnostics.FilesRead.Should().Be(1, "只有变更文件被重解析");

        var third = await adapter.ScanAsync();
        third.Records.Should().HaveCount(2, "重复扫描幂等（文件记账 + 文件内去重）");
        third.Diagnostics.FilesRead.Should().Be(0);
    }

    // ---- helpers ----

    /// <summary>
    /// 全类扫描不读进程环境（xunit 按类并行 + dotnet test 继承 shell 环境，任何泄漏进来的
    /// Copilot 环境变量都会击穿默认定位器的导出路径分支，B-004）；环境语义由专用用例
    /// 经显式注入的 accessor 覆盖。
    /// </summary>
    private static readonly CopilotLogLocator HermeticLocator = new(_ => null);

    private static string Shutdown(string id, string timestamp, params string[] models)
        => $$"""{"type":"session.shutdown","id":"{{id}}","timestamp":"{{timestamp}}","data":{"modelMetrics":{ {{string.Join(",", models)}} } } }""";

    private static string Model(string name, long input = 0, long output = 0, long cacheRead = 0,
        long cacheWrite = 0, long reasoning = 0, long requests = 0)
        => RawModel(name, $$"""{"usage":{"inputTokens":{{input}},"outputTokens":{{output}},"cacheReadTokens":{{cacheRead}},"cacheWriteTokens":{{cacheWrite}},"reasoningTokens":{{reasoning}} },"requests":{"count":{{requests}} } }""");

    private static string RawModel(string name, string metricsJson) => $"\"{name}\":{metricsJson}";

    private CopilotSessionAdapter CreateAdapter(string home, CopilotLogLocator? locator = null)
        => new(configuredHome: home, locator: locator ?? HermeticLocator);

    private string CreateTempHome()
    {
        var home = Path.Combine(CreateTempDir(), "copilot");
        Directory.CreateDirectory(Path.Combine(home, "session-state"));
        return home;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-copilot-{Guid.NewGuid():N}");
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
