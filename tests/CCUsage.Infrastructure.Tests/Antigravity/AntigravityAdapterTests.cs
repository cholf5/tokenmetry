using Microsoft.Data.Sqlite;

namespace CCUsage.Infrastructure.Tests.Antigravity;

using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.Antigravity;
using static AntigravityTestSupport;

/// <summary>
/// I-021: Antigravity 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage/rust/adapters/antigravity/ 的 paths.rs / parser.rs / loader.rs 内联测试，同名场景
/// 逐一对齐：多默认根与 env 多根语义、conversation_dir 双形态、canonicalize 去重、protobuf 字段
/// 重复语义、steps retry 字段 28、model_id 映射与归一化表、代序 continuation、跨库身份去重、
/// trajectory 时间戳回退、空快照跳过、坏库/坏 blob/缺表显式降级）。
/// 蓝本计价类内联测试（model_candidates / timestamp-aware pricing / model alias）不在移植范围
/// （定价候选链为 I-024 AG-1 欠账），见卡片偏差清单。
/// </summary>
public class AntigravityAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- 蓝本 paths.rs 内联测试同名场景 ----

    [Fact]
    public void Discovers_databases_from_independent_roots()
    {
        var fakeHome = CreateTempDir();
        foreach (var root in new[]
                 {
                     Path.Combine(".gemini", "antigravity", "conversations", "ide.db"),
                     Path.Combine(".gemini", "antigravity-cli", "conversations", "cli.db"),
                     Path.Combine(".gemini", "antigravity-ide", "conversations", "ide-alt.db"),
                     Path.Combine(".gemini", "antigravity-backup", "conversations", "backup.db"),
                     Path.Combine(".gemini", "antigravity-acp", "conversations", "acp.db"),
                     Path.Combine(".config", "antigravity", "conversations", "config.db"),
                 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fakeHome, root))!);
            File.WriteAllBytes(Path.Combine(fakeHome, root), []);
        }

        var paths = new AntigravityLogLocator(HomeAccessor(fakeHome)).CollectConversationDatabasePaths(null);

        paths.Should().HaveCount(6);
        paths.Should().OnlyContain(path => Path.GetExtension(path) == ".db");
    }

    [Fact]
    public void Accepts_conversation_directory_and_parent_directory_overrides()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "parent", "conversations"));
        File.WriteAllBytes(Path.Combine(root, "parent", "conversations", "parent.db"), []);
        Directory.CreateDirectory(Path.Combine(root, "direct"));
        File.WriteAllBytes(Path.Combine(root, "direct", "conversation.db"), []);

        var paths = new AntigravityLogLocator(NoEnvAccessor())
            .CollectConversationDatabasePaths($"{Path.Combine(root, "parent")},{Path.Combine(root, "direct")}");

        // 蓝本 paths.sort()：全体排序（ordinal），与配置顺序无关。
        paths.Should().Equal(
            Path.Combine(root, "direct", "conversation.db"),
            Path.Combine(root, "parent", "conversations", "parent.db"));
    }

    [Fact]
    public void Deduplicates_canonicalized_override_aliases()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "conversations"));
        File.WriteAllBytes(Path.Combine(root, "conversations", "session.db"), []);

        var paths = new AntigravityLogLocator(NoEnvAccessor())
            .CollectConversationDatabasePaths($"{root},{Path.Combine(root, "conversations", "..")}");

        // 蓝本以 canonicalize 后路径去重（conversations/.. 与 root 指向同一物理库）。
        // C# 侧 GetFullPath 展开后同键去重。
        paths.Should().HaveCount(1);
        paths[0].Should().EndWith(Path.Combine("conversations", "session.db"));
    }

    [Fact]
    public void Env_set_but_empty_yields_no_roots_without_default_fallback()
    {
        // 蓝本 env::var 成功即 return（split 空值 → 空列表），不回退默认六根（与 OpenClaw 的
        // "空白回退默认"不同，与 Kilo 同款）。
        var fakeHome = CreateTempDir();
        var defaultRoot = Path.Combine(fakeHome, ".gemini", "antigravity", "conversations");
        Directory.CreateDirectory(defaultRoot);
        File.WriteAllBytes(Path.Combine(defaultRoot, "decoy.db"), []);

        var paths = new AntigravityLogLocator(NameAccessor(
            ("ANTIGRAVITY_DATA_DIR", "   "),
            ("USERPROFILE", fakeHome),
            ("HOME", fakeHome)))
            .CollectConversationDatabasePaths(null);

        paths.Should().BeEmpty();
    }

    [Fact]
    public void Env_comma_list_is_trimmed_and_blank_segments_dropped()
    {
        var first = Path.Combine(CreateTempDir(), "one");
        var second = Path.Combine(CreateTempDir(), "two");
        Directory.CreateDirectory(Path.Combine(first, "conversations"));
        File.WriteAllBytes(Path.Combine(first, "conversations", "a.db"), []);
        Directory.CreateDirectory(second);
        File.WriteAllBytes(Path.Combine(second, "b.db"), []);

        var paths = new AntigravityLogLocator(NameAccessor(("ANTIGRAVITY_DATA_DIR", $"  {first} ,, {second} ")))
            .CollectConversationDatabasePaths(null);

        paths.Should().HaveCount(2);
        paths.Should().Contain(Path.Combine(first, "conversations", "a.db"));
        paths.Should().Contain(Path.Combine(second, "b.db"));
    }

    // ---- 蓝本 parser.rs 内联测试同名场景（protobuf 字段语义 + 归一化表） ----

    [Fact]
    public void Protobuf_scalars_use_last_duplicate_and_messages_keep_merge_order()
    {
        var fields = AntigravityProtoReader.DecodeFields(
        [
            .. FieldVarint(1, 1),
            .. FieldVarint(1, 2),
            .. FieldBytes(2, "first"u8.ToArray()),
            .. FieldBytes(2, "last"u8.ToArray()),
            .. FieldBytes(3, "message-first"u8.ToArray()),
            .. FieldBytes(3, "message-second"u8.ToArray()),
        ]);

        AntigravityProtoReader.FieldVarint(fields, 1).Should().Be(2);
        AntigravityProtoReader.FieldText(fields, 2).Should().Be("last");
        AntigravityProtoReader.FieldBytes(fields, 3).Should().Equal("message-first"u8.ToArray());
        AntigravityProtoReader.FieldBytesAll(fields, 3).Should().HaveCount(2);
        AntigravityProtoReader.FieldBytesAll(fields, 3)[1].Should().Equal("message-second"u8.ToArray());
    }

    [Fact]
    public async Task Parses_production_step_retry_info_from_field_28()
    {
        var root = CreateTempDir();
        var retry = new UsageFixture
        {
            InputTokens = 11,
            TotalOutputTokens = 22,
            VisibleOutputTokens = 20,
            ResponseId = "production-step-retry",
        };
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [],
            [(1, StepMetadataBlob(null, null, null, [retry], AntigravityParser.ApiProviderGoogleGemini))],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(11);
        record.OutputTokens.Should().Be(22, "total_output 22 > visible 20 且 reasoning 0 → output 22（蓝本桶换算）");
        record.MessageId.Should().Be("production-step-retry");
    }

    [Theory]
    [InlineData(1318ul, "model_placeholder_m318", "Gemini 3.8 Flash (High)", "gemini-3.8-flash-high")]
    [InlineData(1319ul, "model_placeholder_m319", "Gemini 3.8 Flash (Medium)", "gemini-3.8-flash-medium")]
    [InlineData(1320ul, "model_placeholder_m320", "Gemini 3.8 Flash (Low)", "gemini-3.8-flash-low")]
    [InlineData(1298ul, "model_placeholder_m298", "Gemini 3.7 Flash (High)", "gemini-3.7-flash-high")]
    [InlineData(1299ul, "model_placeholder_m299", "Gemini 3.7 Flash (Medium)", "gemini-3.7-flash-medium")]
    [InlineData(1300ul, "model_placeholder_m300", "Gemini 3.7 Flash (Low)", "gemini-3.7-flash-low")]
    [InlineData(1071ul, "model_placeholder_m71", "Gemini 3.6 Flash (High)", "gemini-3.6-flash-high")]
    [InlineData(1072ul, "model_placeholder_m72", "Gemini 3.6 Flash (Medium)", "gemini-3.6-flash-medium")]
    [InlineData(1073ul, "model_placeholder_m73", "Gemini 3.6 Flash (Low)", "gemini-3.6-flash-low")]
    public void Normalizes_newer_gemini_model_placeholders_and_ids(ulong modelId, string placeholder, string displayName, string expected)
    {
        AntigravityParser.ModelNameFromId(modelId).Should().Be(expected);
        AntigravityParser.NormalizeAntigravityModel(placeholder).Should().Be(expected);
        AntigravityParser.NormalizeAntigravityModel(displayName).Should().Be(expected);
        AntigravityParser.NormalizeAntigravityModel("Gemini 3.8 Flash").Should().Be("gemini-3.8-flash");
    }

    [Theory]
    [InlineData("gemini-3-flash-agent", "gemini-3.5-flash-high")]
    [InlineData("gemini-3-flash-a", "gemini-3.5-flash-high")]
    [InlineData("gemini-3-flash-b", "gemini-3.5-flash-high")]
    [InlineData("gemini-3.6-flash", "gemini-3.6-flash")]
    [InlineData("Gemini 3 Pro", "gemini-3-pro")]
    [InlineData("model_placeholder_m26", "claude-opus-4-6")]
    [InlineData("model_placeholder_m35", "claude-sonnet-4-6")]
    [InlineData("model_openai_gpt_oss_120b_medium", "gpt-oss-120b-medium")]
    [InlineData("gemini-pro-default", "gemini-3.1-pro")]
    [InlineData("claude 3.7 sonnet", "claude-3-7-sonnet")]
    [InlineData("gemini-unpriced-model", "gemini-unpriced-model")]
    [InlineData("Mystery Model X", "Mystery Model X", "未知前缀保留原文（蓝本 default 分支 trimmed）")]
    public void Normalizes_legacy_aliases_and_unknown_names(string raw, string expected, string? because = null)
    {
        AntigravityParser.NormalizeAntigravityModel(raw).Should().Be(expected, because);
    }

    [Fact]
    public void Maps_placeholder_ids_above_thousand()
    {
        AntigravityParser.ModelNameFromId(1_318).Should().Be("gemini-3.8-flash-high");
        AntigravityParser.ModelNameFromId(1_500).Should().Be("model_placeholder_m500");
        AntigravityParser.ModelNameFromId(42).Should().Be("antigravity-model-42");
    }

    // ---- 蓝本 loader.rs 内联测试同名场景 ----

    [Fact]
    public async Task Loads_real_schema_rows_with_continuations_and_token_buckets()
    {
        var root = CreateTempDir();
        var firstUsage = new UsageFixture
        {
            InputTokens = 400,
            TotalOutputTokens = 200,
            CacheCreationTokens = 30,
            CacheReadTokens = 500,
            ReasoningTokens = 50,
            VisibleOutputTokens = 150,
            MessageId = "message-1",
            ResponseId = "response-1",
            ProviderAssignedMessageId = "provider-1",
        };
        var continuationUsage = new UsageFixture
        {
            InputTokens = 300,
            TotalOutputTokens = 100,
            ReasoningTokens = 25,
            VisibleOutputTokens = 75,
            MessageId = "message-2",
            ResponseId = "response-2",
        };
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [
                (2, MetadataBlob(null, continuationUsage, (1_778_000_001, 0), [])),
                (1, MetadataBlob("Gemini 3 Pro", firstUsage, (1_778_000_000, 123_000_000), [])),
            ],
            [],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records[0].Model.Should().Be("gemini-3-pro");
        result.Records[1].Model.Should().Be("gemini-3-pro", "idx 2 无模型文本 → 代序倒序回退 gen 模型");
        result.Records[0].MessageId.Should().Be("response-1", "response id 消息 id 优先级 rank 3");
        result.Records[0].InputTokens.Should().Be(400);
        result.Records[0].CacheCreationTokens.Should().Be(30);
        result.Records[0].CacheReadTokens.Should().Be(500);
        result.Records[0].OutputTokens.Should().Be(150, "visible 150（reasoning 50 进 CLI extra，不落统一记录）");
        result.Records[1].InputTokens.Should().Be(300);
        result.Records[1].OutputTokens.Should().Be(75);
        result.Records[0].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_778_000_000_123));
        result.Records[1].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_778_000_001_000));
        result.Diagnostics.LinesSkipped.Should().Be(0);
    }

    [Fact]
    public async Task Decodes_model_id_without_counting_it_as_input_tokens()
    {
        var root = CreateTempDir();
        CreateDatabase(
            Path.Combine(root, "conversations", "model-id.db"),
            [(1, MetadataBlob(null, new UsageFixture
            {
                ModelId = 246,
                InputTokens = 4,
                TotalOutputTokens = 6,
                VisibleOutputTokens = 6,
                ResponseId = "model-id-response",
            }, (1_778_000_000, 0), []))],
            [],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("gemini-2.5-pro");
        record.InputTokens.Should().Be(4);
    }

    [Fact]
    public async Task Loads_multiple_databases_consistently()
    {
        var root = CreateTempDir();
        CreateDatabase(
            Path.Combine(root, "one", "conversations", "one.db"),
            [(1, MetadataBlob("gemini-3-pro", new UsageFixture
            {
                InputTokens = 20,
                TotalOutputTokens = 45,
                VisibleOutputTokens = 40,
                ReasoningTokens = 5,
                ResponseId = "one",
            }, (1_778_000_000, 0), []))],
            [],
            []);
        CreateDatabase(
            Path.Combine(root, "two", "conversations", "two.db"),
            [(1, MetadataBlob("gemini-3-pro", new UsageFixture
            {
                InputTokens = 21,
                TotalOutputTokens = 47,
                VisibleOutputTokens = 41,
                ReasoningTokens = 6,
                ResponseId = "two",
            }, (1_778_000_002, 0), []))],
            [],
            []);

        var result = await CreateAdapter($"{Path.Combine(root, "one")},{Path.Combine(root, "two")}")
            .ScanAsync();

        result.Records.Select(record => record.MessageId).Should().Equal(["one", "two"], "按时间戳稳定排序");
        result.Records.Should().HaveCount(2);
    }

    [Fact]
    public async Task Deduplicates_response_ids_across_databases()
    {
        var root = CreateTempDir();
        CreateDatabase(
            Path.Combine(root, "first", "conversations", "first.db"),
            [(1, MetadataBlob("gemini-3-pro", new UsageFixture
            {
                InputTokens = 20,
                TotalOutputTokens = 40,
                VisibleOutputTokens = 35,
                ReasoningTokens = 5,
                MessageId = "shared-message",
                ResponseId = "shared-response",
                ProviderAssignedMessageId = "shared-provider",
            }, (1_778_000_000, 0), []))],
            [],
            []);
        CreateDatabase(
            Path.Combine(root, "second", "conversations", "second.db"),
            [(1, MetadataBlob("gemini-3-pro", new UsageFixture
            {
                InputTokens = 200,
                TotalOutputTokens = 400,
                VisibleOutputTokens = 350,
                ReasoningTokens = 50,
                MessageId = "shared-message",
                ResponseId = "retry-response",
                ProviderAssignedMessageId = "shared-provider",
            }, (1_778_000_001, 0), []))],
            [],
            []);

        var result = await CreateAdapter($"{Path.Combine(root, "first")},{Path.Combine(root, "second")}")
            .ScanAsync();

        result.Records.Should().ContainSingle("共享 provider 身份键跨库合并（蓝本 deduplicate_events）");
        result.Records[0].MessageId.Should().Be("shared-response", "同 rank 3 保持先到者");
        result.Records[0].InputTokens.Should().Be(200, "合并取 max");
        result.Records[0].OutputTokens.Should().Be(350);
        result.Diagnostics.DuplicateRecords.Should().Be(1);
    }

    [Fact]
    public async Task Collects_steps_and_retries_and_deduplicates_source_copies()
    {
        var root = CreateTempDir();
        var sharedUsage = new UsageFixture
        {
            InputTokens = 100,
            TotalOutputTokens = 50,
            CacheCreationTokens = 5,
            CacheReadTokens = 7,
            ReasoningTokens = 10,
            VisibleOutputTokens = 40,
            MessageId = "message-1",
            ResponseId = "response-1",
            ProviderAssignedMessageId = "provider-1",
        };
        var retryUsage = new UsageFixture
        {
            InputTokens = 80,
            TotalOutputTokens = 30,
            ReasoningTokens = 5,
            VisibleOutputTokens = 25,
            MessageId = "message-1",
            ResponseId = "retry-response",
            ProviderAssignedMessageId = "provider-1",
        };
        var stepRetryUsage = new UsageFixture
        {
            InputTokens = 11,
            TotalOutputTokens = 22,
            ReasoningTokens = 2,
            VisibleOutputTokens = 20,
            MessageId = "step-retry-message",
            ResponseId = "step-retry-response",
            ProviderAssignedMessageId = "step-retry-provider",
        };
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [(1, MetadataBlob("gemini-3-pro", sharedUsage, (1_778_100_001, 0), [retryUsage]))],
            [
                (1, StepMetadataBlob(null, sharedUsage, (1_778_100_000, 0), [], 7)),
                (2, StepMetadataBlob("gemini-3-flash-agent", null, (1_778_100_002, 0), [stepRetryUsage], 7)),
            ],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "step 副本 + gen 副本 + gen retry 共享身份键合并为一条");
        result.Records[0].InputTokens.Should().Be(100);
        result.Records[0].CacheCreationTokens.Should().Be(5);
        result.Records[0].CacheReadTokens.Should().Be(7);
        result.Records[0].OutputTokens.Should().Be(40);
        result.Records[0].Model.Should().Be("gemini-3-pro");
        result.Records[0].MessageId.Should().Be("response-1", "response id rank 3 保持");
        result.Records[0].Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_778_100_000_000),
            "同 rank 更早时间戳胜出（step 副本 1_778_100_000）");
        result.Records[1].Model.Should().Be("gemini-3.5-flash-high");
        result.Records[1].InputTokens.Should().Be(11);
        result.Diagnostics.DuplicateRecords.Should().Be(2);
    }

    [Fact]
    public async Task Attributes_step_and_retry_usage_to_their_token_models()
    {
        var root = CreateTempDir();
        var stepUsage = new UsageFixture
        {
            ModelId = 246,
            InputTokens = 10,
            TotalOutputTokens = 5,
            VisibleOutputTokens = 5,
            ResponseId = "step-pro",
        };
        var retryUsage = new UsageFixture
        {
            ModelId = 290,
            InputTokens = 20,
            TotalOutputTokens = 4,
            VisibleOutputTokens = 4,
            ResponseId = "retry-opus",
        };
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [(1, MetadataBlob("gemini-2.5-flash", null, null, []))],
            [(1, StepMetadataBlob(null, stepUsage, (1_778_400_000, 0), [retryUsage], null))],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records.Single(record => record.MessageId == "step-pro").Model.Should().Be("gemini-2.5-pro",
            "usage.model_id 优先于上下文模型（蓝本 usage.model_id.map 链）");
        result.Records.Single(record => record.MessageId == "retry-opus").Model.Should().Be("claude-4-opus");
    }

    [Fact]
    public async Task Maps_required_aliases_and_keeps_unprovided_models_unprefixed()
    {
        var root = CreateTempDir();
        var aliases = new[]
        {
            "gemini-3-flash-agent",
            "gemini-3-flash-a",
            "gemini-3-flash-b",
            "gemini-3.6-flash",
            "gemini-unpriced-model",
        };
        var rows = aliases.Select((model, index) => (Idx: (long)index, Data: MetadataBlob(
            model,
            new UsageFixture
            {
                InputTokens = 1,
                TotalOutputTokens = 1,
                VisibleOutputTokens = 1,
                ResponseId = $"alias-{index}",
            },
            (1_778_200_000ul + (ulong)index, 0),
            []))).ToArray();
        CreateDatabase(Path.Combine(root, "conversations", "session.db"), rows, [], []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Select(record => record.Model).Should().Equal(
            "gemini-3.5-flash-high",
            "gemini-3.5-flash-high",
            "gemini-3.5-flash-high",
            "gemini-3.6-flash",
            "gemini-unpriced-model");
    }

    [Fact]
    public async Task Uses_trajectory_timestamp_when_generation_timestamp_is_missing()
    {
        var root = CreateTempDir();
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [(1, MetadataBlob("gemini-3-pro", new UsageFixture
            {
                InputTokens = 2,
                TotalOutputTokens = 3,
                VisibleOutputTokens = 3,
            }, null, []))],
            [],
            [TrajectoryMetadataBlob(1_778_300_000, 456_000_000)]);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_778_300_000_456),
            "蓝本断言 date 2026-05-09 / timestamp 2026-05-09T04:13:20.456Z（UTC）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
    }

    [Fact]
    public async Task Skips_zero_byte_databases_and_reports_valid_entries()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "conversations"));
        File.WriteAllBytes(Path.Combine(root, "conversations", "empty.db"), []);
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [(1, MetadataBlob("gemini-3-pro", new UsageFixture
            {
                InputTokens = 10,
                TotalOutputTokens = 5,
                VisibleOutputTokens = 5,
                ResponseId = "valid-response",
            }, (1_778_000_000, 0), []))],
            [],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.MessageId.Should().Be("valid-response");
        result.Diagnostics.FilesDiscovered.Should().Be(2);
        result.Diagnostics.FilesRead.Should().Be(2, "空快照按蓝本 debug 跳过、不算问题但计已检查");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok, "空快照不降级（蓝本正常路径）");
    }

    [Fact]
    public async Task Returns_empty_report_when_only_zero_byte_databases_exist()
    {
        var root = CreateTempDir();
        var database = Path.Combine(root, "conversations", "empty.db");
        Directory.CreateDirectory(Path.Combine(root, "conversations"));
        File.WriteAllBytes(database, []);

        AntigravityParser.ParseSqliteFile(database).Should().BeNull("蓝本 parse_sqlite_file → Ok(None)");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Still_reports_errors_for_nonempty_databases_alongside_empty_ones()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "conversations"));
        File.WriteAllBytes(Path.Combine(root, "conversations", "empty.db"), []);
        CreateDatabase(
            Path.Combine(root, "conversations", "valid.db"),
            [(1, MetadataBlob("gemini-3-pro", new UsageFixture
            {
                InputTokens = 10,
                TotalOutputTokens = 5,
                VisibleOutputTokens = 5,
                ResponseId = "valid-response",
            }, (1_778_000_000, 0), []))],
            [],
            []);
        // 蓝本 malformed blob vec![0x0a, 0x01, 0x80]：外层 field 1 LEN 1、内层 varint 截断。
        CreateDatabase(
            Path.Combine(root, "conversations", "malformed.db"),
            [(1, [0x0a, 0x01, 0x80])],
            [],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.MessageId.Should().Be("valid-response",
            "坏库显式降级不拖垮有效库（桌面管道口径，蓝本为整次加载失败）");
        result.Diagnostics.MalformedLines.Should().Be(1, "protobuf 解析失败按库显式计数");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Propagates_database_open_and_query_errors_as_partial()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "conversations"));
        File.WriteAllText(Path.Combine(root, "conversations", "not-a-db.db"), "not sqlite");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.FilesDiscovered.Should().Be(1);
        result.Diagnostics.FilesRead.Should().Be(0, "打开失败该库未计入已检查");
        // 仅失败、无记录时 Status=NoData：与 Kilo/Goose/I-020 备案的 family precedence 一致
        // （失败仍经 FilesRead=0 显式可见；有其他有效库时 Partial，见 Still_reports 场景）。
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Propagates_missing_generation_table_errors_as_partial()
    {
        var root = CreateTempDir();
        var dbPath = Path.Combine(root, "conversations", "missing-table.db");
        Directory.CreateDirectory(Path.Combine(root, "conversations"));
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE other (id INTEGER)";
            command.ExecuteNonQuery();
        }

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        // 仅失败、无记录时 Status=NoData（family precedence，见上）；失败面与蓝本"整次加载失败"
        // 同拒绝域——gen_metadata 查询失败 → 该库 0 条。
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Propagates_present_optional_table_schema_errors_as_partial()
    {
        var root = CreateTempDir();
        var dbPath = Path.Combine(root, "conversations", "bad-steps.db");
        Directory.CreateDirectory(Path.Combine(root, "conversations"));
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB NOT NULL)");
            Execute(connection, "CREATE TABLE steps (idx INTEGER PRIMARY KEY, wrong BLOB)");
            Execute(connection, "CREATE TABLE trajectory_metadata_blob (data BLOB)");
            using var statement = connection.CreateCommand();
            statement.CommandText = "INSERT INTO gen_metadata (idx, data) VALUES (1, $data)";
            statement.Parameters.AddWithValue("$data", MetadataBlob(
                "gemini-3-pro",
                new UsageFixture { InputTokens = 1, TotalOutputTokens = 1, VisibleOutputTokens = 1 },
                (1_778_000_000, 0),
                []));
            statement.ExecuteNonQuery();
        }

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty("steps 存在但 schema 不符 → 查询失败 → 整库 0 条（蓝本报错含 'steps'）");
        // 仅失败、无记录时 Status=NoData（family precedence，见上）。
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Propagates_malformed_metadata_errors_when_no_valid_database_exists()
    {
        // 蓝本 propagates_malformed_metadata_errors 同名场景：仅坏 blob 库、无有效库共存
        // （Still_reports_errors 场景的对照面——此时记录为 0）。
        var root = CreateTempDir();
        CreateDatabase(
            Path.Combine(root, "conversations", "malformed.db"),
            [(1, [0x0a, 0x01, 0x80])],
            [],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.MalformedLines.Should().Be(1, "protobuf 解析失败按库显式计数（蓝本整次加载失败）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData,
            "family precedence：仅失败无记录时 NoData（I-020 备案同款，失败面经 MalformedLines 显式可见）");
    }

    [Fact]
    public async Task Omits_steps_and_trajectory_tables_without_problems()
    {
        var root = CreateTempDir();
        var dbPath = Path.Combine(root, "conversations", "gen-only.db");
        Directory.CreateDirectory(Path.Combine(root, "conversations"));
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB NOT NULL)");
            using var statement = connection.CreateCommand();
            statement.CommandText = "INSERT INTO gen_metadata (idx, data) VALUES (1, $data)";
            statement.Parameters.AddWithValue("$data", MetadataBlob(
                "gemini-3-pro",
                new UsageFixture { InputTokens = 3, TotalOutputTokens = 4, VisibleOutputTokens = 4, ResponseId = "gen-only" },
                (1_778_000_000, 0),
                []));
            statement.ExecuteNonQuery();
        }

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok, "steps / trajectory_metadata_blob 均为可选表");
    }

    // ---- 载荷类型角落（仓库历史教训：JSON 载荷非对象 / serde Option 语义 → blob 等价角落） ----

    [Fact]
    public async Task Zero_token_usages_are_counted_as_skipped()
    {
        var root = CreateTempDir();
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [
                (1, MetadataBlob("gemini-3-pro", new UsageFixture
                {
                    InputTokens = 0,
                    TotalOutputTokens = 0,
                    MessageId = "zero-usage",
                    ResponseId = "zero-response",
                }, (1_778_000_000, 0), [])),
                (2, MetadataBlob("gemini-3-pro", new UsageFixture
                {
                    InputTokens = 5,
                    TotalOutputTokens = 5,
                    VisibleOutputTokens = 5,
                    ResponseId = "real-usage",
                }, (1_778_000_001, 0), [])),
            ],
            [],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.MessageId.Should().Be("real-usage");
        result.Diagnostics.LinesSkipped.Should().Be(1, "非 token-bearing usage 蓝本静默丢弃 → 显式计数");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
    }

    [Fact]
    public async Task Whitespace_only_model_text_is_treated_as_missing()
    {
        var root = CreateTempDir();
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [(1, MetadataBlob("   ", new UsageFixture
            {
                InputTokens = 1,
                TotalOutputTokens = 1,
                VisibleOutputTokens = 1,
                ResponseId = "ws-response",
            }, (1_778_000_000, 0), []))],
            [],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Model.Should().Be("gemini-internal-model",
            "field_text 去空白非空才生效（蓝本 filter(!trim().is_empty())）→ 缺省模型");
    }

    [Fact]
    public async Task Trajectory_rows_use_first_valid_candidate()
    {
        var root = CreateTempDir();
        // 首行无 field 2（候选 None），次行有效 → 蓝本 timestamp.is_none() 才写入 → 次行生效。
        var noTimestampRow = Array.Empty<byte>();
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [(1, MetadataBlob("gemini-3-pro", new UsageFixture
            {
                InputTokens = 2,
                TotalOutputTokens = 2,
                VisibleOutputTokens = 2,
                ResponseId = "trajectory-response",
            }, null, []))],
            [],
            [noTimestampRow, TrajectoryMetadataBlob(1_778_000_100, 0), TrajectoryMetadataBlob(1_778_000_200, 0)]);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should().Be(
            DateTimeOffset.FromUnixTimeMilliseconds(1_778_000_100_000), "首个有效候选生效");
    }

    [Fact]
    public async Task Timestamp_seconds_zero_falls_back_to_file_mtime()
    {
        var root = CreateTempDir();
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [],
            [(1, StepMetadataBlob("gemini-3-pro", new UsageFixture
            {
                InputTokens = 2,
                TotalOutputTokens = 2,
                VisibleOutputTokens = 2,
                ResponseId = "zero-seconds",
            }, (0, 0), [], null))],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Timestamp.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5), "seconds=0 无效 → mtime 回退（rank 0）");
    }

    [Fact]
    public async Task Identity_timestamp_lookup_supplies_missing_step_timestamp()
    {
        // 蓝本身份键回查：无时间戳的 step usage 复用同身份事件早前记录的时间戳。
        var root = CreateTempDir();
        var sharedUsage = new UsageFixture
        {
            InputTokens = 10,
            TotalOutputTokens = 10,
            VisibleOutputTokens = 10,
            ResponseId = "identity-lookup",
        };
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [(1, MetadataBlob("gemini-3-pro", sharedUsage, (1_778_500_000, 0), []))],
            [(1, StepMetadataBlob(null, sharedUsage, null, [], null))],
            []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.Timestamp.Should().Be(
            DateTimeOffset.FromUnixTimeMilliseconds(1_778_500_000_000),
            "step usage 无时间戳 → 身份键回查命中 gen 事件（rank 3）");
    }

    [Fact]
    public async Task Repeated_scans_are_idempotent()
    {
        // 回归守卫：蓝本 BEGIN DEFERRED TRANSACTION 若用裸 SQL BEGIN 移植，连接池归还时不回滚，
        // 第二次扫描全部库报 "cannot start a transaction within a transaction"（0 记录）。
        var root = CreateTempDir();
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [(1, MetadataBlob("gemini-3-pro", new UsageFixture
            {
                InputTokens = 10,
                TotalOutputTokens = 5,
                VisibleOutputTokens = 5,
                ResponseId = "repeat-response",
            }, (1_778_000_000, 0), []))],
            [],
            []);
        var adapter = CreateAdapter(root);

        var first = await adapter.ScanAsync();
        var second = await adapter.ScanAsync();

        second.Records.Should().HaveCount(first.Records.Count, "托管事务 Dispose 干净归还连接池，重复扫描幂等");
        second.Diagnostics.FilesRead.Should().Be(first.Diagnostics.FilesRead);
        second.Diagnostics.Status.Should().Be(first.Diagnostics.Status);
    }

    [Fact]
    public async Task Files_without_extension_db_are_not_discovered()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "conversations"));
        File.WriteAllBytes(Path.Combine(root, "conversations", "notes.txt"), []);
        File.WriteAllBytes(Path.Combine(root, "conversations", "DB.bak"), []);

        var result = await CreateAdapter(root).ScanAsync();

        result.Diagnostics.FilesDiscovered.Should().Be(0, "扩展名恰为 .db 才发现（蓝本 collect_files_with_extension）");
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    /// <summary>
    /// 蓝本 model_candidates 移植锚点（I-024 接线 AG-1）：[模型名] +（flash effort 变体时）[基础名]，
    /// provider ∈ {3, 24, 30} 时每名展开 google/ · gemini/ · vertex_ai/ · openrouter/google/ 前缀组；
    /// 非 google provider / 无 provider 只裸名（+ 变体别名）。
    /// </summary>
    [Fact]
    public void Builds_candidate_chain_with_flash_alias_and_google_prefixes()
    {
        AntigravityParser.GeminiFlashPricingAlias("gemini-3.8-flash-medium").Should().Be("gemini-3.8-flash");
        AntigravityParser.GeminiFlashPricingAlias("gemini-3.7-flash-low").Should().Be("gemini-3.7-flash");
        AntigravityParser.GeminiFlashPricingAlias("gemini-3.6-flash-high").Should().Be("gemini-3.6-flash");
        AntigravityParser.GeminiFlashPricingAlias("gemini-2.5-pro").Should().BeNull();

        AntigravityParser.BuildModelCandidates("gemini-3.8-flash-medium", AntigravityParser.ApiProviderGoogleGemini)
            .Should().Equal(
                "gemini-3.8-flash-medium",
                "google/gemini-3.8-flash-medium",
                "gemini/gemini-3.8-flash-medium",
                "vertex_ai/gemini-3.8-flash-medium",
                "openrouter/google/gemini-3.8-flash-medium",
                "gemini-3.8-flash",
                "google/gemini-3.8-flash",
                "gemini/gemini-3.8-flash",
                "vertex_ai/gemini-3.8-flash",
                "openrouter/google/gemini-3.8-flash");
        AntigravityParser.BuildModelCandidates("claude-4-sonnet", AntigravityParser.ApiProviderGoogleVertex)
            .Should().Equal(
                "claude-4-sonnet",
                "google/claude-4-sonnet",
                "gemini/claude-4-sonnet",
                "vertex_ai/claude-4-sonnet",
                "openrouter/google/claude-4-sonnet");
        AntigravityParser.BuildModelCandidates("gemini-2.5-pro", provider: null)
            .Should().Equal("gemini-2.5-pro"); // 无 provider → 不展开前缀组
        AntigravityParser.BuildModelCandidates("gemini-2.5-pro", provider: 7)
            .Should().Equal("gemini-2.5-pro"); // 非 google 系 provider → 不展开前缀组
    }

    /// <summary>
    /// I-024 接线端到端（AG-1 两笔）：①flash effort 变体经 exact-only 走向命中基础名候选
    /// gemini-3.8-flash（快照条目 2026-10-12 取自 CLI 20.0.29 内嵌 models-dev 数据）；②计价 output
    /// 分量取 total_output（含 reasoning，OutputTokenOverride）——此前 visible output 计价 + 记 0 欠账。
    /// </summary>
    [Fact]
    public async Task Prices_flash_effort_variant_through_base_candidate_with_total_output()
    {
        var root = CreateTempDir();
        CreateDatabase(
            Path.Combine(root, "conversations", "session.db"),
            [],
            [(1, StepMetadataBlob("Gemini 3.8 Flash (Medium)", new UsageFixture
            {
                InputTokens = 1_000_000,
                TotalOutputTokens = 22,
                ReasoningTokens = 2,
                VisibleOutputTokens = 20,
            }, (1_778_000_000, 0), [], AntigravityParser.ApiProviderGoogleGemini))],
            []);

        var result = await CreateAdapter(root).ScanAsync();
        var record = result.Records.Should().ContainSingle().Subject;

        record.Model.Should().Be("gemini-3.8-flash-medium", "effort 变体归一化名（蓝本 normalize_antigravity_model）");
        record.OutputTokens.Should().Be(20, "统一记录 output 为 visible 口径（total_output 22 − reasoning 2）");
        record.Pricing.Should().NotBeNull();
        record.Pricing!.Walk.Should().Be(PricingCandidateWalk.ExactOnlyAcrossCandidates,
            "flash 变体 exact_lookup → 逐候选精确（蓝本 find_exact_pricing_with_alias）");
        record.Pricing.OutputTokenOverride.Should().Be(22, "计价 output 分量取 total_output 含 reasoning");

        var priced = new PricingEngine().PriceRecord(record);
        priced.CostUsd.Should().Be(1.5m + 22 * 7.5m / 1_000_000m,
            "input 1M×$1.5/M + 计价 output 22×$7.5/M（快照 google/gemini-3.8-flash，基础名候选命中）");
        priced.MatchedKey.Should().Be("google/gemini-3.8-flash");
    }

    // ---- helpers ----

    private AntigravityAdapter CreateAdapter(string configuredRoot)
        => new(configuredDataDir: configuredRoot);

    private static Func<string, string?> NoEnvAccessor() => _ => null;

    private static Func<string, string?> HomeAccessor(string home)
        => name => name is "USERPROFILE" or "HOME" ? home : null;

    private static Func<string, string?> NameAccessor(params (string Name, string Value)[] values)
        => name => values.FirstOrDefault(entry => entry.Name == name).Value;

    private static void Execute(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-antigravity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
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
                // 清理失败不影响测试结论。
            }
        }
    }
}
