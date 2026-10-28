using System.Text.Json;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.JsonSource;
using Tokenmetry.Infrastructure.SqliteSource;
using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.OpenCodeSqlite;

/// <summary>
/// OpenCode 本地 SQLite 数据直读适配器（I-009，移植蓝本 ../ccusage/rust/adapters/opencode/src/loader.rs
/// 的 SQLite 主线；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// 目录定位（<see cref="OpenCodeDataLocator"/>）→ 每目录选一个库（opencode.db 优先，否则按名排序取第一个
/// opencode-*.db 频道库）→ 文件戳探针（<see cref="FileStampMemo"/>，主库 + WAL 边车的 mtime/大小——
/// WAL 库写入先落边车，主库 mtime 在 checkpoint 前不变）→ 账面未动复用上轮记录（零读取零映射）→
/// 否则只读打开（共享 <see cref="SqliteReadonly"/>）→ v1 `message` 表与 v2 `session_message` 表
/// （type='assistant'）两轮扫描 → 载荷映射（<see cref="OpenCodeMessageParser"/>，lenient JSON 口径）→
/// fork 拷贝行剔除（session_v2.fork_session_id/fork_boundary + seq 边界解析，继承历史不计）→
/// 按 message id 去重（映射成功之后，跨库共用一个集合）→ 按时间排序。
/// 与 JSONL 适配器不同，账本随使用持续变化，mtime 增量不适用；以 I-042 文件戳记忆化替代每轮全量
/// 重读（与共享扫描器的零变更缓存同口径）。记忆化准入：全部发现库都成功读取才落账（FilesRead ==
/// 库数），有库打不开的轮次清空记忆下一轮重试（见 <see cref="FileStampMemo"/>）；频道库集变化
/// （选中的库路径清单变化）时指纹集合随之变化，不复用陈旧记录。只读打开不改 journal mode，
/// 兼容 OpenCode 运行中的 WAL。
/// 诊断口径沿用 <see cref="JsonlScanDiagnostics"/>（ZCodeSqlite 同款映射）：FilesDiscovered = 选中的库数、
/// FilesRead = 打开且查询完成的库数（零变更轮为 0）、LinesSkipped = 无 usage 载荷的行（user 消息等）+
/// 非 assistant 的 v2 行 + fork 拷贝行、MalformedLines = data 载荷非法 JSON 的行、InvalidRecords =
/// 身份列不可读 / 有载荷但缺 modelID/providerID / 时间戳越界的行、DuplicateRecords = 按 message id
/// 折叠的重复记录。状态：有记录且无问题 = Ok，有问题 = Partial，无记录 = NoData。
/// 蓝本的 storage/message legacy JSON 回退、会话累计表（session/session_v2）聚合回退（仅 session 报表
/// 使用）、since/until 窗口与 SQL pushdown 不在本卡范围（统一记录全量出账，见卡片偏差清单）。
/// 实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class OpenCodeSqliteAdapter : IOpenCodeSqliteAdapter
{
    /// <summary>默认库文件名（蓝本 db_path）。</summary>
    public const string DefaultDatabaseFileName = "opencode.db";

    /// <summary>v2 session_message 必需列（蓝本 prepare_session_message_query 的门槛）。</summary>
    private static readonly string[] RequiredSessionMessageColumns = ["id", "session_id", "type", "data"];

    /// <summary>v1 message 必需列（蓝本 prepare_message_query 的隐式门槛）。</summary>
    private static readonly string[] RequiredMessageColumns = ["id", "session_id", "data"];

    /// <summary>fork 边界解析必需的 session_v2 列（蓝本 fork_copy_cutoffs）。</summary>
    private static readonly string[] RequiredForkV2Columns = ["id", "fork_session_id", "fork_boundary"];

    /// <summary>fork 边界解析必需的 session_message 列（蓝本 fork_copy_cutoffs）。</summary>
    private static readonly string[] RequiredForkMessageColumns = ["id", "session_id", "seq"];

    private readonly string? _configuredDataDir;
    private readonly OpenCodeDataLocator _locator;
    private readonly FileStampMemo _memo = new();

    public OpenCodeSqliteAdapter(string? configuredDataDir = null, OpenCodeDataLocator? locator = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new OpenCodeDataLocator();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var directories = _locator.ResolveDataDirectories(_configuredDataDir);
        // 现存库清单（与既有 FilesDiscovered 语义一致：只计选中到库的目录）。
        var databasePaths = directories
            .Select(SelectDatabase)
            .Where(databasePath => databasePath is not null)
            .Select(databasePath => databasePath!)
            .ToList();
        var stamps = FileStampMemo.CaptureWithOptional(FileStampMemo.SqliteFingerprintFiles(databasePaths));

        if (_memo.TryReuse(stamps, databasePaths.Count, directories, out var reused))
        {
            return Task.FromResult(reused);
        }

        var records = new List<UsageRecord>();
        var seenMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new JsonlScanDiagnostics { FilesDiscovered = databasePaths.Count };
        var problems = false;

        foreach (var databasePath in databasePaths)
        {
            ReadDatabase(databasePath, records, seenMessageIds, diagnostics, ref problems, ct);
        }

        // 稳定排序（同时间戳保持库内读取顺序，与蓝本 stable sort_by_key 一致）。
        var sorted = records.OrderBy(static record => record.Timestamp).ToList();
        diagnostics.MissingCostRecords = sorted.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = sorted.Count(static record => record.Model is null);
        diagnostics.Status = sorted.Count == 0
            ? JsonlScanStatus.NoData
            : problems || diagnostics.InvalidRecords > 0 || diagnostics.MalformedLines > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        // FilesChanged：走到全量读取即本轮账面有变化（有库填 1）；无库不设值，
        // 由管道按"本轮发现即空"规则处理（与 ZCode 探针同口径）。
        diagnostics.FilesChanged = databasePaths.Count > 0 ? 1 : null;

        // 记忆化准入：全部发现库都成功读取才落账（有库打不开 → null 戳清空记忆，下轮重试）。
        _memo.Update(diagnostics.FilesRead == databasePaths.Count ? stamps : null, sorted, diagnostics);

        return Task.FromResult(new JsonlScanResult
        {
            Roots = directories,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 每目录选一个库（蓝本 db_path）：opencode.db 存在即用它；否则取文件名匹配频道库模式
    /// （opencode-*.db，中段仅字母数字/_/-）的文件里按完整路径排序的第一个。公开为纯函数供测试对齐。
    /// </summary>
    public static string? SelectDatabase(string directory)
    {
        var defaultPath = Path.Combine(directory, DefaultDatabaseFileName);
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        return Directory.EnumerateFiles(directory)
            .Where(path => IsChannelDatabaseName(Path.GetFileName(path)) && File.Exists(path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static bool IsChannelDatabaseName(string fileName)
    {
        const string prefix = "opencode-";
        const string suffix = ".db";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal)
            || fileName.Length <= prefix.Length + suffix.Length - 1)
        {
            return false;
        }

        // 蓝本允许中段为空（"opencode-.db" 前缀 + 后缀即过）：start..end 为空串时 all() 为 true。
        var middle = fileName[prefix.Length..^suffix.Length];
        return middle.All(IsChannelNameCharacter);
    }

    private static bool IsChannelNameCharacter(char character)
        => char.IsAsciiLetterOrDigit(character) || character is '_' or '-';

    /// <summary>
    /// 打开单个库并读出 usage 行。打开失败 / 查询失败记为问题（状态降级 Partial）并返回已读记录，
    /// 与蓝本"读失败即断、保留已读条目"一致；行级拒绝按诊断口径计数后继续。
    /// </summary>
    private void ReadDatabase(
        string databasePath,
        List<UsageRecord> records,
        HashSet<string> seenMessageIds,
        JsonlScanDiagnostics diagnostics,
        ref bool problems,
        CancellationToken ct)
    {
        try
        {
            using var connection = SqliteReadonly.Open(databasePath);

            if (SqliteTableSchema.TableExists(connection, "message"))
            {
                ScanMessageTable(connection, records, seenMessageIds, diagnostics, ct);
            }

            var hasSessionMessages = SqliteTableSchema.TableExists(connection, "session_message");
            if (hasSessionMessages)
            {
                var forkCutoffs = ForkCopyCutoffs(connection);
                ScanSessionMessageTable(connection, forkCutoffs, records, seenMessageIds, diagnostics, ct);
            }

            diagnostics.FilesRead++;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            problems = true;
        }
    }

    /// <summary>
    /// v1 `message` 表扫描（蓝本 prepare_message_query 的无窗口形态：SELECT id, session_id, data）。
    /// 列集门槛与 v2 对称：id/session_id/data 缺失时整表**静默**跳过（蓝本 prepare 失败 = 跳过 v1、
    /// 继续扫 v2、不标问题），不像打开/查询失败那样把库标为 problems。
    /// </summary>
    private static void ScanMessageTable(
        SqliteConnection connection,
        List<UsageRecord> records,
        HashSet<string> seenMessageIds,
        JsonlScanDiagnostics diagnostics,
        CancellationToken ct)
    {
        if (!SqliteTableSchema.ContainsAll(
                SqliteTableSchema.ReadColumns(connection, "message"), RequiredMessageColumns))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, session_id, data FROM message";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (TryReadText(reader, 0) is not { } id
                || TryReadText(reader, 1) is not { } sessionId
                || TryReadText(reader, 2) is not { } data)
            {
                diagnostics.InvalidRecords++;
                continue;
            }

            IngestRow(() => OpenCodeMessageParser.MapMessage(data, id, sessionId), records, seenMessageIds, diagnostics);
        }
    }

    /// <summary>
    /// v2 `session_message` 表扫描（蓝本 prepare_session_message_query + is_fork_copy）：
    /// 必需列缺失整表跳过（蓝本 prepare 失败静默）；time_created / seq 可选（缺 seq 时 fork 分类
    /// 无从判定、行保留——蓝本 read 失败 keep 同口径）；仅消费 type='assistant' 行。
    /// </summary>
    private static void ScanSessionMessageTable(
        SqliteConnection connection,
        Dictionary<string, long> forkCutoffs,
        List<UsageRecord> records,
        HashSet<string> seenMessageIds,
        JsonlScanDiagnostics diagnostics,
        CancellationToken ct)
    {
        var columns = SqliteTableSchema.ReadColumns(connection, "session_message");
        if (!SqliteTableSchema.ContainsAll(columns, RequiredSessionMessageColumns))
        {
            return;
        }

        var hasTimeCreated = columns.Contains("time_created");
        var hasSeq = columns.Contains("seq");
        var timeCreatedColumn = hasTimeCreated ? "time_created" : "NULL";
        var seqColumn = hasSeq ? ", seq" : string.Empty;

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT id, session_id, type, data, {timeCreatedColumn}{seqColumn} FROM session_message";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            // 蓝本先读 id/session_id/type、非 assistant 行在**读 data 之前**静默 continue——
            // 非 assistant 行的 data 不可读是无声非事件（计 skipped），不计 InvalidRecords。
            if (TryReadText(reader, 0) is not { } id
                || TryReadText(reader, 1) is not { } sessionId
                || TryReadText(reader, 2) is not { } messageType)
            {
                diagnostics.InvalidRecords++;
                continue;
            }

            if (!string.Equals(messageType, "assistant", StringComparison.Ordinal))
            {
                diagnostics.LinesSkipped++;
                continue;
            }

            if (TryReadText(reader, 3) is not { } data)
            {
                diagnostics.InvalidRecords++;
                continue;
            }

            var created = hasTimeCreated ? ReadCreatedColumn(reader, 4) : 0;

            // fork 拷贝行不参与映射也不占用去重名额（蓝本 is_fork_copy 在解析前剔除）；
            // 无 seq 列或 seq 不可读时行保留（蓝本 read 失败 → 非 copy）。
            if (hasSeq
                && forkCutoffs.TryGetValue(sessionId, out var cutoff)
                && ReadSeqColumn(reader, 5) is { } seq
                && seq <= cutoff)
            {
                diagnostics.LinesSkipped++;
                continue;
            }

            IngestRow(() => OpenCodeMessageParser.MapSessionMessage(data, id, sessionId, created), records, seenMessageIds, diagnostics);
        }
    }

    /// <summary>行映射产物的统一入口：拒绝原因计数 + message id 去重（映射成功之后，I-006 口径）。</summary>
    private static void IngestRow(
        Func<(UsageRecord? Record, OpenCodeRowRejection Rejection)> map,
        List<UsageRecord> records,
        HashSet<string> seenMessageIds,
        JsonlScanDiagnostics diagnostics)
    {
        UsageRecord? record;
        OpenCodeRowRejection rejection;
        try
        {
            (record, rejection) = map();
        }
        catch (JsonException)
        {
            diagnostics.MalformedLines++;
            return;
        }

        switch (rejection)
        {
            case OpenCodeRowRejection.NoUsagePayload:
                diagnostics.LinesSkipped++;
                return;
            case OpenCodeRowRejection.MissingIdentity:
            case OpenCodeRowRejection.TimestampOutOfRange:
                diagnostics.InvalidRecords++;
                return;
        }

        // 去重发生在映射成功之后；无 message id 的记录不去重直接保留（蓝本 entry_id None → push）。
        if (record!.MessageId is { } messageId
            && !seenMessageIds.Add(messageId))
        {
            diagnostics.DuplicateRecords++;
            return;
        }

        records.Add(record!);
    }

    /// <summary>
    /// fork 会话的继承历史截断点（蓝本 fork_copy_cutoffs）：session_v2 携带 fork 链接与边界消息，
    /// session_message 携带拷贝行保留的 seq。无法解析边界（缺父行 / 缺边界行 / 载荷不可解析）时不
    /// 记录截断点——fork 保留全部行（蓝本"keeps every row"口径）。
    /// </summary>
    private static Dictionary<string, long> ForkCopyCutoffs(SqliteConnection connection)
    {
        var cutoffs = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!SqliteTableSchema.TableExists(connection, "session_v2")
            || !SqliteTableSchema.TableExists(connection, "session_message"))
        {
            return cutoffs;
        }

        if (!SqliteTableSchema.ContainsAll(SqliteTableSchema.ReadColumns(connection, "session_v2"), RequiredForkV2Columns)
            || !SqliteTableSchema.ContainsAll(SqliteTableSchema.ReadColumns(connection, "session_message"), RequiredForkMessageColumns))
        {
            return cutoffs;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, fork_session_id, fork_boundary FROM session_v2 "
            + "WHERE fork_session_id IS NOT NULL AND fork_boundary IS NOT NULL";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (TryReadText(reader, 0) is not { } forkId
                || TryReadText(reader, 1) is not { } parentId
                || TryReadText(reader, 2) is not { } boundary)
            {
                continue;
            }

            if (forkId.Length == 0 || parentId.Length == 0)
            {
                continue;
            }

            if (TryResolveForkCopyCutoff(connection, parentId, boundary, out var cutoff))
            {
                cutoffs[forkId] = cutoff;
            }
        }

        return cutoffs;
    }

    /// <summary>
    /// 单个 fork 的最大拷贝 seq（蓝本 fork_copy_cutoff）：boundary.type='through' → 父边界消息的 seq；
    /// 'before' → 父会话中小于边界 seq 的最大 seq（seq 稀疏时 fork 自有行紧接最后一条拷贝行）。
    /// </summary>
    private static bool TryResolveForkCopyCutoff(SqliteConnection connection, string parentId, string boundary, out long cutoff)
    {
        cutoff = 0;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(boundary);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || LenientJson.GetNonEmptyString(root, "type") is not { } boundaryType
                || LenientJson.GetNonEmptyString(root, "messageID") is not { } parentMessageId)
            {
                return false;
            }

            var boundarySeq = QueryScalarLong(
                connection,
                "SELECT seq FROM session_message WHERE session_id = $parentId AND id = $messageId LIMIT 1",
                ("$parentId", parentId),
                ("$messageId", parentMessageId));
            if (boundarySeq is not { } seq)
            {
                return false;
            }

            switch (boundaryType)
            {
                case "through":
                    cutoff = seq;
                    return true;
                case "before":
                    return TryLastParentSeqBefore(connection, parentId, seq, out cutoff);
                default:
                    return false;
            }
        }
    }

    /// <summary>before 边界的拷贝上界（蓝本 last_parent_seq_before）：MAX(seq) WHERE seq &lt; boundarySeq；
    /// 空集返回 None（没有拷贝行，全保留）。</summary>
    private static bool TryLastParentSeqBefore(SqliteConnection connection, string parentId, long boundarySeq, out long cutoff)
    {
        cutoff = 0;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(seq) FROM session_message WHERE session_id = $parentId AND seq < $boundarySeq";
        command.Parameters.AddWithValue("$parentId", parentId);
        command.Parameters.AddWithValue("$boundarySeq", boundarySeq);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return false;
        }

        try
        {
            cutoff = reader.GetInt64(0);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>单值 long 查询；无行 / NULL / 类型不可读返回 null（蓝本 read::&lt;i64&gt;.ok()? 口径）。</summary>
    private static long? QueryScalarLong(SqliteConnection connection, string sql, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return null;
        }

        try
        {
            return reader.GetInt64(0);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>身份/文本列：NULL 或类型异常视为不可读（返回 null，行按蓝本静默跳过、此处计数）。</summary>
    private static string? TryReadText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            return reader.GetString(ordinal);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// v2 created 列（蓝本 read i64 → f64 截断 → 其余 0）：NULL / 文本 / 类型异常按 0；
    /// f64 溢出按 Rust as-cast 饱和语义钳到 long 边界。
    /// </summary>
    private static long ReadCreatedColumn(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        try
        {
            return reader.GetInt64(ordinal);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            try
            {
                var value = reader.GetDouble(ordinal);
                if (!double.IsFinite(value))
                {
                    return 0;
                }

                return value switch
                {
                    >= 9223372036854775808.0 => long.MaxValue,
                    <= -9223372036854775808.0 => long.MinValue,
                    _ => (long)value,
                };
            }
            catch (Exception fallbackException) when (fallbackException is FormatException or InvalidCastException)
            {
                return 0;
            }
        }
    }

    /// <summary>fork 分类的 seq 列：i64 直读；NULL / 类型异常返回 null（行保留，蓝本 read 失败同口径）。</summary>
    private static long? ReadSeqColumn(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            return reader.GetInt64(ordinal);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return null;
        }
    }
}
