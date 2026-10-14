namespace TokenPulse.Infrastructure.Tests.QwenCode;

using TokenPulse.Core.Models;
using TokenPulse.Core.Services;
using TokenPulse.Infrastructure.QwenCode;

/// <summary>
/// I-011: Qwen Code 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage rust/adapters/qwen 的 parser.rs / loader.rs / paths.rs，含蓝本内联测试的同名场景）。
/// </summary>
public class QwenCodeAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- 全量加载（蓝本 lib.rs loads_qwen_jsonl_usage_entries 旗舰场景） ----

    [Fact]
    public async Task Loads_qwen_jsonl_usage_entries()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/myProject/chats/chat-a.jsonl",
            """{"type":"user","text":"hello"}""",
            """{"type":"assistant","model":"qwen3-coder-plus","timestamp":"2026-02-23T14:24:56.857Z","sessionId":"session-json","usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":50,"thoughtsTokenCount":10,"cachedContentTokenCount":5}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Agent.Should().Be("Qwen Code");
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 2, 23, 14, 24, 56, 857, TimeSpan.Zero));
        record.SessionId.Should().Be("session-json");
        record.ProjectPath.Should().Be("myProject");
        record.Model.Should().Be("qwen3-coder-plus");
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(60, "display output 50 + thoughts 10（extra 并入 OutputTokens，蓝本 billable 口径）");
        record.CacheCreationTokens.Should().Be(0);
        record.CacheReadTokens.Should().Be(5);
        record.TotalTokens.Should().Be(165, "与蓝本 totalTokens（display + extra）一致");
        record.CostUsd.Should().BeNull("成本恒 null 交定价引擎");
        record.MessageId.Should().BeNull("Qwen 无 message id，去重走内容键");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        result.Diagnostics.LinesSkipped.Should().Be(1, "user 行无 usageMetadata 标记被预过滤跳过");
        result.Diagnostics.MalformedLines.Should().Be(0);
        result.Diagnostics.InvalidRecords.Should().Be(0);
    }

    // ---- total 兜底（蓝本 parser.rs falls_back_to_total_token_count_when_qwen_parts_are_missing） ----

    [Fact]
    public async Task Falls_back_to_total_token_count_when_qwen_parts_are_missing()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/p/chats/chat.jsonl",
            """{"type":"assistant","timestamp":"2026-01-02T00:00:00.000Z","sessionId":"session-a","model":"qwen3-coder","usageMetadata":{"totalTokenCount":321}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(321, "output 为 0 时 total 缺口补入 output");
        record.InputTokens.Should().Be(0);
    }

    [Fact]
    public async Task Total_fallback_keeps_extra_when_output_is_known()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/p/chats/chat.jsonl",
            """{"type":"assistant","timestamp":"2026-01-02T00:00:00.000Z","sessionId":"session-a","usageMetadata":{"promptTokenCount":200,"candidatesTokenCount":100,"thoughtsTokenCount":0,"cachedContentTokenCount":20,"totalTokenCount":500}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(280, "display output 100 + total 缺口 180（extra）");
        record.CacheReadTokens.Should().Be(20);
        record.TotalTokens.Should().Be(500);
    }

    [Fact]
    public async Task Zero_and_missing_counts_are_invalid()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/p/chats/chat.jsonl",
            """{"type":"assistant","timestamp":"2026-01-02T00:00:00.000Z","usageMetadata":{"promptTokenCount":0}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty("四桶与 extra 全零的行不产出");
        result.Diagnostics.InvalidRecords.Should().Be(1, "全零计数计 InvalidRecords（蓝本静默跳过处显式计数）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    // ---- 结构与语义拒绝（蓝本 serde 单次反序列化失败场景的仓库口径） ----

    [Fact]
    public async Task Structural_and_semantic_rejections_are_counted_and_do_not_stop_the_scan()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/p/chats/chat.jsonl",
            "{\"type\":\"assistant\",\"timestamp\":\"2026-01-02T00:00:00.000Z\",\"sessionId\":\"ok\",\"usageMetadata\":{\"promptTokenCount\":10,\"candidatesTokenCount\":5}}",
            "not json but with \"usageMetadata\" marker",
            """{"type":"user","usageMetadata":{"promptTokenCount":1}}""",
            """{"type":"assistant","usageMetadata":null}""",
            """{"type":"assistant","usageMetadata":"oops"}""",
            """[{"usageMetadata":{}}]""",
            """{"type":"assistant","timestamp":"2026-01-02T00:00:00.000Z","sessionId":"ok-2","usageMetadata":{"promptTokenCount":20,"candidatesTokenCount":6}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Diagnostics.MalformedLines.Should().Be(1, "坏 JSON 行计 1");
        result.Diagnostics.InvalidRecords.Should().Be(4, "user 行 + usageMetadata null + 非对象 usageMetadata + 顶层非对象");
        result.Diagnostics.LinesSkipped.Should().Be(0, "所有行都带标记");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    // ---- 去重（蓝本 entry_id 跨文件 first-wins） ----

    [Fact]
    public async Task Entry_id_dedupes_across_files_first_wins()
    {
        var root = CreateTempDataDir();
        var assistantLine = """{"type":"assistant","model":"qwen3-coder-plus","timestamp":"2026-02-23T14:24:56.857Z","sessionId":"session-json","usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":50,"thoughtsTokenCount":10,"cachedContentTokenCount":5}}""";
        WriteChat(root, "projects/myProject/chats/chat-a.jsonl", assistantLine);
        WriteChat(root, "projects/myProject/chats/dupe-chat.jsonl", assistantLine);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SourceFile.Should().Contain("chat-a.jsonl", "发现序 first-wins（chat-a 排序在前）");
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    [Fact]
    public async Task Repeated_scan_is_idempotent()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/p/chats/chat.jsonl",
            """{"type":"assistant","timestamp":"2026-02-23T14:24:56.857Z","sessionId":"s","usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":50}}""");
        var adapter = CreateAdapter(root);

        var first = await adapter.ScanAsync();
        var second = await adapter.ScanAsync();

        second.Records.Should().HaveCount(first.Records.Count);
        second.Diagnostics.DuplicateRecords.Should().Be(0, "增量扫描不产生重复记录");
        second.Diagnostics.FilesRead.Should().Be(0, "未变更文件不重解析");
    }

    // ---- 内容键（蓝本 entry_id_is_unambiguous_for_colon_fields） ----

    [Fact]
    public void Entry_key_is_unambiguous_for_colon_fields()
    {
        var deduper = new QwenUsageRecordDeduper();
        var timestamp = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        UsageRecord Record(string sessionId, string model, long input, long output, long cacheRead, DateTimeOffset at) => new()
        {
            Timestamp = at,
            SessionId = sessionId,
            Model = model,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
        };

        var deduped = deduper.Dedupe(new[]
        {
            Record("session:1", "model:1", 1, 6, 3, timestamp),
            Record("session:1", "model:1", 1, 6, 3, timestamp), // 同内容键 → 折叠
            Record("session:1", "model:1", 1, 6, 4, timestamp), // cacheRead 不同
            Record("session:2", "model:1", 1, 6, 3, timestamp), // session 不同
            Record("session:1", "model:2", 1, 6, 3, timestamp), // model 不同
            Record("session:1", "model:1", 2, 6, 3, timestamp), // input 不同
            Record("session:1", "model:1", 1, 7, 3, timestamp), // output 不同
            Record("session:1", "model:1", 1, 6, 3, timestamp.AddMilliseconds(1)), // 时间戳不同
        });

        deduped.Should().HaveCount(7, "JSON 数组键按分量区分，冒号/逗号字段不串键（extra 已并入 output）");
        deduped[0].InputTokens.Should().Be(1, "first-wins 保留首条");
    }

    // ---- 时间戳回退（蓝本 file_timestamp → mtime） ----

    [Fact]
    public async Task Missing_timestamp_falls_back_to_file_mtime()
    {
        var root = CreateTempDataDir();
        var file = Path.Combine(root, "projects", "p", "chats", "chat.jsonl");
        WriteChat(root, "projects/p/chats/chat.jsonl",
            """{"type":"assistant","sessionId":"s","usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}""");
        var mtime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, mtime);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
            "行内 timestamp 缺失回退文件 mtime（毫秒截断）");
    }

    [Fact]
    public async Task Unparseable_timestamp_falls_back_to_file_mtime()
    {
        var root = CreateTempDataDir();
        var file = Path.Combine(root, "projects", "p", "chats", "chat.jsonl");
        WriteChat(root, "projects/p/chats/chat.jsonl",
            """{"type":"assistant","timestamp":"2026-01-02 00:00:00","sessionId":"s","usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}""");
        var mtime = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, mtime);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should()
            .Be(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
                "蓝本 parse_ts_timestamp 严格校验（空格分隔拒绝）→ 回退 mtime");
    }

    // ---- 会话与项目回退 ----

    [Fact]
    public async Task Missing_session_id_falls_back_to_project_and_file_stem()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/myProject/chats/chat-a.jsonl",
            """{"type":"assistant","timestamp":"2026-02-23T14:24:56.857Z","usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SessionId.Should().Be("myProject-chat-a", "sessionId 缺失回退 <project>-<文件名主干>");
        record.ProjectPath.Should().Be("myProject");
    }

    [Fact]
    public async Task Missing_model_defaults_to_unknown()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/p/chats/chat.jsonl",
            """{"type":"assistant","timestamp":"2026-02-23T14:24:56.857Z","sessionId":"s","usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Model.Should().Be("unknown", "蓝本 DEFAULT_QWEN_MODEL");
    }

    // ---- 目录发现（蓝本 paths.rs projects/chats 双段布局） ----

    [Fact]
    public async Task Discovers_only_projects_chats_layout_files()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/pA/chats/chat-a.jsonl",
            """{"type":"assistant","timestamp":"2026-02-23T14:24:56.857Z","sessionId":"a","usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}""");
        WriteChat(root, "projects/pA/transcripts/notes.jsonl",
            """{"type":"assistant","usageMetadata":{"promptTokenCount":999}}""");
        WriteChat(root, "projects/pA/chats/deep/inside.jsonl",
            """{"type":"assistant","usageMetadata":{"promptTokenCount":999}}""");
        WriteChat(root, "chats/stray.jsonl",
            """{"type":"assistant","usageMetadata":{"promptTokenCount":999}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.SourceFile.Should().Contain("chat-a.jsonl");
        result.Diagnostics.FilesDiscovered.Should().Be(1, "仅 projects/<p>/chats/<f>.jsonl 三段布局被发现");
    }

    [Fact]
    public async Task Qwen_data_dir_environment_variable_is_respected_without_default_fallback()
    {
        var relocated = CreateTempDataDir();
        WriteChat(relocated, "projects/p/chats/relocated.jsonl",
            """{"type":"assistant","timestamp":"2026-02-23T14:24:56.857Z","sessionId":"s","usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}""");

        try
        {
            Environment.SetEnvironmentVariable(QwenLogLocator.QwenDataDirEnvVariable, relocated);
            var result = await new QwenCodeAdapter().ScanAsync();

            result.Records.Should().ContainSingle();
            result.Records[0].SessionId.Should().Be("s");
            result.Roots.Should().ContainSingle().Which.Should().Be(relocated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(QwenLogLocator.QwenDataDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Set_but_empty_environment_variable_yields_no_data_without_fallback()
    {
        var defaultRoot = CreateTempDataDir();
        WriteChat(defaultRoot, "projects/p/chats/default.jsonl",
            """{"type":"assistant","timestamp":"2026-02-23T14:24:56.857Z","sessionId":"s","usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}""");

        try
        {
            Environment.SetEnvironmentVariable(QwenLogLocator.QwenDataDirEnvVariable, "");
            var result = await new QwenCodeAdapter().ScanAsync();

            result.Records.Should().BeEmpty();
            result.Roots.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(QwenLogLocator.QwenDataDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Missing_configured_directory_yields_no_data()
    {
        var missing = Path.Combine(CreateTempDir(), "qwen-missing");

        var result = await CreateAdapter(missing).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Roots.Should().ContainSingle().Which.Should().Be(missing);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    // ---- 排序与定价 ----

    [Fact]
    public async Task Records_are_sorted_by_timestamp_across_files()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/p/chats/b-later.jsonl",
            """{"type":"assistant","timestamp":"2026-02-24T00:00:00.000Z","sessionId":"b","usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}""");
        WriteChat(root, "projects/p/chats/a-earlier.jsonl",
            """{"type":"assistant","timestamp":"2026-02-23T00:00:00.000Z","sessionId":"a","usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}""");

        var result = await CreateAdapter(root).ScanAsync();

        // 蓝本 entries.sort_by_key(timestamp)：稳定排序（文件发现序不受文件名字母序影响）。
        result.Records.Select(record => record.SessionId).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Prices_records_via_builtin_snapshot_qwen_section()
    {
        var root = CreateTempDataDir();
        WriteChat(root, "projects/p/chats/chat.jsonl",
            """{"type":"assistant","model":"qwen3-coder-plus","timestamp":"2026-02-23T14:24:56.857Z","sessionId":"s","usageMetadata":{"promptTokenCount":10000,"candidatesTokenCount":5000,"cachedContentTokenCount":4000}}""");

        var result = await CreateAdapter(root).ScanAsync();
        var engine = new PricingEngine();

        var priced = engine.PriceRecord(result.Records.Should().ContainSingle().Subject);
        priced.Source.Should().Be(PricingSourceKind.Snapshot);
        // 10000×$1/M + 5000×$5/M + 4000×$0.1/M = $0.01 + $0.025 + $0.0004
        // （快照裸名键无 cache_read 价，按 input×0.1 推导——与 CLI 计价同式，golden 逐位一致）
        priced.CostUsd.Should().Be(0.0354m);
        engine.GetUnpricedModels().Should().BeEmpty();
    }

    // ---- 项目名提取（蓝本 project_from_file 滑窗） ----

    [Fact]
    public void Project_from_path_extracts_project_between_projects_and_chats()
    {
        QwenLogLocator.ProjectFromPath("/home/u/.qwen/projects/myProject/chats/chat.jsonl")
            .Should().Be("myProject");
        // 多层同名目录：自后向前的滑窗命中最后一个 projects/chats 对。
        QwenLogLocator.ProjectFromPath("/x/projects/a/projects/b/chats/c.jsonl")
            .Should().Be("b");
        QwenLogLocator.ProjectFromPath("/home/u/other/chats/chat.jsonl")
            .Should().BeNull("无 projects/chats 滑窗命中");
        QwenLogLocator.ProjectFromPath("short.jsonl").Should().BeNull();
    }

    // ---- helpers ----

    private QwenCodeAdapter CreateAdapter(string dataDir) => new(configuredDataDir: dataDir);

    private string CreateTempDataDir()
    {
        var path = Path.Combine(CreateTempDir(), "qwen");
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-qwen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private static void WriteChat(string root, string relativePath, params string[] lines)
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
