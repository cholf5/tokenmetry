namespace Tokenmetry.Infrastructure.Tests.Cindy;

using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.CodexJsonl;
using Tokenmetry.Infrastructure.Cindy;

/// <summary>
/// 宿主归属后处理测试：Agent（引擎）不变，Host / 项目覆盖按 owner 库重算；
/// 未命中索引的保持"无宿主"；Cindy 内部目录不再冒充项目；每轮先清空、结果权威。
/// </summary>
public class CindyHostAttributionTests : IDisposable
{
    private const string CodexThreadId = "11111111-1111-4111-8111-111111111111";
    private const string CodexRolloutName =
        "rollout-2026-05-25T16-00-18-11111111-1111-4111-8111-111111111111.jsonl";

    private readonly List<string> _tempDirs = new();

    [Fact]
    public void Attributes_claude_codex_and_pi_records_and_overrides_product_projects()
    {
        var userData = CreateCindyLayout();
        CreateOwnerDatabase(userData,
        [
            new SessionSeed("biz-cc", "cc", "sdk-claude", "E:/dev/demo", "E:/dev/demo", "project"),
            new SessionSeed("biz-codex", "codex", CodexThreadId, "E:/dev/skynet", "E:/dev/skynet", "project"),
            new SessionSeed("biz-pi", "pi", "sdk-pi", "", "E:/Cindy/owners/x/dialogues/2026-11-10/task", "dialogue"),
        ]);
        var records = new List<UsageRecord>
        {
            Record(ClaudeCodeJsonlLineParser.AgentName, "sdk-claude",
                "C:/fixtures/.claude/projects/-Cindy-owners-hash-dialogues-2026-11-10-task/sdk-claude.jsonl",
                "-Cindy-owners-hash-dialogues-2026-11-10-task"),
            Record(CodexJsonlFileParser.AgentName, "2026/05/25/" + CodexRolloutName,
                Path.Combine(userData, "codex-home", "sessions", "2026", "05", "25", CodexRolloutName),
                "E:/Cindy/owners/x/dialogues/2026-11-10/task"),
            Record(CindyPiAdapter.AgentName, "sdk-pi",
                Path.Combine(userData, "pi-agent-home", "sessions", "2026-11-10-task_sdk-pi.jsonl"),
                "E:/Cindy/owners/x/dialogues/2026-11-10/task"),
        };

        CindyHostAttribution.Apply(records, BuildIndex(userData));

        records.Should().OnlyContain(record => record.Host == "Cindy");
        records[0].EffectiveProjectPath.Should().Be("E:/dev/demo");
        records[1].EffectiveProjectPath.Should().Be("E:/dev/skynet");
        records[2].EffectiveProjectPath.Should().Be("Unknown Project", "dialogue 会话不是用户项目");
        records[0].Agent.Should().Be(ClaudeCodeJsonlLineParser.AgentName, "宿主不替代执行引擎");
    }

    [Fact]
    public void Leaves_standalone_cli_records_without_host()
    {
        var userData = CreateCindyLayout();
        var index = BuildIndex(userData);
        var records = new List<UsageRecord>
        {
            Record(ClaudeCodeJsonlLineParser.AgentName, "not-owned",
                "C:/fixtures/.claude/projects/-dev-demo/not-owned.jsonl", "-dev-demo"),
            Record(CodexJsonlFileParser.AgentName, "2026/05/25/" + CodexRolloutName,
                Path.Combine(Path.GetTempPath(), "codex-cli-home", "sessions", CodexRolloutName), "E:/dev/skynet"),
        };

        CindyHostAttribution.Apply(records, index);

        records.Should().OnlyContain(record => record.Host.Length == 0);
        records[0].EffectiveProjectPath.Should().Be("-dev-demo");
        records[1].EffectiveProjectPath.Should().Be("E:/dev/skynet");
    }

    [Fact]
    public void Codex_rollout_in_cindy_private_home_is_hosted_even_without_database_row()
    {
        var userData = CreateCindyLayout();
        var index = BuildIndex(userData); // 空库：没有 thread 行
        var sourceFile = Path.Combine(userData, "codex-home", "sessions", CodexRolloutName);
        var record = Record(CodexJsonlFileParser.AgentName, "2026/05/25/" + CodexRolloutName, sourceFile, "E:/dev/skynet");

        CindyHostAttribution.Apply([record], index);

        record.Host.Should().Be("Cindy", "文件就在 Cindy 私有 CODEX_HOME 下");
        record.EffectiveProjectPath.Should().Be("E:/dev/skynet", "没有产品项目数据时保留 rollout cwd");
    }

