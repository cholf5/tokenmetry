using System.Text.Json;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.JsonSource;
using CCUsage.Infrastructure.SqliteSource;
using Microsoft.Data.Sqlite;

namespace CCUsage.Infrastructure.OpenClaw;

/// <summary>单个 agent 库的读取产出（蓝本 read_agent_database 返回 Vec&lt;LoadedEntry&gt; + 各 debug 分支的显式计数化）。</summary>
internal sealed record OpenClawSqliteReadResult(
    List<UsageRecord> Records,
    bool Opened,
    int MalformedRows,
    int InvalidRows,
    int SkippedRows);

/// <summary>
/// OpenClaw per-agent SQLite store 读取（蓝本 ../ccusage/rust/adapters/openclaw/src/sqlite.rs 逐条移植，
/// I-020；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。每根下
/// agents/&lt;agentId&gt;/agent/openclaw-agent.sqlite（<see cref="OpenClawLogLocator"/> 同款定位）：
/// 只读打开（共享 <see cref="SqliteReadonly"/>，hostile store 不可写、兼容运行中的 WAL）→
/// transcript_events 表存在性检查（缺失按蓝本 debug 静默跳过、不算问题）→
/// `SELECT session_id, seq, event_json, created_at ORDER BY session_id ASC, seq ASC` →
/// 每行 event_json 复用 <see cref="OpenClawLogParser"/> 的行映射（蓝本 OpenClawLine 双格式同构）。
/// 模型 / provider 跟踪状态<b>按会话重置</b>（行序按 session_id 分组连续，会话边界即状态边界——
/// 与 JSONL 逐文件状态等价）。列类型纪律与 rusqlite 一致：session_id / event_json 仅 TEXT 可读
/// （其余类型按行拒绝计 InvalidRecords）；created_at 接受 INTEGER（REAL 截断；其余 0）。
/// 坏 JSON 载荷行计 MalformedRows 后继续（追加日志单行坏不弃会话）；打开 / 查询失败按
/// "该库本轮 0 条 + 问题标记"处理（蓝本 debug 后返回空表，状态降级 Partial——Kilo 同款登记）。
/// </summary>
public static class OpenClawSqliteReader
{
    /// <summary>查询与蓝本 sqlite.rs 同构（固定列序 + 双键排序）。</summary>
    internal const string TranscriptQuery =
        "SELECT session_id, seq, event_json, created_at FROM transcript_events ORDER BY session_id ASC, seq ASC";

