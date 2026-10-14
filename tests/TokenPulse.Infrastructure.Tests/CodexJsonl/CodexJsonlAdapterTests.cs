namespace TokenPulse.Infrastructure.Tests.CodexJsonl;

using TokenPulse.Core.Models;
using TokenPulse.Core.Services;
using TokenPulse.Infrastructure.CodexJsonl;

public class CodexJsonlAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    /// <summary>TC-001: codex fixture 解析（fixture 素材取自 codex.rs 内联测试的真实 payload 形状）。</summary>
    [Fact]
    public async Task Parses_sample_fixture_from_configured_home()
    {
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/2026/05/25/rollout-fixture-a.jsonl",
            await File.ReadAllTextAsync("fixtures/codex/sample-session.jsonl"));
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEquivalentTo(new[] { Path.Combine(home, "sessions") });
        result.Records.Should().HaveCount(2);

        var first = result.Records[0];
        first.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 25, 16, 0, 18, 744, TimeSpan.Zero));
        first.Agent.Should().Be("Codex");
        first.Model.Should().Be("gpt-5.3-codex");
        first.ProjectPath.Should().Be("F:\\dev\\skynet");
        first.SessionId.Should().Be("2026/05/25/rollout-fixture-a");
        first.SourceFile.Should().EndWith(
            Path.Combine("sessions", "2026", "05", "25", "rollout-fixture-a.jsonl"));
        // input_tokens 为"非缓存输入"：全量 input 13551 - cached 2432，cached 计入 CacheRead。
        first.InputTokens.Should().Be(11_119);
        first.CacheReadTokens.Should().Be(2_432);
        first.OutputTokens.Should().Be(528);
        first.TotalTokens.Should().Be(14_079); // 与蓝本 total_tokens 一致（缓存不重复计数）
        first.CostUsd.Should().BeNull(); // 定价由 I-003 引擎承担
        first.MessageId.Should().BeNull();

        // 第二个事件只有累计总额，增量由差分恢复。
        var second = result.Records[1];
        second.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 25, 16, 5, 18, 744, TimeSpan.Zero));
        second.Model.Should().Be("gpt-5.3-codex"); // 沿用 turn_context 的模型
        second.InputTokens.Should().Be(4_881); // 6449 - 1568
        second.CacheReadTokens.Should().Be(1_568);
        second.OutputTokens.Should().Be(472);
        second.TotalTokens.Should().Be(6_921);

        var diagnostics = result.Diagnostics;
        diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        diagnostics.FilesDiscovered.Should().Be(1);
        diagnostics.FilesRead.Should().Be(1);
        diagnostics.LinesSkipped.Should().Be(1); // response_item 行不含标记
        diagnostics.MalformedLines.Should().Be(0);
        diagnostics.InvalidRecords.Should().Be(0);
        diagnostics.DuplicateRecords.Should().Be(0);
        diagnostics.MissingCostRecords.Should().Be(2); // Codex 日志无内建成本
        diagnostics.UnknownModelRecords.Should().Be(0); // 模型缺失时回退 gpt-5，不会为 null
    }

    [Fact]
    public async Task Falls_back_to_gpt5_when_no_model_present()
    {
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/rollout-b.jsonl",
            """{"timestamp":"2026-05-25T16:00:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":50,"reasoning_output_tokens":0,"total_tokens":150}}}}""" + "\n");
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();

        result.Records.Should().ContainSingle();
        var record = result.Records.Single();
        record.Model.Should().Be("gpt-5");
        record.ProjectPath.Should().Be("Unknown Project");
        record.SessionId.Should().Be("rollout-b");
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(50);
        record.TotalTokens.Should().Be(150);
    }

    [Fact]
    public async Task Duplicate_token_count_events_are_not_double_counted()
    {
        // 真实 Codex CLI 每个 turn 连发两条 token_count（第二条为重复快照，累计总额未推进）。
        // 与发布版 ccusage 的 cumulative_advanced 守卫一致：重复事件不产出记录。
        var home = CreateTempHome();
        const string turn1 =
            """{"timestamp":"2026-05-25T16:00:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":50,"reasoning_output_tokens":0,"total_tokens":150},"last_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":50,"reasoning_output_tokens":0,"total_tokens":150}}}}""";
        const string turn1Duplicate =
            """{"timestamp":"2026-05-25T16:00:20.744Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":50,"reasoning_output_tokens":0,"total_tokens":150},"last_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":50,"reasoning_output_tokens":0,"total_tokens":150}}}}""";
        const string turn2 =
            """{"timestamp":"2026-05-25T16:05:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":300,"cached_input_tokens":60,"output_tokens":120,"reasoning_output_tokens":0,"total_tokens":420},"last_token_usage":{"input_tokens":200,"cached_input_tokens":40,"output_tokens":70,"reasoning_output_tokens":0,"total_tokens":270}}}}""";
        const string turn2Duplicate =
            """{"timestamp":"2026-05-25T16:05:20.744Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":300,"cached_input_tokens":60,"output_tokens":120,"reasoning_output_tokens":0,"total_tokens":420},"last_token_usage":{"input_tokens":200,"cached_input_tokens":40,"output_tokens":70,"reasoning_output_tokens":0,"total_tokens":270}}}}""";
        WriteSessionFile(home, "sessions/rollout-dup.jsonl",
            string.Join("\n", turn1, turn1Duplicate, turn2, turn2Duplicate) + "\n");
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();

        result.Records.Should().HaveCount(2); // 重复快照不产出记录
        result.Records[0].Timestamp.Should().Be(new DateTimeOffset(2026, 5, 25, 16, 0, 18, 744, TimeSpan.Zero));
        result.Records[0].InputTokens.Should().Be(80); // 100 - 20
        result.Records[0].CacheReadTokens.Should().Be(20);
        result.Records[0].OutputTokens.Should().Be(50);
        result.Records[1].Timestamp.Should().Be(new DateTimeOffset(2026, 5, 25, 16, 5, 18, 744, TimeSpan.Zero));
        result.Records[1].InputTokens.Should().Be(160); // 200 - 40
        result.Records[1].CacheReadTokens.Should().Be(40);
        result.Records[1].OutputTokens.Should().Be(70);

        var second = await adapter.ScanAsync();
        second.Records.Should().HaveCount(2); // 幂等
    }

    [Fact]
    public async Task Caps_cached_tokens_to_input()
    {
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/rollout-c.jsonl",
            """{"timestamp":"2026-05-25T15:58:27.480Z","type":"turn_context","payload":{"model":"gpt-5"}}""" + "\n"
            + """{"timestamp":"2026-05-25T16:00:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1000000,"cached_input_tokens":2000000,"output_tokens":1000000,"reasoning_output_tokens":0,"total_tokens":2000000}}}}""" + "\n");
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();

        result.Records.Should().ContainSingle();
        var record = result.Records.Single();
        // cached 被截断到 input，非缓存输入为 0。
        record.CacheReadTokens.Should().Be(1_000_000);
        record.InputTokens.Should().Be(0);
        record.OutputTokens.Should().Be(1_000_000);
        record.TotalTokens.Should().Be(2_000_000);

        // 与 codex.rs 测试同口径：cost = 0 * input + 1e6 * cache_read(0.125/1M) + 1e6 * output(10/1M)。
        var pricing = new PricingEngine().PriceRecord(record);
        pricing.Source.Should().Be(PricingSourceKind.Snapshot);
        pricing.MatchedKey.Should().Be("openai/gpt-5");
        pricing.CostUsd.Should().BeApproximately(10.125m, 0.0000001m);
    }

    [Fact]
    public async Task Codex_records_are_priced_by_pricing_engine_with_snapshot_rates()
    {
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/2026/05/25/rollout-fixture-a.jsonl",
            await File.ReadAllTextAsync("fixtures/codex/sample-session.jsonl"));
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();
        var engine = new PricingEngine();

        // gpt-5.3-codex 按快照家族模式命中 gpt-5.3（input 1.75 / output 14 / cache_read 0.175，USD/1M）。
        var first = engine.PriceRecord(result.Records[0]);
        first.Source.Should().Be(PricingSourceKind.Snapshot);
        first.MatchedKey.Should().Be("openai/gpt-5.3");
        first.CostUsd.Should().BeApproximately(0.02727585m, 0.0000001m);

        var second = engine.PriceRecord(result.Records[1]);
        second.CostUsd.Should().BeApproximately(0.01542415m, 0.0000001m);
    }

    [Fact]
    public async Task Corrupted_lines_are_skipped_and_counted()
    {
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/rollout-c.jsonl",
            await File.ReadAllTextAsync("fixtures/codex/corrupted-lines.jsonl"));
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();

        result.Records.Should().ContainSingle(); // 坏行不中断，合法记录仍保留
        result.Records.Single().InputTokens.Should().Be(10);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
        result.Diagnostics.LinesSkipped.Should().Be(1); // 无标记的 summary 行
        result.Diagnostics.MalformedLines.Should().Be(1); // 含标记的坏 JSON
        result.Diagnostics.InvalidRecords.Should().Be(0); // 零 usage 与 info=null 的事件静默跳过
    }

    [Fact]
    public async Task Malformed_field_types_count_as_malformed_lines()
    {
        // 蓝本 serde 单次 from_str 同时校验语法与类型：字段类型不符同计 malformed。
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/rollout-bad-types.jsonl",
            """{"timestamp":"2026-05-25T16:00:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":"abc","output_tokens":5}}}}""" + "\n");
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.MalformedLines.Should().Be(1);
        result.Diagnostics.InvalidRecords.Should().Be(0);
    }

    [Fact]
    public async Task Token_count_without_timestamp_is_skipped_silently()
    {
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/rollout-no-ts.jsonl",
            """{"type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}}""" + "\n");
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.LinesSkipped.Should().Be(0);
        result.Diagnostics.MalformedLines.Should().Be(0);
        result.Diagnostics.InvalidRecords.Should().Be(0);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Unparseable_timestamp_counts_as_invalid_record()
    {
        // 统一记录要求可解析时间戳（与 Claude 适配器口径一致）；蓝本不解析时间戳，此为 C# 侧补充校验。
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/rollout-bad-ts.jsonl",
            """{"timestamp":"not-a-timestamp","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}}""" + "\n");
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.InvalidRecords.Should().Be(1);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>TC-002: 重复运行幂等。</summary>
    [Fact]
    public async Task Repeated_scan_is_idempotent()
    {
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/2026/05/25/rollout-fixture-a.jsonl",
            await File.ReadAllTextAsync("fixtures/codex/sample-session.jsonl"));
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var first = await adapter.ScanAsync();
        var second = await adapter.ScanAsync();

        first.Records.Should().HaveCount(2);
        second.Records.Should().HaveCount(2);
        second.Records.Should().BeEquivalentTo(first.Records);
        second.Diagnostics.FilesRead.Should().Be(0); // 未变更文件不重复解析
        second.Diagnostics.FilesDiscovered.Should().Be(1);
        second.Diagnostics.DuplicateRecords.Should().Be(0);
        second.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
    }

    [Fact]
    public async Task Appended_token_count_event_is_picked_up_next_scan()
    {
        var home = CreateTempHome();
        var file = WriteSessionFile(home, "sessions/2026/05/25/rollout-fixture-a.jsonl",
            await File.ReadAllTextAsync("fixtures/codex/sample-session.jsonl"));
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var first = await adapter.ScanAsync();
        first.Records.Should().HaveCount(2);

        var appended =
            """{"timestamp":"2026-05-25T16:10:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":25000,"cached_input_tokens":5000,"output_tokens":1200,"reasoning_output_tokens":500,"total_tokens":26200},"last_token_usage":{"input_tokens":5000,"cached_input_tokens":1000,"output_tokens":200,"reasoning_output_tokens":100,"total_tokens":6200}}}}""" + "\n";
        File.AppendAllText(file, appended);

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesRead.Should().Be(1); // 只有变更文件被重新解析
        second.Records.Should().HaveCount(3);
        second.Records[2].InputTokens.Should().Be(4_000);
        second.Records[2].CacheReadTokens.Should().Be(1_000);
        second.Records[2].OutputTokens.Should().Be(200);
        second.Records.Count(record => record.Timestamp == new DateTimeOffset(2026, 5, 25, 16, 0, 18, 744, TimeSpan.Zero))
            .Should().Be(1); // 旧记录不重复
    }

    [Fact]
    public async Task Removed_file_drops_its_records()
    {
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/one.jsonl",
            """{"timestamp":"2026-05-25T16:00:00.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}}""" + "\n");
        var second = WriteSessionFile(home, "sessions/two.jsonl",
            """{"timestamp":"2026-05-25T16:01:00.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":20,"output_tokens":5,"total_tokens":25}}}}""" + "\n");
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var initial = await adapter.ScanAsync();
        initial.Records.Should().HaveCount(2);

        File.Delete(second);
        var after = await adapter.ScanAsync();

        after.Records.Should().ContainSingle();
        after.Records.Single().SessionId.Should().Be("one");
    }

    [Fact]
    public async Task Empty_home_returns_nodata()
    {
        var home = CreateTempHome();
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();

        result.Records.Should().BeEmpty();
        result.Roots.Should().BeEquivalentTo(new[] { home }); // 无 sessions/ 时回退 home 本身
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
        result.Diagnostics.FilesDiscovered.Should().Be(0);
    }

    [Fact]
    public async Task Missing_home_returns_nodata_with_empty_roots()
    {
        var missing = Path.Combine(CreateTempHome(), "does-not-exist");
        var adapter = new CodexJsonlAdapter(configuredHome: missing);

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEmpty();
        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Prefers_active_sessions_file_over_archived_duplicate()
    {
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/dup.jsonl",
            """{"timestamp":"2026-05-25T16:00:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"output_tokens":10,"total_tokens":110}}}}""" + "\n");
        WriteSessionFile(home, "archived_sessions/dup.jsonl",
            """{"timestamp":"2026-05-25T16:00:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":999,"output_tokens":10,"total_tokens":1009}}}}""" + "\n");
        WriteSessionFile(home, "archived_sessions/only.jsonl",
            """{"timestamp":"2026-05-25T16:02:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":7,"output_tokens":3,"total_tokens":10}}}}""" + "\n");
        var adapter = new CodexJsonlAdapter(configuredHome: home);

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEquivalentTo(new[] { Path.Combine(home, "sessions"), Path.Combine(home, "archived_sessions") });
        result.Diagnostics.FilesDiscovered.Should().Be(2); // 同相对路径的归档副本被跳过
        result.Records.Should().HaveCount(2);
        result.Records.Single(record => record.SessionId == "dup").InputTokens.Should().Be(100); // 活动副本优先
        result.Records.Single(record => record.SessionId == "only").InputTokens.Should().Be(7);
    }

    [Fact]
    public async Task Uses_codex_home_from_injected_environment()
    {
        var home = CreateTempHome();
        WriteSessionFile(home, "sessions/rollout-env.jsonl",
            """{"timestamp":"2026-05-25T16:00:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":3,"output_tokens":1,"total_tokens":4}}}}""" + "\n");
        var adapter = new CodexJsonlAdapter(locator: NewLocator(("CODEX_HOME", home)));

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEquivalentTo(new[] { Path.Combine(home, "sessions") });
        result.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task Explicit_home_overrides_injected_environment()
    {
        var envHome = CreateTempHome();
        WriteSessionFile(envHome, "sessions/rollout-env.jsonl",
            """{"timestamp":"2026-05-25T16:00:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}}""" + "\n");
        var explicitHome = CreateTempHome();
        WriteSessionFile(explicitHome, "sessions/rollout-explicit.jsonl",
            """{"timestamp":"2026-05-25T16:00:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}}}""" + "\n");
        var locator = NewLocator(("CODEX_HOME", envHome));
        var adapter = new CodexJsonlAdapter(configuredHome: explicitHome, locator: locator);

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEquivalentTo(new[] { Path.Combine(explicitHome, "sessions") });
        result.Records.Single().SessionId.Should().Be("rollout-explicit");
    }

    [Fact]
    public async Task Multi_home_scan_combines_records_from_every_home()
    {
        var homeA = CreateTempHome();
        var homeB = CreateTempHome();
        WriteSessionFile(homeA, "sessions/rollout-a.jsonl",
            """{"timestamp":"2026-05-25T16:00:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"output_tokens":1,"total_tokens":11}}}}""" + "\n");
        WriteSessionFile(homeB, "sessions/rollout-b.jsonl",
            """{"timestamp":"2026-05-25T16:01:18.744Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":20,"output_tokens":1,"total_tokens":21}}}}""" + "\n");
        var locator = NewLocator(("CODEX_HOME", $"{homeA},{homeB}"));
        var adapter = new CodexJsonlAdapter(locator: locator);

        var result = await adapter.ScanAsync();

        result.Roots.Should().BeEquivalentTo(new[] { Path.Combine(homeA, "sessions"), Path.Combine(homeB, "sessions") });
        result.Records.Should().HaveCount(2);
        result.Records.Select(record => record.SessionId).Should().BeEquivalentTo(["rollout-a", "rollout-b"]);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static CodexLogLocator NewLocator(params (string Key, string? Value)[] variables)
        => new(key => variables.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.Ordinal)).Value);

    private string CreateTempHome()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccusage-codex-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static string WriteSessionFile(string home, string relativePath, string contents)
    {
        var path = Path.Combine(home, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
}
