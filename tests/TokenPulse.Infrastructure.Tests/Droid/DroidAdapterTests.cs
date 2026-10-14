namespace TokenPulse.Infrastructure.Tests.Droid;

using TokenPulse.Core.Models;
using TokenPulse.Core.Services;
using TokenPulse.Infrastructure.Droid;

/// <summary>
/// I-015: Droid (Factory) 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage rust/adapters/droid 的 parser.rs / loader.rs / paths.rs，含蓝本内联测试的同名场景）。
/// </summary>
public class DroidAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- 模型归一化（蓝本 loader.rs normalizes_droid_model_names） ----

    [Fact]
    public async Task Normalizes_droid_model_names()
    {
        var root = CreateTempDataDir();
        WriteSettings(root, "n1.settings.json",
            Model: "custom:Claude-Opus-4.5-Thinking-[Anthropic]-0", Provider: "anthropic",
            Timestamp: "2026-05-01T01:02:03.000Z", TokenUsage: """{"inputTokens":1,"outputTokens":1}""");
        WriteSettings(root, "n2.settings.json",
            Model: "Claude-Sonnet-4-[Anthropic]", Provider: "anthropic",
            Timestamp: "2026-05-01T01:02:03.000Z", TokenUsage: """{"inputTokens":1,"outputTokens":1}""");
        WriteSettings(root, "n3.settings.json",
            Model: "gemini-2.5-pro", Provider: "google",
            Timestamp: "2026-05-01T01:02:03.000Z", TokenUsage: """{"inputTokens":1,"outputTokens":1}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Select(record => record.Model).Should().BeEquivalentTo(
            new[] { "claude-opus-4-5-thinking-0", "claude-sonnet-4", "gemini-2-5-pro" },
            "剥 custom: 前缀与 [..] 段、折叠分隔符（蓝本三例逐条）");
    }

    // ---- total 兜底（蓝本 loader.rs falls_back_to_total_tokens_when_droid_parts_are_missing） ----

    [Fact]
    public async Task Falls_back_to_total_tokens_when_droid_parts_are_missing()
    {
        var root = CreateTempDataDir();
        WriteSettings(root, "session-t.settings.json",
            Model: "m", Provider: "unknown",
            Timestamp: "2026-05-01T01:02:03.000Z", TokenUsage: """{"totalTokens":456}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(456, "output 为 0 时 total 缺口补入 output");
        record.InputTokens.Should().Be(0);
    }

    // ---- 全量加载（蓝本 loader.rs loads_usage_from_droid_settings_files） ----

    [Fact]
    public async Task Loads_usage_from_droid_settings_files()
    {
        var root = CreateTempDataDir();
        WriteSettings(root, "session-a.settings.json",
            Model: "Claude-Sonnet-4-[Anthropic]", Provider: "anthropic",
            Timestamp: "2026-05-01T01:02:03.000Z",
            TokenUsage: """{"inputTokens":100,"outputTokens":50,"cacheCreationTokens":20,"cacheReadTokens":10,"thinkingTokens":5}""");
        WriteSettings(root, "zero.settings.json",
            Model: "gpt-5", Provider: "openai",
            Timestamp: "2026-05-01T01:02:03.000Z", TokenUsage: """{"inputTokens":0}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Agent.Should().Be("Droid");
        record.ProjectPath.Should().Be("Droid");
        record.SessionId.Should().Be("session-a");
        record.Model.Should().Be("claude-sonnet-4");
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 1, 1, 2, 3, TimeSpan.Zero));
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(55, "display output 50 + thinking 5（并入 OutputTokens，偏差②）");
        record.CacheCreationTokens.Should().Be(20);
        record.CacheReadTokens.Should().Be(10);
        record.TotalTokens.Should().Be(185, "与蓝本 totalTokens（display+extra）一致");
        result.Diagnostics.LinesSkipped.Should().Be(1, "全零 tokenUsage 的快照跳过");
        result.Records.Should().OnlyContain(record => record.CostUsd == null, "成本恒 null 交定价引擎");
    }

    // ---- 侧车回退（蓝本 loader.rs falls_back_to_sidecar_jsonl_model） ----

    [Fact]
    public async Task Falls_back_to_sidecar_jsonl_model()
    {
        var root = CreateTempDataDir();
        WriteSettings(root, "session-b.settings.json",
            Model: null, Provider: "anthropic",
            Timestamp: "2026-05-02T01:02:03.000Z",
            TokenUsage: """{"inputTokens":10,"outputTokens":20}""");
        File.WriteAllText(Path.Combine(root, "session-b.jsonl"),
            """{"content":"Model: Claude Opus 4.5 Thinking [Anthropic]"}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("claude-opus-4-5-thinking", "无 settings.model 时读侧车 Model: 行并归一化");
    }

    [Fact]
    public async Task Falls_back_to_provider_default_model_without_settings_or_sidecar()
    {
        var root = CreateTempDataDir();
        WriteSettings(root, "session-c.settings.json",
            Model: null, Provider: "openai",
            Timestamp: "2026-05-02T01:02:03.000Z",
            TokenUsage: """{"inputTokens":10,"outputTokens":20}""");
        // 同目录存在不可解析内容的侧车：extract 不到模型 → provider 默认。
        File.WriteAllText(Path.Combine(root, "session-c.jsonl"), "{\"content\":\"no model marker\"}\n");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("gpt-unknown", "侧车无 Model: 行时按 provider 默认（openai → gpt-unknown）");
    }

    [Fact]
    public async Task Bracket_only_model_name_falls_back_to_provider_default()
    {
        // normalize 后为空串（仅方括号段）→ provider 默认模型（蓝本 model.is_empty() 分支）。
        var root = CreateTempDataDir();
        WriteSettings(root, "session-d.settings.json",
            Model: "[Anthropic]", Provider: "anthropic",
            Timestamp: "2026-05-02T01:02:03.000Z",
            TokenUsage: """{"inputTokens":10,"outputTokens":20}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("claude-unknown");
    }

    // ---- 快照去重（蓝本 loader.rs keeps_latest_snapshot_for_duplicate_session_ids） ----

    [Fact]
    public async Task Keeps_latest_snapshot_for_duplicate_session_ids()
    {
        var root = CreateTempDataDir();
        WriteSettings(root, Path.Combine("archive", "session-c.settings.json"),
            Model: "gpt-5", Provider: "openai",
            Timestamp: "2026-05-01T01:02:03.000Z",
            TokenUsage: """{"inputTokens":10,"outputTokens":20}""");
        WriteSettings(root, "session-c.settings.json",
            Model: "gpt-5", Provider: "openai",
            Timestamp: "2026-05-02T01:02:03.000Z",
            TokenUsage: """{"inputTokens":100,"outputTokens":200}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SessionId.Should().Be("session-c");
        record.InputTokens.Should().Be(100, "同 session id 留时间戳最新的快照（latest-wins）");
        record.OutputTokens.Should().Be(200);
        result.Diagnostics.DuplicateRecords.Should().Be(1, "被弃用的旧快照计重复");
    }

    // ---- 时间戳与文件级容错 ----

    [Fact]
    public async Task Provider_lock_timestamp_takes_precedence_over_file_mtime()
    {
        var root = CreateTempDataDir();
        WriteSettings(root, "session-e.settings.json",
            Model: "gpt-5", Provider: "openai",
            Timestamp: "2026-05-03T12:00:00.000Z",
            TokenUsage: """{"inputTokens":10,"outputTokens":20}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero),
            "providerLockTimestamp 可解析即优先（否则回退 mtime——mtime 依赖检出时间，fixture 不覆盖该分支）");
    }

    [Fact]
    public async Task Malformed_and_invalid_snapshots_are_counted_and_do_not_stop_the_scan()
    {
        var root = CreateTempDataDir();
        File.WriteAllText(Path.Combine(root, "bad.settings.json"), "{not json");
        File.WriteAllText(Path.Combine(root, "array.settings.json"), "[1,2,3]");
        File.WriteAllText(Path.Combine(root, "no-usage.settings.json"),
            """{"model":"gpt-5","providerLockTimestamp":"2026-05-01T01:02:03.000Z"}""");
        WriteSettings(root, "ok.settings.json",
            Model: "gpt-5", Provider: "openai",
            Timestamp: "2026-05-01T01:02:03.000Z", TokenUsage: """{"inputTokens":3,"outputTokens":4}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.MalformedLines.Should().Be(1, "坏 JSON 计 1（仓库口径显式计数）");
        result.Diagnostics.InvalidRecords.Should().Be(1, "顶层非对象（JSON 合法但结构性失败）计 1");
        result.Diagnostics.LinesSkipped.Should().Be(1, "无 tokenUsage 的快照计 skipped");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    // ---- 定价（内置快照按裸模型名命中） ----

    [Fact]
    public async Task Prices_records_via_builtin_snapshot()
    {
        var root = CreateTempDataDir();
        WriteSettings(root, "priced.settings.json",
            Model: "claude-sonnet-4", Provider: "anthropic",
            Timestamp: "2026-05-01T01:02:03.000Z",
            TokenUsage: """{"inputTokens":10000,"outputTokens":1000,"cacheCreationTokens":2000,"cacheReadTokens":4000}""");

        var result = await CreateAdapter(root).ScanAsync();
        var engine = new PricingEngine();

        var priced = engine.PriceRecord(result.Records.Should().ContainSingle().Subject);
        priced.Source.Should().Be(PricingSourceKind.Snapshot);
        // 10000×$3/M + 1000×$15/M + 2000×$3.75/M + 4000×$0.3/M = $0.03 + $0.015 + $0.0075 + $0.0012
        priced.CostUsd.Should().Be(0.0537m);
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    // ---- 目录发现与环境变量（蓝本 paths.rs 语义） ----

    [Fact]
    public async Task Discovers_only_settings_snapshot_files()
    {
        var root = CreateTempDataDir();
        WriteSettings(root, "session-a.settings.json",
            Model: "gpt-5", Provider: "openai",
            Timestamp: "2026-05-01T01:02:03.000Z", TokenUsage: """{"inputTokens":1,"outputTokens":1}""");
        File.WriteAllText(Path.Combine(root, "sidecar.jsonl"), "{}");
        File.WriteAllText(Path.Combine(root, "meta.json"), "{}");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllText(Path.Combine(root, "nested", "deep.settings.json"),
            """{"model":"gpt-5","providerLock":"openai","providerLockTimestamp":"2026-05-02T01:02:03.000Z","tokenUsage":{"inputTokens":2,"outputTokens":2}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "仅 *.settings.json 被发现（含嵌套目录）；meta.json/侧车不发现");
        result.Diagnostics.FilesDiscovered.Should().Be(2);
    }

    [Fact]
    public async Task Droid_sessions_dir_environment_variable_is_respected_without_default_fallback()
    {
        var relocated = CreateTempDataDir();
        WriteSettings(relocated, "relocated.settings.json",
            Model: "gpt-5", Provider: "openai",
            Timestamp: "2026-05-01T01:02:03.000Z", TokenUsage: """{"inputTokens":5,"outputTokens":1}""");

        try
        {
            Environment.SetEnvironmentVariable(DroidLogLocator.DroidSessionsDirEnvVariable, relocated);
            var result = await new DroidAdapter().ScanAsync();

            result.Records.Should().ContainSingle();
            result.Records[0].SessionId.Should().Be("relocated");
            result.Roots.Should().ContainSingle().Which.Should().Be(relocated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DroidLogLocator.DroidSessionsDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Set_but_empty_environment_variable_yields_no_data_without_fallback()
    {
        var defaultRoot = CreateTempDataDir();
        WriteSettings(defaultRoot, "default.settings.json",
            Model: "gpt-5", Provider: "openai",
            Timestamp: "2026-05-01T01:02:03.000Z", TokenUsage: """{"inputTokens":5,"outputTokens":1}""");

        try
        {
            Environment.SetEnvironmentVariable(DroidLogLocator.DroidSessionsDirEnvVariable, "");
            var result = await new DroidAdapter().ScanAsync();

            result.Records.Should().BeEmpty();
            result.Roots.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DroidLogLocator.DroidSessionsDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Missing_configured_directory_yields_no_data()
    {
        var root = CreateTempDir();
        var missing = Path.Combine(root, "droid-missing");

        var result = await CreateAdapter(missing).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Roots.Should().ContainSingle().Which.Should().Be(missing);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    // ---- helpers ----

    private DroidAdapter CreateAdapter(string dataDir) => new(configuredDataDir: dataDir);

    private string CreateTempDataDir()
    {
        var path = Path.Combine(CreateTempDir(), "sessions");
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-droid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static void WriteSettings(string sessionsDir, string fileName, string? Model, string Provider,
        string Timestamp, string TokenUsage)
    {
        var fullPath = Path.Combine(sessionsDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var modelField = Model is null ? string.Empty : $@"""model"":""{Model}"",";
        File.WriteAllText(fullPath,
            $"{{{modelField}\"providerLock\":\"{Provider}\",\"providerLockTimestamp\":\"{Timestamp}\",\"tokenUsage\":{TokenUsage}}}");
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