    /// <summary>
    /// 发现根下全部 agent 库（蓝本 collect_agent_databases）：agents/ 与 agent/ 目录本身、库文件
    /// 均不可为符号链接（lstat 语义），按完整路径排序。
    /// </summary>
    public static IReadOnlyList<string> CollectAgentDatabases(string root)
    {
        var databases = new List<string>();
        var agentsPath = Path.Combine(root, "agents");
        if (!IsDirectoryWithoutSymlink(agentsPath))
        {
            return databases;
        }

        var agentsDirectory = new DirectoryInfo(agentsPath);
        FileSystemInfo[] entries;
        try
        {
            entries = agentsDirectory.EnumerateFileSystemInfos().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return databases;
        }

        foreach (var agent in entries)
        {
            if (agent is not DirectoryInfo || (agent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var agentAgentPath = Path.Combine(agent.FullName, "agent");
            if (!IsDirectoryWithoutSymlink(agentAgentPath))
            {
                continue;
            }

            var databasePath = Path.Combine(agentAgentPath, OpenClawLogLocator.DatabaseFileName);
            if (IsFileWithoutSymlink(databasePath))
            {
                databases.Add(databasePath);
            }
        }

        return databases.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 读取单个 agent 库，产出 (session_id, seq) 序的 usage 记录。打开失败 / 查询中途失败返回
    /// <c>Opened=false</c>（调用方计问题标记）；表缺失返回空产出且不算问题（蓝本 debug 口径）。
    /// </summary>
    internal static OpenClawSqliteReadResult ReadAgentDatabase(string databasePath, CancellationToken ct)
    {
        var records = new List<UsageRecord>();
        var malformedRows = 0;
        var invalidRows = 0;
        var skippedRows = 0;
        try
        {
            using var connection = SqliteReadonly.Open(databasePath);
            if (!TableExists(connection))
            {
                return new OpenClawSqliteReadResult(records, Opened: true, malformedRows, invalidRows, skippedRows);
            }

            using var command = connection.CreateCommand();
            command.CommandText = TranscriptQuery;
            using var reader = command.ExecuteReader();

            // 行序按 session_id 分组连续：会话边界重置跟踪状态（蓝本 current_session 同构）。
            string? currentSession = null;
            string? currentModel = null;
            string? currentProvider = null;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (TryReadText(reader, 0) is not { } sessionId)
                {
                    invalidRows++;
                    continue;
                }

                if (currentSession != sessionId)
                {
                    currentSession = sessionId;
                    currentModel = null;
                    currentProvider = null;
                }

                var createdAt = ReadCreatedAt(reader, 3);
                var fallbackTimestamp = createdAt is > 0 and <= OpenClawLogParser.MaxUnixMilliseconds
                    ? DateTimeOffset.UnixEpoch.AddMilliseconds(createdAt.Value)
                    : StrictRfc3339Timestamp.Epoch;

                if (TryReadText(reader, 2) is not { } eventJson)
                {
                    invalidRows++;
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(eventJson);
                }
                catch (JsonException)
                {
                    malformedRows++;
                    continue;
                }

                using (document)
                {
                    if (OpenClawLogParser.ParseLine(document.RootElement) is not { } parsed)
                    {
                        malformedRows++;
                        continue;
                    }

                    var result = OpenClawLogParser.MapLine(
                        parsed, sessionId, currentModel, currentProvider, fallbackTimestamp, databasePath,
                        useEventIdAsMessageId: true);
                    switch (result.Outcome)
                    {
                        case OpenClawLineOutcome.Produced:
                            records.Add(result.Record!);
                            break;
                        case OpenClawLineOutcome.ModelChange:
                            var (model, provider) = OpenClawLogParser.ModelChangeSource(parsed);
                            if (model is not null)
                            {
                                currentModel = model;
                            }

                            if (provider is not null)
                            {
                                currentProvider = provider;
                            }

                            break;
                        case OpenClawLineOutcome.MessageUnusable:
                            invalidRows++;
                            break;
                        default:
                            skippedRows++;
                            break;
                    }
                }
            }

            return new OpenClawSqliteReadResult(records, Opened: true, malformedRows, invalidRows, skippedRows);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // 蓝本逐行 Err → debug + break（保留已读记录）；此处保留已读记录并置问题标记（状态降级 Partial）。
            return new OpenClawSqliteReadResult(records, Opened: false, malformedRows, invalidRows, skippedRows);
        }
    }

    /// <summary>transcript_events 表存在性（蓝本 table_exists：sqlite_master 按 name 查表）。</summary>
    private static bool TableExists(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", "transcript_events");
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// 文本列（蓝本 read::&lt;String&gt;）：NULL 或非 TEXT 类型视为不可读（rusqlite 严格类型同口径，
    /// 行计 InvalidRecords 后继续）。
    /// </summary>
    private static string? TryReadText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) is string text ? text : null;
    }

    /// <summary>
    /// created_at（蓝本 read::&lt;i64&gt; 失败回退 read::&lt;f64&gt;.trunc() 再失败 0）：INTEGER 原样、
    /// REAL 截断、其余 0；负值由调用方 max(0) 归纪元。
    /// </summary>
    private static long? ReadCreatedAt(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        return reader.GetValue(ordinal) switch
        {
            long value => value,
            double value => (long)Math.Truncate(value),
            _ => 0,
        };
    }

    /// <summary>目录存在且非符号链接（蓝本 is_directory_without_symlink：symlink_metadata lstat 语义——
    /// 经父目录枚举项判定，枚举项的 ReparsePoint 属性即链接本身、不跟随目标，跨平台确定性）。</summary>
    private static bool IsDirectoryWithoutSymlink(string path)
        => FindEntryWithoutFollowing(path) is DirectoryInfo directory
           && (directory.Attributes & FileAttributes.ReparsePoint) == 0;

    /// <summary>文件存在且非符号链接（蓝本 is_file_without_symlink 同上）。</summary>
    private static bool IsFileWithoutSymlink(string path)
        => FindEntryWithoutFollowing(path) is FileInfo file
           && (file.Attributes & FileAttributes.ReparsePoint) == 0;

    /// <summary>按精确路径取目录枚举项（lstat 语义，不跟随符号链接）；父目录不存在 / 读不了返回 null。</summary>
    private static FileSystemInfo? FindEntryWithoutFollowing(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parentPath = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        if (parentPath is null || name.Length == 0)
        {
            return null;
        }

        try
        {
            var parentDirectory = new DirectoryInfo(parentPath);
            if (!parentDirectory.Exists)
            {
                return null;
            }

            return parentDirectory.EnumerateFileSystemInfos()
                .FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
