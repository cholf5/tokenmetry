using System.Text;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.DshSession;
using ZstdSharp;

namespace Tokenmetry.Infrastructure.Tests.DshSession;

/// <summary>
/// I-007: DSH 会话适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../dsh-usage-stats 的 lib/usage.js fold 与 scripts/verify-raw.mjs 帧扫描器）。
/// zstd fixtures 在测试内用 ZstdSharp 现场压缩（DSH 每次追加一个独立帧的形态），无二进制文件入仓。
/// </summary>
public class DshSessionAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // 固定事件时间（epoch ms）：2026-08-10T10:00:00Z（flat 规则时代）/ 2026-08-24 周一上海高峰（weekday schedule 时代）。
    private const long FlatEraTime = 1_786_356_000_000;          // 2026-08-10T10:00:00Z
    private const long WeekdayEraTime = 1_787_536_800_000;       // 2026-08-24T02:00Z = 周一 10:00 Asia/Shanghai（高峰）

    [Fact]
    public async Task Parses_plain_jsonl_session_with_message_carrier()
    {
        var home = CreateTempHome();
        WriteSessionLog(home, "--Users-dev-dsh--", "session-aaa", zstd: false, version: null,
            Header("session-aaa", "/Users/dev/dsh"),
            Message(FlatEraTime, 1, turn: 1, step: 1, input: 1372, output: 339, cacheRead: 7488, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"),
            Message(FlatEraTime + 60_000, 2, turn: 2, step: 1, input: 100, output: 10, cacheRead: 0, cacheWrite: 20,
                provider: "ark", model: "glm-5.3-flash"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(2);
        var first = result.Records[0];
        first.Agent.Should().Be("DSH");
        first.ProjectPath.Should().Be("/Users/dev/dsh");
        first.SessionId.Should().Be("session-aaa");
        first.Model.Should().Be("ark/glm-5.3-flash");
        first.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(FlatEraTime));
        // 四桶口径：input 不含缓存切片，cacheWrite 落 CacheCreationTokens。
        first.InputTokens.Should().Be(1372);
        first.OutputTokens.Should().Be(339);
        first.CacheReadTokens.Should().Be(7488);
        first.CacheCreationTokens.Should().Be(0);
        first.TotalTokens.Should().Be(9199);

        var diagnostics = result.Diagnostics;
        diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        diagnostics.FilesDiscovered.Should().Be(1);
        diagnostics.FilesRead.Should().Be(1);
        diagnostics.LinesSkipped.Should().Be(1); // session 头行
        diagnostics.MalformedLines.Should().Be(0);
        diagnostics.InvalidRecords.Should().Be(0);
        // ark 路由不可计价 = 缺价（A-0b）：此前记已知 0 不计入，现按 null 缺价如实计数。
        diagnostics.MissingCostRecords.Should().Be(2);
        diagnostics.UnknownModelRecords.Should().Be(0);
    }

    [Fact]
    public async Task Parses_multiframe_zstd_log_and_tolerates_torn_tail()
    {
        var home = CreateTempHome();
        var lines = new[]
        {
            Header("session-zstd", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 10, output: 1, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"),
            Message(WeekdayEraTime + 1, 2, turn: 2, step: 1, input: 20, output: 2, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"),
        };
        var path = WriteSessionLog(home, "--tmp-proj--", "session-zstd", zstd: true, version: 2,
            frames: [lines[..2], lines[2..]]);

        // 模拟追加进程半途而废：截掉最后一帧的 60%。
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(int)(bytes.Length * 0.94)]);

        var result = await CreateAdapter(home).ScanAsync();

        // 完整帧照常产出，半帧内容不进入解析。
        result.Records.Should().ContainSingle();
        result.Records[0].InputTokens.Should().Be(10);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
        result.Diagnostics.FilesRead.Should().Be(1);
    }

    [Fact]
    public async Task Reads_session_log_while_agent_holds_write_handle()
    {
        // B-010（Windows 冒烟后续）：agent 持写句柄追加日志期间采集不静默缺数据。
        // 旧 FileShare.Read 对写句柄即 sharing violation——Unix 空操作恒绿，Windows 旧实现即红。
        var home = CreateTempHome();
        var path = WriteSessionLog(home, "--tmp-proj--", "session-live", zstd: false, version: null,
            Header("session-live", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 10, output: 1, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));
        using var agentHandle = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.FilesRead.Should().Be(1);
    }

    [Fact]
    public async Task Junction_to_sibling_session_directory_is_not_followed()
    {
        // B-011（Windows 冒烟后续）：junction 指向另一会话目录时，同一物理文件以两个路径被
        // artifactByPath 记两份账——DSH 无消息级去重（重复保证由文件级记账承担），旧实现直接双计。
        // 与共享收集器同口径：reparse point 不跟随。
        var home = CreateTempHome();
        WriteSessionLog(home, "--tmp-proj--", "session-real", zstd: false, version: null,
            Header("session-real", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 10, output: 1, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));
        CreateReparseDirectoryLink(
            Path.Combine(home, "sessions", "session-link"),
            Path.Combine(home, "sessions", "--tmp-proj--", "session-real"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task Self_referential_junction_does_not_recurse_forever()
    {
        // B-011：junction 成环旧实现无限递归 → StackOverflowException（不可捕获，进程死）。
        // 旧实现下本用例会击穿测试宿主而非红——只在修后随同 sibling 用例一起运行。
        var home = CreateTempHome();
        WriteSessionLog(home, "--tmp-proj--", "session-real", zstd: false, version: null,
            Header("session-real", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 10, output: 1, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));
        CreateReparseDirectoryLink(
            Path.Combine(home, "sessions", "loop"),
            Path.Combine(home, "sessions"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task Reads_only_highest_generation_per_session_directory()
    {
        var home = CreateTempHome();
        var projectDir = Path.Combine(home, "sessions", "--Users-dev-dsh--", "session-gen");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "session.jsonl"), Header("session-gen", "/old") + "\n");
        WriteCompressedFrames(Path.Combine(projectDir, "session.v3.jsonl.zstd"),
            [Encoding.UTF8.GetBytes(Header("session-gen", "/v3") + "\n")]);
        File.WriteAllText(Path.Combine(projectDir, "session.v4.jsonl"),
            Header("session-gen", "/v4-cwd") + "\n" +
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 5, output: 5, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash") + "\n");

        var result = await CreateAdapter(home).ScanAsync();

        // 只读最高版本：v0 / v3 的内容不出现。
        result.Records.Should().ContainSingle();
        result.Records[0].ProjectPath.Should().Be("/v4-cwd");
        result.Diagnostics.FilesDiscovered.Should().Be(1);
    }

    [Fact]
    public async Task Attributes_chunk_samples_from_last_request_header()
    {
        var home = CreateTempHome();
        WriteSessionLog(home, "--tmp-proj--", "session-chunk", zstd: false, version: null,
            Header("session-chunk", "/tmp/proj"),
            RequestHeader(WeekdayEraTime, 1, provider: "ark", model: "glm-5.3-flash"),
            // chunk 载体：无 source，归因回退最近 request/header。
            Chunk(WeekdayEraTime + 1, 2, turn: 1, step: 1, input: 7, output: 3, cacheRead: 0, cacheWrite: 0),
            // assistant/message 不推进 request/header 游标：本行无 source，不改变下一 chunk 的归因。
            Message(WeekdayEraTime + 2, 3, turn: 1, step: 2, input: 1, output: 1, cacheRead: 0, cacheWrite: 0,
                provider: null, model: null),
            Chunk(WeekdayEraTime + 3, 4, turn: 2, step: 1, input: 2, output: 2, cacheRead: 0, cacheWrite: 0));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(3);
        result.Records.Should().OnlyContain(record => record.Model == "ark/glm-5.3-flash");
    }

    [Fact]
    public async Task Message_carrier_uses_message_source_over_header_cursor()
    {
        var home = CreateTempHome();
        WriteSessionLog(home, "--tmp-proj--", "session-src", zstd: false, version: null,
            Header("session-src", "/tmp/proj"),
            RequestHeader(WeekdayEraTime, 1, provider: "ark", model: "glm-5.3-flash"),
            Message(WeekdayEraTime + 1, 2, turn: 1, step: 1, input: 9, output: 1, cacheRead: 0, cacheWrite: 0,
                provider: "deepseek-official", model: "deepseek-flash"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Single().Model.Should().Be("deepseek-official/deepseek-flash");
    }

    [Fact]
    public async Task Unknown_attribution_lands_in_unknown_unknown()
    {
        var home = CreateTempHome();
        WriteSessionLog(home, "--tmp-proj--", "session-unknown", zstd: false, version: null,
            Header("session-unknown", "/tmp/proj"),
            Chunk(WeekdayEraTime, 1, turn: 1, step: 1, input: 4, output: 4, cacheRead: 0, cacheWrite: 0));

        var result = await CreateAdapter(home).ScanAsync();

        var record = result.Records.Single();
        record.Model.Should().Be("unknown/unknown");
        record.CostUsd.Should().BeNull(); // 非 deepseek 路由 fail-closed = 缺价 null（不折已知 0，A-0b）
    }

    [Fact]
    public async Task Adjacent_same_turn_step_replaces_sample_and_reattributes_day()
    {
        var home = CreateTempHome();
        // 同 (1,1) 的第二个样本覆盖第一个（含跨日重归属：t+1ms 已是次日）。
        var nextDay = WeekdayEraTime + 86_400_000 - 3_600_000; // 次日早些时候（本地时区归属由测试时间保证为次日 UTC 日）
        WriteSessionLog(home, "--tmp-proj--", "session-replace", zstd: false, version: null,
            Header("session-replace", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 100, output: 10, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"),
            Message(nextDay, 2, turn: 1, step: 1, input: 60, output: 6, cacheRead: 5, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        var record = result.Records[0];
        record.InputTokens.Should().Be(60); // 替换不是累加
        record.OutputTokens.Should().Be(6);
        record.CacheReadTokens.Should().Be(5);
        record.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(nextDay));
    }

    [Fact]
    public async Task Non_adjacent_same_turn_step_samples_are_kept_like_blueprint()
    {
        var home = CreateTempHome();
        // 蓝本 lastSample 游标只对相邻样本生效：A、B、A' 三个样本全部计入（对账口径以蓝本为准）。
        WriteSessionLog(home, "--tmp-proj--", "session-nonadj", zstd: false, version: null,
            Header("session-nonadj", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 10, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"),
            Message(WeekdayEraTime + 1, 2, turn: 2, step: 1, input: 20, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"),
            Message(WeekdayEraTime + 2, 3, turn: 1, step: 1, input: 30, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(3);
        result.Records.Sum(record => record.InputTokens).Should().Be(60);
    }

    [Fact]
    public async Task All_zero_warmup_samples_are_filtered_silently()
    {
        var home = CreateTempHome();
        WriteSessionLog(home, "--tmp-proj--", "session-warmup", zstd: false, version: null,
            Header("session-warmup", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 0, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"),
            Message(WeekdayEraTime + 1, 2, turn: 2, step: 1, input: 8, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok); // warmup 不算 invalid
        result.Diagnostics.InvalidRecords.Should().Be(0);
    }

    [Fact]
    public async Task Zero_sample_replacing_adjacent_nonzero_sample_yields_no_record()
    {
        var home = CreateTempHome();
        WriteSessionLog(home, "--tmp-proj--", "session-zero-replace", zstd: false, version: null,
            Header("session-zero-replace", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 50, output: 5, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"),
            Message(WeekdayEraTime + 1, 2, turn: 1, step: 1, input: 0, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Corrupt_frame_poisons_whole_file_like_blueprint()
    {
        var home = CreateTempHome();
        var path = WriteSessionLog(home, "--tmp-proj--", "session-corrupt", zstd: true, version: null,
            frames: new[]
            {
                new[] { Header("session-corrupt", "/tmp/proj") },
            });
        // 同 home 再放一个健康会话，验证坏文件降级整体状态、好文件照常产出。
        WriteSessionLog(home, "--tmp-proj--", "session-healthy", zstd: false, version: null,
            Header("session-healthy", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 10, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));

        // 追加一个 magic 错误的"帧"（结构损坏 ≠ torn tail）：蓝本按"读取失败弃全文件"处理，
        // 即使前面帧的内容已可解压也不落账。
        await using (var stream = File.Open(path, FileMode.Append, FileAccess.Write))
        {
            stream.Write([0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03]);
        }

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle(); // 健康会话的记录
        result.Records[0].SessionId.Should().Be("session-healthy");
        result.Diagnostics.MalformedLines.Should().Be(1);
        result.Diagnostics.FilesRead.Should().Be(1); // 仅健康文件计入 FilesRead
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Corrupt_frame_content_fails_closed_like_blueprint()
    {
        var home = CreateTempHome();
        // 帧结构合法（头/块/校验和布局完好）但内容损坏：带校验和帧内载荷位翻转，
        // ZstdSharp 解压抛 ZstdException（"Restored data doesn't match checksum"），
        // 流层须转译为 InvalidDataException（独立审查 P1-1：否则会冲出 ScanAsync 打断整次刷新），
        // 蓝本 verify-raw 对任何解压错误都是按文件 preflight failure。
        var path = WriteSessionLog(home, "--tmp-proj--", "session-corrupt-content", zstd: true, version: null,
            checksum: true,
            frames: new[]
            {
                new[]
                {
                    Header("session-corrupt-content", "/tmp/proj"),
                    Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 30, output: 3, cacheRead: 0, cacheWrite: 0,
                        provider: "ark", model: "glm-5.3-flash"),
                },
            });
        WriteSessionLog(home, "--tmp-proj--", "session-healthy", zstd: false, version: null,
            Header("session-healthy", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 10, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));

        // 翻转载荷末字节（紧邻帧尾 4 字节校验和之前）：帧结构不变、校验和必然不符。
        var bytes = File.ReadAllBytes(path);
        bytes[^5] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var result = await CreateAdapter(home).ScanAsync();

        // 异常不逃逸出 ScanAsync；损坏文件按蓝本 fail-closed（不落账），健康会话照常产出。
        result.Records.Should().ContainSingle();
        result.Records[0].SessionId.Should().Be("session-healthy");
        result.Diagnostics.MalformedLines.Should().Be(1);
        result.Diagnostics.FilesRead.Should().Be(1);
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Malformed_lines_are_counted_without_aborting_file()
    {
        var home = CreateTempHome();
        WriteSessionLog(home, "--tmp-proj--", "session-bad", zstd: false, version: null,
            Header("session-bad", "/tmp/proj"),
            "{not-json",
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 3, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"),
            "[1,2,3]");

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Diagnostics.MalformedLines.Should().Be(1);
        result.Diagnostics.LinesSkipped.Should().Be(2); // session 头行 + 非对象行
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Incremental_rescan_picks_up_appended_frame_once()
    {
        var home = CreateTempHome();
        var path = WriteSessionLog(home, "--tmp-proj--", "session-incr", zstd: true, version: null,
            frames: new[]
            {
                new[]
                {
                    Header("session-incr", "/tmp/proj"),
                    Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 10, output: 1, cacheRead: 0, cacheWrite: 0,
                        provider: "ark", model: "glm-5.3-flash"),
                },
            });
        var adapter = CreateAdapter(home);

        var first = await adapter.ScanAsync();
        first.Records.Should().ContainSingle();
        first.Diagnostics.FilesRead.Should().Be(1);

        // 追加一个新帧（DSH 的追加写形态）。
        AppendCompressedFrame(path, Message(WeekdayEraTime + 5, 2, turn: 2, step: 1, input: 20, output: 2,
            cacheRead: 0, cacheWrite: 0, provider: "ark", model: "glm-5.3-flash"));

        var second = await adapter.ScanAsync();
        second.Records.Should().HaveCount(2);
        second.Diagnostics.FilesRead.Should().Be(1); // 只重读变更文件
        second.Diagnostics.FilesDiscovered.Should().Be(1);

        // 无变化的第三次扫描不重读、不产生重复。
        var third = await adapter.ScanAsync();
        third.Records.Should().HaveCount(2);
        third.Diagnostics.FilesRead.Should().Be(0);
    }

    [Fact]
    public async Task Line_spanning_frame_boundary_is_parsed_by_stream_reader()
    {
        var home = CreateTempHome();
        // 一行 JSON 跨帧（DSH 实际不产生，流式 reader 的兼容行为）：前帧给前半字节、后帧给后半。
        var fullLine = Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 11, output: 1, cacheRead: 0, cacheWrite: 0,
            provider: "ark", model: "glm-5.3-flash") + "\n";
        var bytes = Encoding.UTF8.GetBytes(fullLine);
        var split = bytes.Length / 2;
        var path = Path.Combine(home, "sessions", "--tmp-proj--", "session-split", "session.jsonl.zstd");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteCompressedFrames(path, [bytes[..split], bytes[split..]]);

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].InputTokens.Should().Be(11);
    }

    [Fact]
    public async Task Missing_home_yields_no_data()
    {
        var result = await CreateAdapter(Path.Combine(Path.GetTempPath(), $"ccusage-dsh-missing-{Guid.NewGuid():N}")).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Dsh_home_environment_variable_is_respected()
    {
        var envHome = CreateTempHome();
        WriteSessionLog(envHome, "--env-proj--", "session-env", zstd: false, version: null,
            Header("session-env", "/env/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 6, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));

        var adapter = new DshSessionAdapter(
            locator: new DshLogLocator(name => name == "DSH_HOME" ? envHome : null));

        var result = await adapter.ScanAsync();

        result.Records.Should().ContainSingle();
        result.Records[0].ProjectPath.Should().Be("/env/proj");
        result.Roots.Should().Contain(envHome);
    }

    [Fact]
    public async Task Project_path_falls_back_to_directory_decode_without_cwd()
    {
        var home = CreateTempHome();
        WriteSessionLog(home, "--Users-dev-dsh-dsh-plugin-dev-notes--", "session-nocwd", zstd: false, version: null,
            // 头行无 cwd（异常形态）：项目路径回退目录名解码（'-' 不猜测分隔符，~XXXX 精确还原）。
            "{\"type\":\"session\",\"version\":4,\"id\":\"session-nocwd\"}",
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 2, output: 2, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Single().ProjectPath.Should().Be("Users-dev-dsh-dsh-plugin-dev-notes");
    }

    [Fact]
    public async Task Deepseek_route_is_priced_by_history_catalog_and_others_fail_closed()
    {
        var home = CreateTempHome();
        // flat 规则时代（< 2026-08-16T16:00Z）：1M input × 0.14 + 2M cacheRead × 0.0028 + 1M output × 0.28。
        // weekday schedule 时代高峰（周一 10:00 上海）：1M input × 0.44。
        var flatEra = WriteSessionLog(home, "--tmp-proj--", "session-priced", zstd: false, version: null,
            Header("session-priced", "/tmp/proj"),
            Message(FlatEraTime, 1, turn: 1, step: 1, input: 1_000_000, output: 1_000_000, cacheRead: 2_000_000, cacheWrite: 0,
                provider: "deepseek", model: "deepseek-v4-flash"),
            Message(WeekdayEraTime, 2, turn: 2, step: 1, input: 1_000_000, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "deepseek-official", model: "deepseek-v4-flash"));

        var result = await CreateAdapter(home).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records[0].CostUsd.Should().Be(0.14m + 0.0056m + 0.28m);
        result.Records[1].CostUsd.Should().Be(0.44m);

        // 非 deepseek 路由：null 缺价（fail-closed 不本地计价；A-0b 起不折已知 0，
        // 采集侧侧表仍可定价，缺价进 UnpricedModels——快照 zai 段确有 glm-5.3-flash）。
        WriteSessionLog(home, "--tmp-proj--", "session-ark-priced", zstd: false, version: null,
            Header("session-ark-priced", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 1_000_000, output: 0, cacheRead: 0, cacheWrite: 0,
                provider: "ark", model: "glm-5.3-flash"));
        var arkResult = await CreateAdapter(home).ScanAsync();
        arkResult.Records.Single(record => record.SessionId == "session-ark-priced").CostUsd.Should().BeNull();
    }

    [Fact]
    public async Task CacheWrite_bucket_fails_closed_for_all_catalog_rules()
    {
        var home = CreateTempHome();
        WriteSessionLog(home, "--tmp-proj--", "session-cachewrite", zstd: false, version: null,
            Header("session-cachewrite", "/tmp/proj"),
            Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 100, output: 10, cacheRead: 0, cacheWrite: 50,
                provider: "deepseek", model: "deepseek-v4-pro"));

        var result = await CreateAdapter(home).ScanAsync();

        // 目录所有规则的 cacheWrite 无单价（蓝本 unitPrices 默认 null）→ 正桶缺价整体 fail-closed
        // → 缺价 null（不折已知 0，A-0b；侧表定价同样缺价时进 UnpricedModels）。
        var record = result.Records.Single();
        record.CacheCreationTokens.Should().Be(50);
        record.CostUsd.Should().BeNull();
    }

    [Fact]
    public async Task Snapshot_deepseek_section_is_not_parsed_as_flat_models()
    {
        // deepseek 段无 models 键：不得产生 flat 价格条目（否则 deepseek 记录会被泛化匹配按 flat 价错算）。
        // I-035 起断言走模型目录（生产路径）：目录里关联不上 deepseek 的 flat 条目，
        // 其定价由 DeepSeekPricingCatalog 的历史规则承担。
        var identity = new ModelCatalog().ResolveIdentity("deepseek-v4-flash");

        identity.IsLinked.Should().BeFalse();
        identity.Definition.Should().BeNull();
    }

    [Fact]
    public async Task Double_scan_is_idempotent()
    {
        var home = CreateTempHome();
        WriteSessionLog(home, "--tmp-proj--", "session-idem", zstd: true, version: null,
            frames: new[]
            {
                new[]
                {
                    Header("session-idem", "/tmp/proj"),
                    Message(WeekdayEraTime, 1, turn: 1, step: 1, input: 12, output: 3, cacheRead: 0, cacheWrite: 0,
                        provider: "ark", model: "glm-5.3-flash"),
                },
            });
        var adapter = CreateAdapter(home);

        var first = await adapter.ScanAsync();
        var second = await adapter.ScanAsync();

        second.Records.Should().HaveCount(first.Records.Count);
        second.Records.Sum(record => record.TotalTokens).Should().Be(first.Records.Sum(record => record.TotalTokens));
        second.Diagnostics.DuplicateRecords.Should().Be(0);
    }

    // ---- helpers ----

    private DshSessionAdapter CreateAdapter(string home)
        => new(configuredHome: home, fileParser: new DshSessionLogParser());

    private string CreateTempHome()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-dsh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(path, "sessions"));
        _tempDirs.Add(path);
        return path;
    }

    private static string Header(string id, string cwd)
        => $"{{\"type\":\"session\",\"version\":4,\"id\":\"{id}\",\"createdAt\":{WeekdayEraTime - 1000},\"cwd\":\"{cwd}\"}}";

    private static string RequestHeader(long time, int seq, string provider, string model)
        => $"{{\"type\":\"request/header\",\"seq\":{seq},\"time\":{time},\"data\":{{\"header\":{{\"config\":{{\"provider\":\"{provider}\",\"model\":\"{model}\"}}}}}}}}";

    private static string Message(long time, int seq, int turn, int step, long input, long output, long cacheRead, long cacheWrite, string? provider, string? model)
    {
        // source 在 message 内（真实 DSH 形态：data.message.source.{kind,provider,model}）。
        var source = provider is null || model is null
            ? string.Empty
            : $",\"source\":{{\"kind\":\"model\",\"provider\":\"{provider}\",\"model\":\"{model}\"}}";
        return $"{{\"type\":\"assistant/message\",\"seq\":{seq},\"time\":{time},\"data\":{{\"turn\":{turn},\"step\":{step},"
            + $"\"message\":{{\"role\":\"assistant\",\"content\":[]{source}}},"
            + $"\"usage\":{{\"inputTokens\":{input},\"outputTokens\":{output},\"cacheReadTokens\":{cacheRead},\"cacheWriteTokens\":{cacheWrite}}}}}}}";
    }

    private static string Chunk(long time, int seq, int turn, int step, long input, long output, long cacheRead, long cacheWrite)
        => $"{{\"type\":\"assistant/chunk\",\"seq\":{seq},\"time\":{time},\"data\":{{\"turn\":{turn},\"step\":{step},"
            + $"\"chunk\":{{\"type\":\"usage\",\"usage\":{{\"inputTokens\":{input},\"outputTokens\":{output},"
            + $"\"cacheReadTokens\":{cacheRead},\"cacheWriteTokens\":{cacheWrite}}}}}}}}}";

    private string WriteSessionLog(
        string home,
        string projectDirName,
        string sessionDirName,
        bool zstd,
        int? version,
        params string[] lines)
        => WriteSessionLog(home, projectDirName, sessionDirName, zstd, version, frames: [lines]);

    private string WriteSessionLog(
        string home,
        string projectDirName,
        string sessionDirName,
        bool zstd,
        int? version,
        string[][] frames,
        bool checksum = false)
    {
        var sessionDir = Path.Combine(home, "sessions", projectDirName, sessionDirName);
        Directory.CreateDirectory(sessionDir);
        var fileName = version is null or 0 ? "session.jsonl" : $"session.v{version}.jsonl";
        var path = Path.Combine(sessionDir, zstd ? fileName + ".zstd" : fileName);
        if (zstd)
        {
            WriteCompressedFrames(path, frames.Select(frame => Encoding.UTF8.GetBytes(string.Join("\n", frame) + "\n")).ToArray(), checksum);
        }
        else
        {
            File.WriteAllText(path, string.Join("\n", frames.SelectMany(frame => frame)) + "\n");
        }

        return path;
    }

    private static void WriteCompressedFrames(string path, byte[][] frames, bool checksum = false)
    {
        using var stream = File.Create(path);
        WriteCompressedFramesInto(stream, frames, checksum);
    }

    private static void AppendCompressedFrame(string path, string line)
        => WriteCompressedFramesInto(File.Open(path, FileMode.Append, FileAccess.Write), [Encoding.UTF8.GetBytes(line)]);

    private static void WriteCompressedFramesInto(FileStream stream, byte[][] frames, bool checksum = false)
    {
        using var _ = stream;
        var compressor = new Compressor();
        if (checksum)
        {
            // 帧校验和（ZSTD_c_checksumFlag）：内容损坏可被确定性检出（Restored data doesn't match checksum）。
            compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        }

        foreach (var frame in frames)
        {
            stream.Write(compressor.Wrap(frame));
        }
    }


    /// <summary>建目录 reparse point：Windows 用 junction（mklink /J，普通权限即可）；Unix 用
    /// 符号链接（同带 ReparsePoint 语义）。两平台定位器口径一致：一律不跟随。</summary>
    private static void CreateReparseDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "junction 建立失败则用例前提不成立");
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时目录清理失败不影响测试结论；B-011 用例的 junction 会被递归删除探入而拒绝访问。
            }
        }
    }
}
