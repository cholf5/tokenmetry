namespace Tokenmetry.Infrastructure.Tests.Cindy;

using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Cindy;

/// <summary>
/// Cindy（Pi runtime）适配器测试：临时目录 fixtures，全部合成数据。锁定三件事——
/// ① 出账身份 = Agent "Cindy"（不带 store 前缀、不区分 runtime）；
/// ② 项目维度取会话头 cwd（Cindy 的 sessions 目录是扁平的，蓝本 extract_project 会退化成文件名）；
/// ③ 复用 pi 蓝本的重复抑制（subagent-artifacts 剔除、父子重放前缀抑制）与零变更轮记忆。
/// </summary>
public class CindyPiAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task Scans_pi_sessions_as_cindy_agent_with_cwd_project()
    {
        var userData = CreateUserDataRoot();
        WriteSession(userData, "2026-01-02T10-00-00-000Z_session-a.jsonl",
            SessionLine("session-a", "2026-01-02T10:00:00.000Z", parent: null, cwd: "E:/dev/demo"),
            UsageLine("2026-01-02T10:00:05.000Z", 100, 10, 20, 3));

        var result = await new CindyPiAdapter(configuredUserDataRoot: userData).ScanAsync();

        result.Records.Should().ContainSingle();
        var record = result.Records[0];
        record.Agent.Should().Be("Cindy");
        record.Model.Should().Be("gpt-5", "store 前缀不进模型维度（Cindy 是 agent 身份，不是模型命名空间）");
        record.ProjectPath.Should().Be("E:/dev/demo", "项目维度取会话头 cwd");
        record.SessionId.Should().Be("session-a", "文件名首个 '_' 之后的部分（pi 蓝本口径）");
        record.TotalTokens.Should().Be(133);
        result.Roots.Should().ContainSingle().Which.Should()
            .Be(Path.Combine(userData, "pi-agent-home", "sessions"));
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.Ok);
    }

    [Fact]
    public async Task Missing_cwd_header_falls_back_to_unknown_project()
    {
        var userData = CreateUserDataRoot();
        WriteSession(userData, "2026-01-02T10-00-00-000Z_session-a.jsonl",
            SessionLine("session-a", "2026-01-02T10:00:00.000Z", parent: null, cwd: null),
            UsageLine("2026-01-02T10:00:05.000Z", 10, 1, 2, 0));

        var result = await new CindyPiAdapter(configuredUserDataRoot: userData).ScanAsync();

        result.Records.Should().ContainSingle().Which.ProjectPath.Should().Be("Unknown Project",
            "与 Claude / Codex 适配器同口径，不用文件名冒充项目");
    }

    [Fact]
    public async Task Excludes_subagent_artifact_transcripts()
    {
        var userData = CreateUserDataRoot();
        var sessions = Path.Combine(userData, "pi-agent-home", "sessions");
        WriteSession(userData, "2026-01-02T10-00-00-000Z_session-a.jsonl",
            SessionLine("session-a", "2026-01-02T10:00:00.000Z", parent: null, cwd: "E:/dev/demo"),
            UsageLine("2026-01-02T10:00:05.000Z", 10, 1, 2, 0));
        // 派生转录：run-*/ 下的子会话 artifacts
        var artifactDir = Path.Combine(sessions, "subagent-artifacts", "run-1");
        Directory.CreateDirectory(artifactDir);
        File.WriteAllText(Path.Combine(artifactDir, "2026-01-02T10-00-01-000Z_session-sub.jsonl"),
            string.Join("\n",
                SessionLine("session-sub", "2026-01-02T10:00:01.000Z", parent: null, cwd: "E:/dev/demo"),
                UsageLine("2026-01-02T10:00:06.000Z", 10, 1, 2, 0)) + "\n");

        var result = await new CindyPiAdapter(configuredUserDataRoot: userData).ScanAsync();

        result.Records.Should().ContainSingle("subagent-artifacts 是派生转录，由父会话覆盖");
        result.Records[0].SessionId.Should().Be("session-a");
        result.Diagnostics.FilesDiscovered.Should().Be(1, "剔除的文件不计数");
    }

    [Fact]
    public async Task Skips_replayed_parent_prefix_but_keeps_child_usage()
    {
        var userData = CreateUserDataRoot();
        var sessions = Path.Combine(userData, "pi-agent-home", "sessions");
        WriteSession(userData, "2026-01-01T00-00-00-000Z_agent_root.jsonl",
            SessionLine("agent_root", "2026-01-01T00:00:00.000Z", parent: null, cwd: "E:/dev/demo"),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            UsageLine("2026-01-03T00:00:00.000Z", 200, 20, 30, 4));
        WriteSession(userData, "2026-01-03T00-00-00-000Z_agent_child.jsonl",
            SessionLine("agent_child", "2026-01-03T00:00:00.000Z",
                Path.Combine(sessions, "2026-01-01T00-00-00-000Z_agent_root.jsonl"), cwd: "E:/dev/demo"),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            UsageLine("2026-01-03T00:00:00.000Z", 200, 20, 30, 4),
            UsageLine("2026-01-03T01:00:00.000Z", 70, 7, 8, 2));

        var result = await new CindyPiAdapter(configuredUserDataRoot: userData).ScanAsync();

        result.Records.Should().HaveCount(3, "父 2 条 + 子仅 fork 之后 1 条（前缀 2 条被抑制）");
        result.Records.Where(record => record.SessionId == "agent_child").Should().ContainSingle()
            .Which.InputTokens.Should().Be(70, "前缀两条与父文件一致 → 抑制，只留 fork 之后的");
    }

    [Fact]
    public async Task Missing_sessions_directory_yields_no_data()
    {
        // 只建 userData 根、不建 pi-agent-home：Cindy 没装过 / 没用过 Pi runtime。
        var userData = CreateTempDir();

        var result = await new CindyPiAdapter(configuredUserDataRoot: userData).ScanAsync();

        result.Records.Should().BeEmpty();
        result.Roots.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public async Task Unchanged_second_scan_reuses_records_without_rereading()
    {
        var userData = CreateUserDataRoot();
        WriteSession(userData, "2026-01-02T10-00-00-000Z_session-a.jsonl",
            SessionLine("session-a", "2026-01-02T10:00:00.000Z", parent: null, cwd: "E:/dev/demo"),
            UsageLine("2026-01-02T10:00:05.000Z", 100, 10, 20, 3));
        var adapter = new CindyPiAdapter(configuredUserDataRoot: userData);

        var first = await adapter.ScanAsync();
        first.Diagnostics.FilesRead.Should().Be(1);
        first.Diagnostics.FilesChanged.Should().Be(1);

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesChanged.Should().Be(0, "文件戳未动 → 管道可按无变化轮短路");
        second.Diagnostics.FilesRead.Should().Be(0, "零变更轮零读取零解析");
        second.Records.Should().ContainSingle();
        second.Records[0].Agent.Should().Be("Cindy");
    }

    [Fact]
    public async Task Zero_stored_cost_is_treated_as_unknown_so_pricing_engine_can_apply()
    {
        // Cindy 网关回的 cost.total 恒为 0（= 没报成本）：留着 0 会让主管道直接采用，
        // 价目覆盖 / 模型别名永远不生效。0 成本 + 非零 token → 未知，交定价引擎。
        var userData = CreateUserDataRoot();
        WriteSession(userData, "2026-01-02T10-00-00-000Z_session-a.jsonl",
            SessionLine("session-a", "2026-01-02T10:00:00.000Z", parent: null, cwd: "E:/dev/demo"),
            UsageLineWithCost("2026-01-02T10:00:05.000Z", 100, 10, "0"));

        var result = await new CindyPiAdapter(configuredUserDataRoot: userData).ScanAsync();

        result.Records.Should().ContainSingle().Which.CostUsd.Should().BeNull("0 不是免费，是未报");
        result.Diagnostics.MissingCostRecords.Should().Be(1);
    }

    [Fact]
    public async Task Non_zero_stored_cost_is_honored()
    {
        var userData = CreateUserDataRoot();
        WriteSession(userData, "2026-01-02T10-00-00-000Z_session-a.jsonl",
            SessionLine("session-a", "2026-01-02T10:00:00.000Z", parent: null, cwd: "E:/dev/demo"),
            UsageLineWithCost("2026-01-02T10:00:05.000Z", 100, 10, "0.25"));

        var result = await new CindyPiAdapter(configuredUserDataRoot: userData).ScanAsync();

        result.Records.Should().ContainSingle().Which.CostUsd.Should().Be(0.25m, "网关真的报了成本就照用");
    }

    [Fact]
    public void UserDataRoot_resolution_prefers_configured_root()
    {
        var locator = new CindyLogLocator(name => null);

        locator.ResolveUserDataRoot(@"E:\cindy-userdata").Should().Be(@"E:\cindy-userdata");
        locator.ResolveUserDataRoot("   ").Should().BeNull("空配置回落平台默认，桩环境里解析不出 home");
    }

    // ---- helpers ----

    private string CreateUserDataRoot()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "pi-agent-home", "sessions"));
        return root;
    }

    private static void WriteSession(string userDataRoot, string fileName, params string[] lines)
    {
        var path = Path.Combine(userDataRoot, "pi-agent-home", "sessions", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    private static string SessionLine(string id, string timestamp, string? parent, string? cwd)
    {
        var line = $"{{\"type\":\"session\",\"id\":\"{id}\",\"timestamp\":\"{timestamp}\"";
        if (parent is not null)
        {
            line += $",\"parentSession\":\"{parent.Replace("\\", "\\\\")}\"";
        }

        if (cwd is not null)
        {
            line += $",\"cwd\":\"{cwd.Replace("\\", "\\\\")}\"";
        }

        return line + "}";
    }

    private static string UsageLine(string timestamp, long input, long output, long cacheRead, long cacheWrite)
        => $"{{\"type\":\"message\",\"timestamp\":\"{timestamp}\",\"message\":{{\"role\":\"assistant\",\"model\":\"gpt-5\",\"usage\":{{\"input\":{input},\"output\":{output},\"cacheRead\":{cacheRead},\"cacheWrite\":{cacheWrite},\"totalTokens\":{input + output + cacheRead + cacheWrite}}}}}}}";

    private static string UsageLineWithCost(string timestamp, long input, long output, string totalCost)
        => $"{{\"type\":\"message\",\"timestamp\":\"{timestamp}\",\"message\":{{\"role\":\"assistant\",\"model\":\"gpt-5\",\"usage\":{{\"input\":{input},\"output\":{output},\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":{input + output},\"cost\":{{\"total\":{totalCost}}}}}}}}}";

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tokenmetry-cindy-{Guid.NewGuid():N}");
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
                // 临时目录清理失败不影响断言
            }
        }
    }
}
