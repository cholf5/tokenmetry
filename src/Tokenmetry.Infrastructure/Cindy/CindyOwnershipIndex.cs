using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.Cindy;

/// <summary>
/// Cindy 产品会话元数据（provider 侧）：一条 SDK 会话 id 对应的引擎、业务会话与项目归属。
/// <para>
/// ProjectDir 是 Cindy 的产品项目归属（project_dir）；WorkingDir / WorkspaceKind 只在 project_dir
/// 缺失时参与项目推导（dialogue 会话的 working_dir 是 Cindy 自己的任务目录，不是用户项目）。
/// </para>
/// </summary>
public sealed record CindyOwnershipEntry(
    string AgentKind,
    string BusinessSessionId,
    string ProjectDir,
    string WorkingDir,
    string WorkspaceKind);

/// <summary>
/// Cindy 归属索引：只读 Cindy 的 owner 主库（<c>cindy-&lt;ownerId&gt;.db</c>，历史
/// <c>xdt-maker-*</c>），把 SDK 会话 id（Claude 的 sdk session id / Codex 的 thread id / Pi 的
/// session id）映射到产品会话元数据。
/// <para>
/// 为什么需要：Cindy 作为宿主时，Claude Code 的 transcript 在共享 <c>~/.claude</c>、Codex 的
/// rollout 在 Cindy 私有 home，光看文件只能知道"谁执行的"，不知道"是不是 Cindy 发起的"。Cindy 自己
/// 的 <c>sessions.sdk_session_id</c> 就是精确的 join key（Claude transcript 文件名与之一一对应，
/// Codex 的 sdk_session_id 即 thread id）；<c>skill_usage_sources</c> 再补上 rewind / fork 之后
/// 不再出现在 sessions 当前值里的历史 id。
/// </para>
/// <para>
/// 只读、只取会话元数据列；<b>绝不读 messages / 内容列</b>（隐私红线）。任一库读取失败时保留上一份
/// 成功索引（不把宿主归属闪断成"未知"），失败轮不推进指纹，下一轮自动重试。指纹 = 各库与 -wal
/// 的长度 / mtime，未变化时零 SQL。
/// </para>
/// </summary>
public sealed class CindyOwnershipIndex
{
    /// <summary>宿主名（与 <c>Agent</c> 维度正交的第二个标签）。</summary>
    public const string HostName = "Cindy";

    private readonly object _gate = new();
    private readonly CindyLogLocator _locator;
    private readonly string? _configuredUserDataRoot;
    private Dictionary<string, CindyOwnershipEntry> _entries = new(StringComparer.Ordinal);
    private string? _fingerprint;
    private bool _built;
    private long _revision;

    public CindyOwnershipIndex(CindyLogLocator? locator = null, string? configuredUserDataRoot = null)
    {
        _locator = locator ?? new CindyLogLocator();
        _configuredUserDataRoot = configuredUserDataRoot;
    }

    /// <summary>目录发现器（归属判定还要用 userData 根 / Codex 私有 home 清单）。</summary>
    public CindyLogLocator Locator => _locator;

    /// <summary>归属判定用的 Cindy userData 根（与索引同一配置口径）。</summary>
    public IReadOnlyList<string> ResolveUserDataRoots()
        => _locator.ResolveUserDataRoots(_configuredUserDataRoot);

    /// <summary>归属判定用的 Cindy 私有 Codex home（与索引同一配置口径）。</summary>
    public IReadOnlyList<string> ResolveCodexHomes()
        => _locator.ResolveCodexHomes(_configuredUserDataRoot);

    /// <summary>
    /// 索引版本：每次成功重建（文件戳变化且读取成功）递增。采集管道用它判断"源文件没变但宿主归属变了"
    /// 的无变化轮短路是否仍成立。
    /// </summary>
    public long Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    /// <summary>
    /// 按 owner 库文件戳刷新索引。未变化 → 零 SQL；读取失败 → 保留上一份成功索引。
    /// 可在采集管道的每轮开头调用（两次 stat 的代价）。
    /// </summary>
    public void Refresh()
    {
        IReadOnlyList<string> databases;
        try
        {
            databases = _locator.ResolveOwnerDatabases(_configuredUserDataRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var fingerprint = BuildFingerprint(databases);
        lock (_gate)
        {
            if (_built && string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }
        }

        var entries = TryRead(databases);
        if (entries is null)
        {
            // 任一库读失败：不清空、不推进指纹，下一轮重试（Cindy 正在写 / 权限抖动 / 锁）。
            return;
        }

        lock (_gate)
        {
            _entries = entries;
            _fingerprint = fingerprint;
            _built = true;
            _revision++;
        }
    }

    /// <summary>按 SDK 会话 id 查归属；未命中（非 Cindy 发起 / 库尚未记录）返回 false。</summary>
    public bool TryResolve(string? sessionId, out CindyOwnershipEntry entry)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            entry = null!;
            return false;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(sessionId.Trim(), out entry!);
        }
    }

