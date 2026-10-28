namespace Tokenmetry.Infrastructure.Tests.PiAgent;

using System.Text.Json;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.PiAgent;

/// <summary>
/// I-019: pi-agent 适配器测试（临时目录 fixtures，全部合成数据；语义对照
/// ../ccusage rust/adapters/pi 的 loader.rs / parser.rs / paths.rs，含蓝本内联测试的同名场景：
/// 重放抑制、fail-open、树形活动分支、多路径命名存储、subagent-artifacts 剔除、签名全字段比较）。
/// </summary>
public class PiAgentAdapterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- 蓝本 loader.rs 内联测试同名场景（重放抑制） ----

    [Fact]
    public async Task Skips_replayed_parent_prefix_but_keeps_child_usage()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            UsageLine("2026-01-03T00:00:00.000Z", 200, 20, 30, 4),
            UsageLine("2026-01-04T00:00:00.000Z", 50, 5, 6, 1));
        WriteLines(root, "project-a/agent_child-a.jsonl",
            SessionLine("child-a", "2026-01-03T00:00:00.000Z", ParentPath(root, "project-a/agent_root.jsonl")),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            UsageLine("2026-01-03T00:00:00.000Z", 200, 20, 30, 4),
            UsageLine("2026-01-04T00:00:00.000Z", 50, 5, 6, 1));
        WriteLines(root, "project-a/agent_child-b.jsonl",
            SessionLine("child-b", "2026-01-03T00:00:00.000Z", ParentPath(root, "project-a/agent_root.jsonl")),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            UsageLine("2026-01-03T00:00:00.000Z", 200, 20, 30, 999),
            UsageLine("2026-01-03T01:00:00.000Z", 70, 7, 8, 2));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(6, "父 3 + child-a 1（前缀 2 抑制）+ child-b 2（签名首异即止）");
        var childA = result.Records.Where(record => record.SessionId == "child-a").ToList();
        childA.Should().ContainSingle().Which.InputTokens.Should().Be(50);
        var childB = result.Records.Where(record => record.SessionId == "child-b").ToList();
        childB.Should().HaveCount(2);
        childB[0].CacheCreationTokens.Should().Be(999, "cacheWrite 变异阻断前缀匹配");
    }

    [Fact]
    public async Task Skips_the_copied_active_parent_branch_after_an_abandoned_sibling()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            LinkedUsageLine("a", null, "2026-01-02T10:00:00.000Z", 100),
            LinkedUsageLine("x", "a", "2026-01-02T11:00:00.000Z", 200),
            LinkedUsageLine("y", "a", "2026-01-02T12:00:00.000Z", 300),
            LinkedUsageLine("z", "y", "2026-01-02T13:00:00.000Z", 400));
        WriteLines(root, "project-a/agent_child.jsonl",
            SessionLine("child", "2026-01-03T00:00:00.000Z", ParentPath(root, "project-a/agent_root.jsonl")),
            LinkedUsageLine("copy-a", null, "2026-01-02T10:00:00.000Z", 100),
            LinkedUsageLine("copy-y", "copy-a", "2026-01-02T12:00:00.000Z", 300),
            LinkedUsageLine("copy-z", "copy-y", "2026-01-02T13:00:00.000Z", 400),
            LinkedUsageLine("child-only", "copy-z", "2026-01-03T01:00:00.000Z", 500));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(5, "父活动分支 a→y→z 3 条（弃支 x 保留）+ child-only 1 条");
        result.Records.Should().Contain(record =>
            record.SessionId == "root" && record.InputTokens == 200, "弃支 x 保留在父中");
        var child = result.Records.Where(record => record.SessionId == "child").ToList();
        child.Should().ContainSingle().Which.InputTokens.Should().Be(500);
    }

    [Fact]
    public async Task Skips_copied_branch_when_parent_has_multiple_disconnected_roots()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            LinkedUsageLine("unrelated-root", null, "2026-01-02T09:00:00.000Z", 50),
            LinkedUsageLine("active-root", null, "2026-01-02T10:00:00.000Z", 100),
            LinkedUsageLine("active-leaf", "active-root", "2026-01-02T11:00:00.000Z", 200));
        WriteLines(root, "project-a/agent_child.jsonl",
            SessionLine("child", "2026-01-03T00:00:00.000Z", ParentPath(root, "project-a/agent_root.jsonl")),
            LinkedUsageLine("copy-root", null, "2026-01-02T10:00:00.000Z", 100),
            LinkedUsageLine("copy-leaf", "copy-root", "2026-01-02T11:00:00.000Z", 200),
            LinkedUsageLine("child-only", "copy-leaf", "2026-01-03T01:00:00.000Z", 300));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(4, "叶回溯取 active-root→active-leaf 分支，非活动根不计入候选");
        result.Records.Should().Contain(record =>
            record.SessionId == "root" && record.InputTokens == 50);
        var child = result.Records.Where(record => record.SessionId == "child").ToList();
        child.Should().ContainSingle().Which.InputTokens.Should().Be(300);
    }

    [Fact]
    public async Task Fails_open_for_missing_malformed_and_unrelated_same_token_sessions()
    {
        var root = CreateSessionsRoot();
        var missingParent = Path.Combine(root, "project-a", "agent_missing.jsonl");
        var malformedParent = Path.Combine(root, "project-a", "agent_malformed-parent.jsonl");
        var outsideParent = WriteLines(CreateTempDir(), "agent_outside.jsonl",
            SessionLine("outside", "2026-01-01T00:00:00.000Z", parent: null),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_missing-child.jsonl",
            SessionLine("missing-child", "2026-01-03T00:00:00.000Z", missingParent),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_outside-parent-child.jsonl",
            SessionLine("outside-parent-child", "2026-01-03T00:00:00.000Z", outsideParent),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_malformed-parent.jsonl",
            "not json",
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_malformed-parent-child.jsonl",
            SessionLine("malformed-parent-child", "2026-01-03T00:00:00.000Z", malformedParent),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_malformed-parent-grandchild.jsonl",
            SessionLine("malformed-parent-grandchild", "2026-01-04T00:00:00.000Z",
                Path.Combine(root, "project-a", "agent_malformed-parent-child.jsonl")),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_malformed-child.jsonl",
            "not json",
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_regular-a.jsonl",
            UsageLine("2026-01-02T11:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_regular-b.jsonl",
            UsageLine("2026-01-02T11:00:00.000Z", 100, 10, 20, 3));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(8, "lineage 无效一律 fail-open：缺失父/畸形父/圈外父/自环全保留");
        foreach (var sessionId in new[] { "missing-child", "outside-parent-child", "regular-a", "regular-b", "malformed-parent-grandchild" })
        {
            result.Records.Count(record => record.SessionId == sessionId).Should().Be(1, sessionId);
        }
    }

    [Fact]
    public async Task Fails_open_for_self_referential_and_cyclic_sessions()
    {
        var root = CreateSessionsRoot();
        var selfPath = Path.Combine(root, "project-a", "agent_self.jsonl");
        var cycleA = Path.Combine(root, "project-a", "agent_cycle-a.jsonl");
        var cycleB = Path.Combine(root, "project-a", "agent_cycle-b.jsonl");
        WriteLines(root, "project-a/agent_self.jsonl",
            SessionLine("self", "2026-01-03T00:00:00.000Z", selfPath),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_cycle-a.jsonl",
            SessionLine("cycle-a", "2026-01-03T00:00:00.000Z", cycleB),
            UsageLine("2026-01-02T10:00:00.000Z", 200, 20, 30, 4));
        WriteLines(root, "project-a/agent_cycle-b.jsonl",
            SessionLine("cycle-b", "2026-01-03T00:00:00.000Z", cycleA),
            UsageLine("2026-01-02T10:00:00.000Z", 200, 20, 30, 4));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(3, "自引用与环：fail-open 全保留");
    }

    [Fact]
    public async Task Uses_raw_parent_stream_for_nested_forks()
    {
        var root = CreateSessionsRoot();
        var childPath = WriteLines(root, "project-a/agent_child.jsonl",
            SessionLine("child", "2026-01-03T00:00:00.000Z", ParentPath(root, "project-a/agent_root.jsonl")),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            UsageLine("2026-01-02T11:00:00.000Z", 200, 20, 30, 4),
            UsageLine("2026-01-03T01:00:00.000Z", 300, 30, 40, 5));
        WriteLines(root, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            UsageLine("2026-01-02T11:00:00.000Z", 200, 20, 30, 4));
        WriteLines(root, "project-a/agent_grandchild.jsonl",
            SessionLine("grandchild", "2026-01-04T00:00:00.000Z", childPath),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            UsageLine("2026-01-02T11:00:00.000Z", 200, 20, 30, 4),
            UsageLine("2026-01-03T01:00:00.000Z", 300, 30, 40, 5),
            UsageLine("2026-01-04T01:00:00.000Z", 400, 40, 50, 6));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(4);
        result.Records.Count(record => record.SessionId == "child").Should().Be(1);
        result.Records.Should().Contain(record =>
            record.SessionId == "grandchild" && record.InputTokens == 400, "孙代对父（child）原始流匹配前缀");
    }

    [Fact]
    public async Task Compares_every_effective_usage_field_before_suppressing_replay()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            UsageLineWithModelAndTotal("2026-01-02T10:00:00.000Z", "gpt-5", 10, 20, 30, 40, 100));
        var variants = new (string Name, string Model, long Input, long Output, long CacheRead, long CacheWrite, long Total)[]
        {
            ("input", "gpt-5", 11, 20, 30, 40, 101),
            ("output", "gpt-5", 10, 21, 30, 40, 101),
            ("cache-read", "gpt-5", 10, 20, 31, 40, 101),
            ("cache-write", "gpt-5", 10, 20, 30, 41, 101),
            ("model", "gpt-5-mini", 10, 20, 30, 40, 100),
            ("total-fallback", "gpt-5", 10, 20, 30, 40, 110),
        };
        foreach (var variant in variants)
        {
            WriteLines(root, $"project-a/child-{variant.Name}.jsonl",
                SessionLine(variant.Name, "2026-01-03T00:00:00.000Z", ParentPath(root, "project-a/agent_root.jsonl")),
                UsageLineWithModelAndTotal("2026-01-02T10:00:00.000Z", variant.Model, variant.Input, variant.Output, variant.CacheRead, variant.CacheWrite, variant.Total),
                UsageLine("2026-01-03T01:00:00.000Z", 1, 2, 3, 4));
        }

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(1 + variants.Length * 2, "任一有效字段（含 effective total）变异阻断抑制");
        foreach (var variant in variants)
        {
            result.Records.Count(record => record.SessionId == $"child-{variant.Name}").Should().Be(2, variant.Name);
        }
    }

    [Fact]
    public async Task Keeps_child_usage_when_display_cost_differs()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            UsageLineWithDisplayCost("2026-01-02T10:00:00.000Z", "1.0"));
        WriteLines(root, "project-a/agent_child.jsonl",
            SessionLine("child", "2026-01-03T00:00:00.000Z", ParentPath(root, "project-a/agent_root.jsonl")),
            UsageLineWithDisplayCost("2026-01-02T10:00:00.000Z", "2.0"));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "有效成本进重放签名：成本不同不抑制");
        result.Records.Should().Contain(record => record.SessionId == "child" && record.CostUsd == 2.0m);
    }

    [Fact]
    public async Task Treats_signed_zero_display_costs_as_equal_replay_identity()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            UsageLineWithDisplayCost("2026-01-02T10:00:00.000Z", "0.0"));
        WriteLines(root, "project-a/agent_child.jsonl",
            SessionLine("child", "2026-01-03T00:00:00.000Z", ParentPath(root, "project-a/agent_root.jsonl")),
            UsageLineWithDisplayCost("2026-01-02T10:00:00.000Z", "-0.0"));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.SessionId.Should().Be("root",
            "±0.0 等价（decimal 归一），child 前缀被抑制");
    }

    [Fact]
    public async Task Treats_missing_and_zero_display_costs_as_equal_replay_identity()
    {
        // 蓝本用空 PricingMap：无成本记录现算 0.0 == 存储 0.0。桌面以未定价模型等价复现
        // （内置快照未收录 → 现算成本 0）。
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            UsageLineWithModelAndTotal("2026-01-02T10:00:00.000Z", "mystery-unpriced-model", 10, 20, 30, 40, 100));
        WriteLines(root, "project-a/agent_child.jsonl",
            SessionLine("child", "2026-01-03T00:00:00.000Z", ParentPath(root, "project-a/agent_root.jsonl")),
            """{"type":"message","timestamp":"2026-01-02T10:00:00.000Z","message":{"role":"assistant","model":"mystery-unpriced-model","usage":{"input":10,"output":20,"cacheRead":30,"cacheWrite":40,"totalTokens":100,"cost":{"total":0.0}}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.SessionId.Should().Be("root",
            "同裸模型：无成本记录现算 0 == 存储 0.0（未定价模型现算恒 0），child 前缀被抑制");
    }

    [Fact]
    public async Task Dedupes_replay_when_raw_total_underreports_billable_usage()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            UsageLineWithModelAndTotal("2026-01-02T10:00:00.000Z", "gpt-5", 10, 20, 30, 40, 100));
        WriteLines(root, "project-a/agent_child.jsonl",
            SessionLine("child", "2026-01-03T00:00:00.000Z", ParentPath(root, "project-a/agent_root.jsonl")),
            UsageLineWithModelAndTotal("2026-01-02T10:00:00.000Z", "gpt-5", 10, 20, 30, 40, 99),
            UsageLine("2026-01-03T01:00:00.000Z", 1, 2, 3, 4));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "total 99 ≤ 四桶和 100：effective total 恒 100，签名等价被抑制");
        var child = result.Records.Where(record => record.SessionId == "child").ToList();
        child.Should().ContainSingle().Which.InputTokens.Should().Be(1);
    }

    // ---- 命名存储 ----

    [Fact]
    public async Task Matches_parent_files_across_multiple_paths_of_one_named_store()
    {
        // 蓝本 matches_parent_files_across_multiple_paths_of_one_named_store：单一命名存储的多个路径
        // 跨根解析父文件（pi.stores[].path 支持逗号分隔清单）。
        var fakeHome = CreateTempDir();
        var firstStore = Path.Combine(fakeHome, "first");
        var secondStore = Path.Combine(fakeHome, "second");
        Directory.CreateDirectory(firstStore);
        Directory.CreateDirectory(secondStore);
        var parent = WriteLines(firstStore, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(secondStore, "project-a/agent_child.jsonl",
            SessionLine("child", "2026-01-03T00:00:00.000Z", parent),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3),
            UsageLine("2026-01-03T01:00:00.000Z", 50, 5, 6, 1));
        var configDir = Path.Combine(fakeHome, ".claude");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "ccusage.json"),
            "{\"pi\":{\"stores\":[{\"name\":\"omp\",\"path\":\"" + firstStore.Replace("\\", "\\\\") + "," + secondStore.Replace("\\", "\\\\") + "\"}]}}");

        var adapter = new PiAgentAdapter(
            locator: new PiLogLocator(name => name switch
            {
                "USERPROFILE" or "HOME" => fakeHome,
                _ => null,
            }));

        var result = await adapter.ScanAsync();

        result.Records.Should().HaveCount(2, "子会话重放前缀跨存储路径解析并抑制");
        result.Records.Count(record => record.SessionId == "child").Should().Be(1);
        result.Records.Should().Contain(record => record.Model == "[omp] gpt-5");
    }

    [Fact]
    public async Task Named_store_prefixes_models_and_scans_from_config_store()
    {
        // 命名存储端到端：pi.stores[] 经配置发现 → 独立 agent（store 名）出账、模型带 "[store] " 前缀、
        // 项目名取相对存储根首段。
        var fakeHome = CreateTempDir();
        var store = Path.Combine(fakeHome, "omp-store");
        Directory.CreateDirectory(store);
        WriteLines(store, "project-a/agent_root.jsonl",
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        var configDir = Path.Combine(fakeHome, ".claude");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "ccusage.json"),
            "{\"pi\":{\"stores\":[{\"name\":\"omp\",\"path\":\"" + store.Replace("\\", "\\\\") + "\"}]}}");

        var adapter = new PiAgentAdapter(
            locator: new PiLogLocator(name => name switch
            {
                "USERPROFILE" or "HOME" => fakeHome,
                _ => null,
            }));

        var result = await adapter.ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Agent.Should().Be("omp", "命名存储作为独立 agent 出账（蓝本 adapter-all 装配）");
        record.Model.Should().Be("[omp] gpt-5");
        record.ProjectPath.Should().Be("project-a", "命名存储项目名 = 相对存储根首段");
    }

    [Fact]
    public async Task Invalid_named_store_config_is_dropped_and_default_store_still_scans()
    {
        // 蓝本报配置错误终止命令；桌面降级为整组丢弃 pi.stores[]（偏差⑥），默认存储照常。
        var fakeHome = CreateTempDir();
        WriteSessionsUnderConfigHome(fakeHome, "project-a/agent_root.jsonl",
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        var configDir = Path.Combine(fakeHome, ".claude");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "ccusage.json"),
            """{"pi":{"stores":[{"name":"BadName","path":"/tmp/whatever"}]}}""");

        var adapter = new PiAgentAdapter(
            locator: new PiLogLocator(name => name switch
            {
                "USERPROFILE" or "HOME" => fakeHome,
                _ => null,
            }));

        var result = await adapter.ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Agent.Should().Be("pi");
        record.Model.Should().Be("[pi] gpt-5");
    }

    // ---- subagent-artifacts ----

    [Fact]
    public async Task Skips_subagent_artifact_transcripts_but_keeps_fresh_context_child_sessions()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_root.jsonl",
            SessionLine("root", "2026-01-01T00:00:00.000Z", parent: null),
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_root/subagent-artifacts/run1_agent_0_transcript.jsonl",
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/subagent-artifacts/other_transcript.jsonl",
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/agent_root/run-abc/run-0/agent_session.jsonl",
            UsageLine("2026-01-03T01:00:00.000Z", 50, 5, 6, 1));

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2, "subagent-artifacts 路径段剔除、run-* 子会话保留");
        result.Records.Should().Contain(record => record.InputTokens == 100);
        result.Records.Should().Contain(record => record.InputTokens == 50);
        result.Records.Should().NotContain(record => record.SessionId.Contains("transcript"));
    }

    [Fact]
    public void Detects_subagent_artifact_path_segments()
    {
        PiAgentAdapter.IsSubagentArtifactTranscript(
            "sessions/project-a/root/subagent-artifacts/run1_agent_0_transcript.jsonl").Should().BeTrue();
        PiAgentAdapter.IsSubagentArtifactTranscript(
            "sessions/project-a/root/run-abc/run-0/session.jsonl").Should().BeFalse();
        PiAgentAdapter.IsSubagentArtifactTranscript("sessions/project-a/root.jsonl").Should().BeFalse();
    }

    // ---- 蓝本 parser.rs 内联测试同名场景 ----

    [Fact]
    public async Task Falls_back_to_total_tokens_when_pi_parts_are_missing()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_session-a.jsonl",
            """{"type":"message","timestamp":"2026-01-02T00:00:00.000Z","message":{"role":"assistant","model":"gpt-5","usage":{"totalTokens":333}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.OutputTokens.Should().Be(333, "total 缺口补 output");
        record.InputTokens.Should().Be(0);
    }

    [Fact]
    public async Task Keeps_record_when_cost_is_not_an_object()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_session-a.jsonl",
            """{"type":"message","timestamp":"2026-01-02T00:00:00.000Z","message":{"role":"assistant","model":"gpt-5","usage":{"input":100,"output":200,"cost":0}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        var record = result.Records.Should().ContainSingle().Subject;
        record.InputTokens.Should().Be(100);
        record.OutputTokens.Should().Be(200);
        record.CostUsd.Should().BeNull("cost 非对象 → 视为缺失（蓝本 lenient_object），交引擎计价");
    }

    [Fact]
    public async Task Negative_display_cost_is_recalculated_and_zero_is_kept()
    {
        // 蓝本 auto_recalculates_negative_but_keeps_zero_source_cost：负成本 Auto 重算（引擎）、0 保留。
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_session-a.jsonl",
            """{"type":"message","timestamp":"2026-01-02T00:00:00.000Z","message":{"role":"assistant","model":"gpt-5","usage":{"input":1000,"output":2000,"cacheRead":0,"cacheWrite":0,"totalTokens":3000,"cost":{"total":-1.25}}}}""",
            """{"type":"message","timestamp":"2026-01-02T00:01:00.000Z","message":{"role":"assistant","model":"gpt-5","usage":{"input":1000,"output":2000,"cacheRead":0,"cacheWrite":0,"totalTokens":3000,"cost":{"total":0}}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().HaveCount(2);
        result.Records[0].CostUsd.Should().BeNull("负成本不采纳（CLI auto 口径），交引擎重算");
        result.Records[1].CostUsd.Should().Be(0m, "存储 0 保留（LogCost 短路）");
    }

    [Fact]
    public async Task Prefixed_display_model_is_priced_via_bare_candidate()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_session-a.jsonl",
            """{"type":"message","timestamp":"2026-01-19T00:00:00.000Z","message":{"role":"assistant","model":"gpt-5","usage":{"input":10000,"output":1000,"cacheRead":0,"cacheWrite":0,"totalTokens":11000}}}""");

        var result = await CreateAdapter(root).ScanAsync();
        var engine = new Tokenmetry.Core.Services.PricingEngine();

        var record = result.Records.Should().ContainSingle().Subject;
        record.Model.Should().Be("[pi] gpt-5");
        var priced = engine.PriceRecord(record);
        priced.Source.Should().Be(Tokenmetry.Core.Models.PricingSourceKind.Snapshot,
            "引擎候选剥离 \"[pi] \" 前缀命中 openai/gpt-5");
        priced.CostUsd.Should().Be(0.0225m, "10000×$1.25/M + 1000×$10/M");
    }

    [Fact]
    public async Task Structural_shape_failures_are_counted_as_malformed()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/agent_session-a.jsonl",
            "not json",
            """{"type":"message","timestamp":"2026-01-02T00:00:00.000Z","message":{"role":"assistant","usage":"scalar"}}""",
            """{"type":"message","timestamp":"2026-01-02T00:00:00.000Z","message":"not-object"}""",
            """["top-level-array"]""",
            """{"type":"message","timestamp":"2026-01-02T00:00:00.000Z","message":{"role":"assistant","model":"gpt-5","usage":{"input":5,"output":5}}}""",
            """{"type":"message","timestamp":"not-a-time","message":{"role":"assistant","model":"gpt-5","usage":{"input":6,"output":6}}}""",
            """{"type":"session","id":"s","timestamp":"2026-01-02T00:00:00.000Z"}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records.Should().ContainSingle().Which.InputTokens.Should().Be(5);
        result.Diagnostics.MalformedLines.Should().Be(1, "usage 标量行——仅预过滤通过的坏行进解析（坏 JSON 行无标记，被预过滤跳过）");
        result.Diagnostics.InvalidRecords.Should().Be(1, "时间戳不可解析（仓库口径）");
        result.Diagnostics.LinesSkipped.Should().Be(4, "坏 JSON、message 标量与顶层数组行不含 usage 标记、session 头行均被预过滤跳过");
        result.Diagnostics.Status.Should().Be(Tokenmetry.Core.Models.JsonlScanStatus.Partial);
    }

    [Fact]
    public async Task Session_id_and_project_are_derived_from_path()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/20260102_abc123.jsonl",
            """{"type":"message","timestamp":"2026-01-02T00:00:00.000Z","message":{"role":"assistant","model":"gpt-5","usage":{"input":1,"output":1}}}""");

        var result = await CreateAdapter(root).ScanAsync();

        result.Records[0].SessionId.Should().Be("abc123", "文件名首个 '_' 之后的部分");
        result.Records[0].ProjectPath.Should().Be("project-a", "路径中 sessions 段的后一段");

        // env 根目录名不含 "sessions" 段时项目名回退 unknown（蓝本 extract_project）。
        var plainRoot = CreateTempDir();
        WriteLines(plainRoot, "sub/agent_x.jsonl",
            """{"type":"message","timestamp":"2026-01-03T00:00:00.000Z","message":{"role":"assistant","model":"gpt-5","usage":{"input":2,"output":2}}}""");
        var plainResult = await CreateAdapter(plainRoot).ScanAsync();

        plainResult.Records.Should().ContainSingle().Which.ProjectPath.Should().Be("unknown");
    }

    // ---- 目录发现与环境变量（蓝本 paths.rs 语义） ----

    [Fact]
    public async Task Pi_agent_dir_environment_variable_is_respected()
    {
        var relocated = CreateSessionsRoot();
        WriteLines(relocated, "project-a/agent_root.jsonl",
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));

        try
        {
            Environment.SetEnvironmentVariable(PiLogLocator.PiAgentDirEnvVariable, relocated);
            var result = await new PiAgentAdapter().ScanAsync();

            result.Records.Should().ContainSingle();
            result.Records[0].ProjectPath.Should().Be("project-a");
            result.Roots.Should().ContainSingle().Which.Should().Be(relocated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PiLogLocator.PiAgentDirEnvVariable, null);
        }
    }

    [Fact]
    public async Task Set_but_empty_environment_variable_falls_back_to_default()
    {
        // 蓝本特例（与 Kimi/Amp/Codebuff 相反）：PI_AGENT_DIR 置空串 → trim 非空判定失败 → 回退默认
        // 目录 ~/.pi/agent/sessions（默认布局存在时照常出数）。
        var fakeHome = CreateTempDir();
        WriteSessionsUnderConfigHome(fakeHome, "project-a/agent_root.jsonl",
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));

        var locator = new PiLogLocator(name => name switch
        {
            PiLogLocator.PiAgentDirEnvVariable => "",
            "USERPROFILE" or "HOME" => fakeHome,
            _ => null,
        });
        var adapter = new PiAgentAdapter(locator: locator);

        var result = await adapter.ScanAsync();

        result.Records.Should().ContainSingle("空串 env 不生效，回退默认目录");
        result.Records[0].ProjectPath.Should().Be("project-a");
    }

    [Fact]
    public async Task Missing_default_directory_yields_no_data()
    {
        var fakeHome = CreateTempDir();
        var locator = new PiLogLocator(name => name is "USERPROFILE" or "HOME" ? fakeHome : null);
        var adapter = new PiAgentAdapter(locator: locator);

        var result = await adapter.ScanAsync();

        result.Records.Should().BeEmpty();
        result.Diagnostics.Status.Should().Be(JsonlScanStatus.NoData);
    }

    [Fact]
    public void Named_store_paths_expand_home_and_dedupe_existing_directories()
    {
        // 蓝本 named_store_path_expands_home_directory + named_store_path_matches_pi_path_list_semantics。
        var home = CreateTempDir();
        var store = Directory.CreateDirectory(Path.Combine(home, ".omp", "agent", "sessions")).FullName;
        var first = Directory.CreateDirectory(Path.Combine(home, "first", "sessions")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(home, "second", "sessions")).FullName;

        var locator = new PiLogLocator(name => name is "USERPROFILE" or "HOME" ? home : null);
        locator.ResolveNamedStoreDirectories("~/.omp/agent/sessions").Should().Equal([store]);
        // A-1：期望路径用 Path.Combine 构造（POSIX 斜杠字面量在 Windows 下不成立）。
        locator.ResolveNamedStoreDirectories($" {first}, {second}, {first}, {Path.Combine(home, "missing", "sessions")} ")
            .Should().Equal([first, second], "去空白、去重、不存在目录丢弃");
    }

    [Fact]
    public void Named_store_config_rejects_invalid_entries()
    {
        using var document = JsonDocument.Parse(
            """{"pi":{"stores":[{"name":"ok-store","path":"/a"},{"name":"BadName","path":"/b"}]}}""");

        PiLogLocator.ParseNamedStores(document.RootElement).Should().BeEmpty("非法名整组丢弃（偏差⑥）");
    }

    // ---- I-042 延伸：文件戳探针 + 零变更轮结果记忆化（与 IncrementalJsonlScanner 同构） ----

    [Fact]
    public async Task ChangeProbe_UnchangedSecondScan_SkipsRereadAndReusesCachedRecords()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/session-a.jsonl",
            SessionLine("session-a", "2026-01-02T10:00:00.000Z", null),
            UsageLine("2026-01-02T10:00:05.000Z", 100, 10, 20, 3));
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
    public async Task ChangeProbe_AppendedSessionContent_DetectedAsChange()
    {
        var root = CreateSessionsRoot();
        var file = WriteLines(root, "project-a/session-a.jsonl",
            SessionLine("session-a", "2026-01-02T10:00:00.000Z", null),
            UsageLine("2026-01-02T10:00:05.000Z", 100, 10, 20, 3));
        var adapter = CreateAdapter(root);
        (await adapter.ScanAsync()).Records.Should().ContainSingle();

        File.WriteAllText(file, string.Join("\n",
            SessionLine("session-a", "2026-01-02T10:00:00.000Z", null),
            UsageLine("2026-01-02T10:00:05.000Z", 100, 10, 20, 3),
            UsageLine("2026-01-02T10:01:05.000Z", 10, 1, 2, 0)) + "\n");

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesChanged.Should().Be(1, "文件内容变化（大小 + mtime）");
        second.Records.Should().HaveCount(2);
    }

    [Fact]
    public async Task ChangeProbe_RemovedAndAddedSessions_TriggerFullRescan()
    {
        var root = CreateSessionsRoot();
        WriteLines(root, "project-a/session-a.jsonl",
            SessionLine("session-a", "2026-01-02T10:00:00.000Z", null),
            UsageLine("2026-01-02T10:00:05.000Z", 100, 10, 20, 3));
        WriteLines(root, "project-a/session-b.jsonl",
            SessionLine("session-b", "2026-01-02T11:00:00.000Z", null),
            UsageLine("2026-01-02T11:00:05.000Z", 100, 10, 20, 3));
        var adapter = CreateAdapter(root);
        (await adapter.ScanAsync()).Records.Should().HaveCount(2);

        File.Delete(Path.Combine(root, "project-a", "session-b.jsonl"));
        WriteLines(root, "project-a/session-c.jsonl",
            SessionLine("session-c", "2026-01-02T12:00:00.000Z", null),
            UsageLine("2026-01-02T12:00:05.000Z", 100, 10, 20, 3));

        var second = await adapter.ScanAsync();

        second.Diagnostics.FilesDiscovered.Should().Be(2);
        second.Diagnostics.FilesChanged.Should().Be(1, "文件集变化 = 账面变化");
        second.Records.Select(record => record.SessionId).Should().BeEquivalentTo("session-a", "session-c");
    }

    // ---- helpers ----

    private PiAgentAdapter CreateAdapter(string sessionsDir) =>
        new(locator: new PiLogLocator(StubEnv(home: null, piAgentDir: sessionsDir)));

    /// <summary>home 与 PI_AGENT_DIR 的桩环境访问器（绕过默认目录发现）。</summary>
    private static Func<string, string?> StubEnv(string? home, string? piAgentDir)
    {
        return name => name switch
        {
            "USERPROFILE" or "HOME" => home,
            PiLogLocator.PiAgentDirEnvVariable => piAgentDir,
            _ => null,
        };
    }

    private string CreateSessionsRoot()
    {
        var path = Path.Combine(CreateTempDir(), "sessions");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>在 ~/.pi/agent/sessions 布局下写会话文件（默认目录分支测试用）。</summary>
    private static void WriteSessionsUnderConfigHome(string fakeHome, string relativePath, params string[] lines)
    {
        var sessions = Path.Combine(fakeHome, ".pi", "agent", "sessions");
        Directory.CreateDirectory(sessions);
        WriteLines(sessions, relativePath, lines);
    }

    private static string WriteLines(string root, string relativePath, params string[] lines)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Join("\n", lines) + "\n");
        return fullPath;
    }

    private static string? ParentPath(string sessionsRoot, string relative)
        => Path.Combine(sessionsRoot, relative);

    /// <summary>
    /// PI-3 接线（I-024）：identity cost 解析器与定价引擎同一套候选/别名口径——传入共享引擎
    /// （含用户覆盖 / 远程数据）时，重放签名与复合 entry id 的「有效成本」按共享引擎口径现算
    /// （蓝本 calculate_store_cost 的 display_cost 缺省分支）；缺省解析器仍为内置快照口径，
    /// 且在同一解析器实例内复用单个引擎。
    /// </summary>
    [Fact]
    public void Identity_cost_resolver_honors_shared_engine_overrides()
    {
        var sourceFile = WriteLines(CreateTempDir(), "project-a/agent_root.jsonl",
            UsageLine("2026-01-02T10:00:00.000Z", 100, 10, 20, 3));

        var overriddenEngine = new PricingEngine(overrides: new[]
        {
            new ModelPricingOverride { Model = "gpt-5", InputPerMillion = 100m, OutputPerMillion = 1000m },
        });
        var sharedParser = new PiSessionParser(record => overriddenEngine.PriceRecord(record).CostUsd ?? 0m);
        var snapshotParser = new PiSessionParser();

        var shared = sharedParser.ParseSessionFile(sourceFile, new PiStoreContext("pi", null)).Data!;
        var snapshot = snapshotParser.ParseSessionFile(sourceFile, new PiStoreContext("pi", null)).Data!;

        // 覆盖价：input 100×$100/M + output 10×$1000/M + cacheWrite 3×$125/M（100×1.25 缺省）
        // + cacheRead 20×$10/M（100×0.1 缺省）= 0.020575。
        shared.Usages.Should().ContainSingle().Which.Signature.BilledCost.Should().Be(0.020575m);
        // 快照价（gpt-5：1.25 / 10 / 1.5625 / 0.125）= 0.0002321875。
        snapshot.Usages.Should().ContainSingle().Which.Signature.BilledCost.Should().Be(0.0002321875m);
    }

    private static string SessionLine(string id, string timestamp, string? parent)
    {
        var line = $"{{\"type\":\"session\",\"id\":\"{id}\",\"timestamp\":\"{timestamp}\"";
        return parent is null ? line + "}" : line + $",\"parentSession\":\"{parent.Replace("\\", "\\\\")}\"}}";
    }

    private static string UsageLine(string timestamp, long input, long output, long cacheRead, long cacheWrite)
        => UsageLineWithModelAndTotal(timestamp, "gpt-5", input, output, cacheRead, cacheWrite,
            input + output + cacheRead + cacheWrite);

    private static string UsageLineWithModelAndTotal(
        string timestamp, string model, long input, long output, long cacheRead, long cacheWrite, long totalTokens)
        => $"{{\"type\":\"message\",\"timestamp\":\"{timestamp}\",\"message\":{{\"role\":\"assistant\",\"model\":\"{model}\",\"usage\":{{\"input\":{input},\"output\":{output},\"cacheRead\":{cacheRead},\"cacheWrite\":{cacheWrite},\"totalTokens\":{totalTokens}}}}}}}";

    private static string LinkedUsageLine(string id, string? parentId, string timestamp, long input)
    {
        var parentField = parentId is null ? "null" : $"\"{parentId}\"";
        return $"{{\"type\":\"message\",\"id\":\"{id}\",\"parentId\":{parentField},\"timestamp\":\"{timestamp}\",\"message\":{{\"role\":\"assistant\",\"model\":\"gpt-5\",\"usage\":{{\"input\":{input},\"output\":10,\"cacheRead\":20,\"cacheWrite\":3,\"totalTokens\":{input + 33}}}}}}}";
    }

    private static string UsageLineWithDisplayCost(string timestamp, string rawCost)
        => $"{{\"type\":\"message\",\"timestamp\":\"{timestamp}\",\"message\":{{\"role\":\"assistant\",\"model\":\"gpt-5\",\"usage\":{{\"input\":10,\"output\":20,\"cacheRead\":30,\"cacheWrite\":40,\"totalTokens\":100,\"cost\":{{\"total\":{rawCost}}}}}}}}}";

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ccusage-pi-{Guid.NewGuid():N}");
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
                // 临时目录清理失败不影响测试结论。
            }
        }
    }
}
