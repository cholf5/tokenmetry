namespace Tokenmetry.Infrastructure.Tests.Cindy;

using Microsoft.Data.Sqlite;
using Tokenmetry.Infrastructure.Cindy;

/// <summary>
/// Cindy 归属索引测试：owner 库 → SDK 会话 id 的映射（只读、只取元数据列）、历史 id 补齐、
/// 读失败保留上一份索引。fixture 用真实的 SQLite 文件，表结构取 Cindy owner 库的相关子集。
/// </summary>
public class CindyOwnershipIndexTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void Resolves_sdk_session_ids_to_product_session_metadata()
    {
        var userData = CreateTempDir();
        CreateOwnerDatabase(userData, "cindy-owner.db",
        [
            new SessionSeed("biz-cc", "cc", "sdk-claude", "E:/dev/demo", "E:/dev/demo", "project"),
            new SessionSeed("biz-codex", "codex", "019c2954-c567-7fd1-975b-50b2e70955e1", "E:/dev/skynet", "E:/dev/skynet", "project"),
            new SessionSeed("biz-pi", "pi", "sdk-pi", "", "E:/Cindy/owners/x/dialogues/2026-11-10/task", "dialogue"),
        ]);
        var index = new CindyOwnershipIndex(new CindyLogLocator(_ => null), userData);

        index.Refresh();

        index.TryResolve("sdk-claude", out var claude).Should().BeTrue();
        claude.AgentKind.Should().Be("cc");
        claude.BusinessSessionId.Should().Be("biz-cc");
        claude.ProjectDir.Should().Be("E:/dev/demo");

        index.TryResolve("019c2954-c567-7fd1-975b-50b2e70955e1", out var codex).Should().BeTrue();
        codex.AgentKind.Should().Be("codex");
        codex.ProjectDir.Should().Be("E:/dev/skynet");

        index.TryResolve("sdk-pi", out var pi).Should().BeTrue();
        pi.AgentKind.Should().Be("pi");
        pi.WorkspaceKind.Should().Be("dialogue");

        index.TryResolve("not-owned", out _).Should().BeFalse();
    }

    [Fact]
    public void Uses_skill_usage_sources_for_historical_sdk_ids()
    {
        // rewind / fork 之后 sessions.sdk_session_id 变成新值；历史 transcript 的 sdk id 只留在
        // skill_usage_sources（transcript 扫描缓存）里，仍应归到同一个产品会话。
        var userData = CreateTempDir();
        CreateOwnerDatabase(userData, "cindy-owner.db",
            [new SessionSeed("biz-cc", "cc", "sdk-new", "E:/dev/demo", "E:/dev/demo", "project")],
            [("raw-file", "sdk-old", "biz-cc")]);
        var index = new CindyOwnershipIndex(new CindyLogLocator(_ => null), userData);

        index.Refresh();

        index.TryResolve("sdk-old", out var historical).Should().BeTrue();
        historical.BusinessSessionId.Should().Be("biz-cc");
        historical.ProjectDir.Should().Be("E:/dev/demo");
    }

    [Fact]
    public void Discovers_legacy_owner_database_and_ignores_lock_databases()
    {
        var userData = CreateTempDir();
        CreateOwnerDatabase(userData, "xdt-maker-owner.db",
            [new SessionSeed("biz", "cc", "sdk-legacy", "E:/dev/demo", "E:/dev/demo", "project")]);
        // 同目录的非 owner 库（设备锁）不得被当成归属来源。
        File.WriteAllText(Path.Combine(userData, "device-link-ownership-user.lock.db"), "not a database");
        var locator = new CindyLogLocator(_ => null);

        var databases = locator.ResolveOwnerDatabases(userData);

        databases.Should().ContainSingle().Which.Should().EndWith("xdt-maker-owner.db");
    }

    [Fact]
    public void No_owner_database_yields_empty_index()
    {
        var userData = CreateTempDir();
        var index = new CindyOwnershipIndex(new CindyLogLocator(_ => null), userData);

        index.Refresh();

        index.TryResolve("anything", out _).Should().BeFalse();
    }

    [Fact]
    public void Unreadable_database_keeps_the_previous_index()
    {
        var userData = CreateTempDir();
        var database = CreateOwnerDatabase(userData, "cindy-owner.db",
            [new SessionSeed("biz", "cc", "sdk-keep", "E:/dev/demo", "E:/dev/demo", "project")]);
        var index = new CindyOwnershipIndex(new CindyLogLocator(_ => null), userData);
        index.Refresh();
        index.TryResolve("sdk-keep", out _).Should().BeTrue();
        var revision = index.Revision;

        // 把它换成非 SQLite 文件：读取失败必须保留上一份索引（宿主归属不能闪断成"未知"）。
        File.WriteAllText(database, "this is not a sqlite database");
        index.Refresh();

        index.TryResolve("sdk-keep", out _).Should().BeTrue();
        index.Revision.Should().Be(revision, "失败的刷新不推进版本");
    }

    [Fact]
    public void Revision_advances_when_sessions_change()
    {
        var userData = CreateTempDir();
        var database = CreateOwnerDatabase(userData, "cindy-owner.db",
            [new SessionSeed("biz", "cc", "sdk-one", "E:/dev/one", "E:/dev/one", "project")]);
        var index = new CindyOwnershipIndex(new CindyLogLocator(_ => null), userData);

        index.Refresh();
        var firstRevision = index.Revision;
        index.Refresh();
        index.Revision.Should().Be(firstRevision, "文件戳未变 → 零 SQL 零版本变化");

        AppendSession(database, new SessionSeed("biz-2", "codex", "thread-2", "E:/dev/two", "E:/dev/two", "project"));
        index.Refresh();

        index.Revision.Should().BeGreaterThan(firstRevision);
        index.TryResolve("thread-2", out _).Should().BeTrue();
    }

    // ---- helpers ----

    private sealed record SessionSeed(
        string Id,
        string AgentKind,
        string SdkSessionId,
        string ProjectDir,
        string WorkingDir,
        string WorkspaceKind);

    private string CreateOwnerDatabase(
        string userDataRoot,
        string fileName,
        IReadOnlyList<SessionSeed> sessions,
        IReadOnlyList<(string RawFile, string SdkSessionId, string SessionId)>? skillSources = null)
    {
        var path = Path.Combine(userDataRoot, fileName);
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
                CREATE TABLE skill_usage_sources (
                    raw_file_path TEXT PRIMARY KEY,
                    sdk_session_id TEXT NOT NULL,
                    session_id TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        foreach (var session in sessions)
        {
            InsertSession(connection, session);
        }

        foreach (var (rawFile, sdkSessionId, sessionId) in skillSources ?? [])
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO skill_usage_sources (raw_file_path, sdk_session_id, session_id)
                VALUES ($raw, $sdk, $session)
                """;
            command.Parameters.AddWithValue("$raw", rawFile);
            command.Parameters.AddWithValue("$sdk", sdkSessionId);
            command.Parameters.AddWithValue("$session", sessionId);
            command.ExecuteNonQuery();
        }

        return path;
    }

    private static void AppendSession(string databasePath, SessionSeed session)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        InsertSession(connection, session);
    }

    private static void InsertSession(SqliteConnection connection, SessionSeed session)
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

    private string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tokenmetry-cindy-ownership-{Guid.NewGuid():N}");
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
