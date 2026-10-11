using System.Text.Json;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.Jsonl;
using CCUsage.Infrastructure.KiloSqlite;
using CCUsage.Infrastructure.SqliteSource;
using Microsoft.Data.Sqlite;

namespace CCUsage.Infrastructure.KiloSqlite;

/// <summary>
/// Kilo 本地 SQLite 数据直读适配器（I-017，移植蓝本 ../ccusage/rust/adapters/kilo/src/loader.rs；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// 目录定位（<see cref="KiloDataLocator"/>：KILO_DATA_DIR 多目录 / 默认 ~/.local/share/kilo）→
/// 每目录固定取 &lt;目录&gt;/kilo.db → 只读打开（共享 <see cref="SqliteReadonly"/>）→
/// `SELECT id, session_id, data FROM message` → 载荷映射（<see cref="KiloMessageParser"/>，lenient
/// JSON 口径，仅 assistant 行）→ 按 message id 去重（映射成功之后；无内嵌 id 的行回退
/// "&lt;db 路径&gt;:&lt;row id&gt;"，与蓝本同式）→ 按时间稳定排序。
/// 数据随使用持续变化，mtime 增量不适用，每次全量重读；只读打开兼容 Kilo 运行中的 WAL。
/// 诊断口径沿用 <see cref="JsonlScanDiagnostics"/>（ZCodeSqlite 口径映射）：FilesDiscovered = 选中的库数、
/// FilesRead = 打开且查询完成的库数、LinesSkipped = 非 assistant 行 / 无 tokens 载荷行 / 全零行、
/// MalformedLines = data 载荷非法 JSON 的行、InvalidRecords = 身份列不可读 / 有载荷但缺 modelID /
/// 时间戳不可用或越界的行、DuplicateRecords = 按 message id 折叠的重复记录。
/// 打开 / prepare / 查询失败按"该库本轮 0 条 + 问题标记"处理（蓝本返回空列表同语义，状态降级 Partial）。
/// 实例无扫描状态，线程安全；以单例接入 App DI。
/// </summary>
public class KiloSqliteAdapter : IKiloSqliteAdapter
{
    /// <summary>查询与蓝本 loader.rs 同构（固定列序 id, session_id, data）。</summary>
    internal const string MessageQuery = "SELECT id, session_id, data FROM message";

    private readonly string? _configuredDataDir;
    private readonly KiloDataLocator _locator;

    public KiloSqliteAdapter(string? configuredDataDir = null, KiloDataLocator? locator = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new KiloDataLocator();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var directories = _locator.ResolveDataDirectories(_configuredDataDir);
        var records = new List<UsageRecord>();
        var seenMessageIds = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new JsonlScanDiagnostics();
        var problems = false;

        foreach (var directory in directories)
        {
            var databasePath = Path.Combine(directory, KiloDataLocator.DatabaseFileName);
            if (!File.Exists(databasePath))
            {
                continue;
            }

            diagnostics.FilesDiscovered++;
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

        return Task.FromResult(new JsonlScanResult
        {
            Roots = directories,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 打开单个库并读出 usage 行。打开失败 / 查询失败记为问题（状态降级 Partial）并返回已读记录；
    /// 行级拒绝按诊断口径计数后继续。
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
            using var command = connection.CreateCommand();
            command.CommandText = MessageQuery;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (TryReadText(reader, 0) is not { } rowId
                    || TryReadText(reader, 1) is not { } rowSessionId
                    || TryReadText(reader, 2) is not { } data)
                {
                    diagnostics.InvalidRecords++;
                    continue;
                }

                IngestRow(databasePath, data, rowId, rowSessionId, records, seenMessageIds, diagnostics);
            }

            diagnostics.FilesRead++;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            problems = true;
        }
    }

    /// <summary>行映射产物的统一入口：拒绝原因计数 + message id 去重（映射成功之后，I-006 口径）。</summary>
    private static void IngestRow(
        string databasePath,
        string data,
        string rowId,
        string rowSessionId,
        List<UsageRecord> records,
        HashSet<string> seenMessageIds,
        JsonlScanDiagnostics diagnostics)
    {
        UsageRecord? record;
        KiloRowRejection rejection;
        try
        {
            (record, rejection) = KiloMessageParser.MapMessage(data, rowId, rowSessionId, databasePath);
        }
        catch (JsonException)
        {
            diagnostics.MalformedLines++;
            return;
        }

        switch (rejection)
        {
            case KiloRowRejection.NoUsagePayload:
                diagnostics.LinesSkipped++;
                return;
            case KiloRowRejection.MissingIdentity:
            case KiloRowRejection.UnparsableTimestamp:
            case KiloRowRejection.TimestampOutOfRange:
                diagnostics.InvalidRecords++;
                return;
        }

        // 去重发生在映射成功之后；message id 由载荷 id 或 "<db>:<row id>" 回退构成，恒非空。
        if (record!.MessageId is { } messageId
            && !seenMessageIds.Add(messageId))
        {
            diagnostics.DuplicateRecords++;
            return;
        }

        records.Add(record!);
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
}