    [Fact]
    public void Normalizes_cindy_internal_claude_project_even_without_ownership_match()
    {
        var userData = CreateCindyLayout();
        var index = BuildIndex(userData);
        var record = Record(ClaudeCodeJsonlLineParser.AgentName, "unknown-sdk",
            "C:/fixtures/.claude/projects/-Cindy-owners-hash-dialogues-2026-11-10-task/unknown-sdk.jsonl",
            "-Cindy-owners-hash-dialogues-2026-11-10-task");

        CindyHostAttribution.Apply([record], index);

        record.Host.Should().BeEmpty();
        record.EffectiveProjectPath.Should().Be("Unknown Project", "Cindy owners 目录不能冒充用户项目");
    }

    [Fact]
    public void Reapplying_clears_stale_host_and_override()
    {
        var userData = CreateCindyLayout();
        CreateOwnerDatabase(userData,
            [new SessionSeed("biz-cc", "cc", "sdk-claude", "E:/dev/demo", "E:/dev/demo", "project")]);
        var record = Record(ClaudeCodeJsonlLineParser.AgentName, "sdk-claude",
            "C:/fixtures/.claude/projects/-dev-demo/sdk-claude.jsonl", "-dev-demo");

        CindyHostAttribution.Apply([record], BuildIndex(userData));
        record.Host.Should().Be("Cindy");
        record.EffectiveProjectPath.Should().Be("E:/dev/demo");

        // 换成一个找不到任何 owner 库的配置：下一轮必须回落成"无宿主"，不能残留上一轮结果。
        var emptyUserData = CreateCindyLayout();
        CindyHostAttribution.Apply([record], BuildIndex(emptyUserData));

        record.Host.Should().BeEmpty();
        record.ProjectPathOverride.Should().BeNull();
        record.EffectiveProjectPath.Should().Be("-dev-demo", "原适配器推导值不被改写");
    }

    // ---- helpers ----

    private static CindyOwnershipIndex BuildIndex(string userData)
    {
        var index = new CindyOwnershipIndex(new CindyLogLocator(_ => null), userData);
        index.Refresh();
        return index;
    }

    private static UsageRecord Record(string agent, string sessionId, string sourceFile, string projectPath) => new()
    {
        Timestamp = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero),
        Agent = agent,
        SessionId = sessionId,
        SourceFile = sourceFile,
        ProjectPath = projectPath,
        InputTokens = 10,
        OutputTokens = 5,
    };

    private sealed record SessionSeed(
        string Id, string AgentKind, string SdkSessionId, string ProjectDir, string WorkingDir, string WorkspaceKind);

    private string CreateCindyLayout()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "pi-agent-home", "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "codex-home", "sessions"));
        return root;
    }

    private static void CreateOwnerDatabase(string userData, IReadOnlyList<SessionSeed> sessions)
    {
        var path = Path.Combine(userData, "cindy-owner.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY,
                    agent_kind TEXT NOT NULL DEFAULT 'cc',
                    sdk_session_id TEXT,
                    project_dir TEXT,
                    working_dir TEXT,
                    workspace_kind TEXT NOT NULL DEFAULT 'project'
                );
                """;
            command.ExecuteNonQuery();
        }

        foreach (var session in sessions)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sessions (id, agent_kind, sdk_session_id, project_dir, working_dir, workspace_kind)
                VALUES ($id, $kind, $sdk, $project, $working, $workspace)
                """;
            command.Parameters.AddWithValue("$id", session.Id);
            command.Parameters.AddWithValue("$kind", session.AgentKind);
            command.Parameters.AddWithValue("$sdk", session.SdkSessionId);
            command.Parameters.AddWithValue("$project", session.ProjectDir);
            command.Parameters.AddWithValue("$working", session.WorkingDir);
            command.Parameters.AddWithValue("$workspace", session.WorkspaceKind);
            command.ExecuteNonQuery();
        }
    }

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tokenmetry-cindy-attribution-{Guid.NewGuid():N}");
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