    private static string BuildFingerprint(IReadOnlyList<string> databases)
    {
        if (databases.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var database in databases)
        {
            AppendFileStamp(builder, database);
            AppendFileStamp(builder, database + "-wal");
        }

        return builder.ToString();
    }

    private static void AppendFileStamp(System.Text.StringBuilder builder, string path)
    {
        try
        {
            var info = new FileInfo(path);
            builder.Append(path).Append('|');
            if (!info.Exists)
            {
                builder.Append("missing");
            }
            else
            {
                builder.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            builder.Append(path).Append("|error");
        }

        builder.Append('\n');
    }

    /// <summary>
    /// 读取全部 owner 库。任一库抛 SQLite / IO 异常返回 null（调用方保留上一份索引）；
    /// 库存在但尚未初始化（无 sessions 表）按空成功处理。
    /// </summary>
    private static Dictionary<string, CindyOwnershipEntry>? TryRead(IReadOnlyList<string> databases)
    {
        var entries = new Dictionary<string, CindyOwnershipEntry>(StringComparer.Ordinal);

        foreach (var database in databases)
        {
            try
            {
                using var connection = OpenReadOnly(database);
                var sessions = ReadSessions(connection);
                if (sessions.Count == 0)
                {
                    continue;
                }

                var byBusinessId = new Dictionary<string, CindyOwnershipEntry>(StringComparer.Ordinal);
                foreach (var session in sessions)
                {
                    byBusinessId[session.BusinessSessionId] = session.Entry;
                    if (!string.IsNullOrWhiteSpace(session.SdkSessionId))
                    {
                        entries.TryAdd(session.SdkSessionId, session.Entry);
                    }
                }

                // rewind / fork 会让 sessions.sdk_session_id 变成新值；历史 transcript 的 sdk id
                // 由 Cindy 的 transcript 扫描缓存补上（只读引用表，无内容）。
                foreach (var (sdkSessionId, businessSessionId) in ReadSkillUsageSourceIds(connection))
                {
                    if (byBusinessId.TryGetValue(businessSessionId, out var entry))
                    {
                        entries.TryAdd(sdkSessionId, entry);
                    }
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return entries;
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString());

        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 2000; PRAGMA query_only = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static List<SessionRow> ReadSessions(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "sessions");
        if (!columns.Contains("id") || !columns.Contains("agent_kind"))
        {
            return [];
        }

        var hasSdkSessionId = columns.Contains("sdk_session_id");
        var hasProjectDir = columns.Contains("project_dir");
        var hasWorkingDir = columns.Contains("working_dir");
        var hasWorkspaceKind = columns.Contains("workspace_kind");

        var select = new List<string> { "id", "agent_kind" };
        if (hasSdkSessionId)
        {
            select.Add("sdk_session_id");
        }

        if (hasProjectDir)
        {
            select.Add("project_dir");
        }

        if (hasWorkingDir)
        {
            select.Add("working_dir");
        }

        if (hasWorkspaceKind)
        {
            select.Add("workspace_kind");
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", select)} FROM sessions";
        using var reader = command.ExecuteReader();

        var rows = new List<SessionRow>();
        while (reader.Read())
        {
            var index = 2;
            var businessSessionId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (businessSessionId.Length == 0)
            {
                continue;
            }

            var agentKind = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var sdkSessionId = hasSdkSessionId ? ReadString(reader, index++) : string.Empty;
            var projectDir = hasProjectDir ? ReadString(reader, index++) : string.Empty;
            var workingDir = hasWorkingDir ? ReadString(reader, index++) : string.Empty;
            var workspaceKind = hasWorkspaceKind ? ReadString(reader, index) : string.Empty;

            rows.Add(new SessionRow(
                businessSessionId,
                sdkSessionId,
                new CindyOwnershipEntry(agentKind, businessSessionId, projectDir, workingDir, workspaceKind)));
        }

        return rows;
    }

    private static List<(string SdkSessionId, string BusinessSessionId)> ReadSkillUsageSourceIds(
        SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "skill_usage_sources");
        if (!columns.Contains("sdk_session_id") || !columns.Contains("session_id"))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sdk_session_id, session_id
            FROM skill_usage_sources
            WHERE sdk_session_id IS NOT NULL AND sdk_session_id <> ''
            """;
        using var reader = command.ExecuteReader();

        var rows = new List<(string, string)>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            var sdkSessionId = reader.GetString(0).Trim();
            var businessSessionId = reader.GetString(1).Trim();
            if (sdkSessionId.Length > 0 && businessSessionId.Length > 0)
            {
                rows.Add((sdkSessionId, businessSessionId));
            }
        }

        return rows;
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        // table 只来自本类常量，无注入面。
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static string ReadString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal).Trim();

    private sealed record SessionRow(string BusinessSessionId, string SdkSessionId, CindyOwnershipEntry Entry);
}
