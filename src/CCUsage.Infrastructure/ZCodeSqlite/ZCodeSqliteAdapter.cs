using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using Microsoft.Data.Sqlite;

namespace CCUsage.Infrastructure.ZCodeSqlite;

/// <summary>
/// ZCode SQLite 账本直读适配器（I-006，蓝本 ../ccusage/rust/adapters/zcode/）：
/// 库定位（<see cref="ZCodeLogLocator"/>）→ 逐库只读打开 → schema 检查（<see cref="ZCodeSchemaInspector"/>，
/// 按可选列拼查询）→ 读 completed 行 → 行映射（<see cref="ZCodeRecordMapper"/>）→ 按 usage id 去重 →
/// 按时间排序 → 统一记录 + 诊断计数。
/// 与 JSONL 适配器不同，账本是随使用持续变化的单一库文件，mtime 增量不适用，每次全量重读
/// （completed 行为万级，全读代价可忽略；库内 id 主键 + 去重保证重复扫描不产生重复记录）。
/// 只读模式打开且不改 journal mode，兼容 ZCode 运行中的 WAL。
/// 诊断字段沿用 <see cref="JsonlScanDiagnostics"/> 的最近口径：FilesDiscovered = 发现的库数、
/// FilesRead = 成功打开且 schema 支持并完成查询的库数、InvalidRecords = 身份/时间/负值/全零被
/// 拒绝的行数、DuplicateRecords = 按 id 折叠的跨库重复行；行级 malformed 概念不存在（Malformed/LinesSkipped 恒 0）。
/// 实例无扫描状态（每次全量），线程安全；仍以单例接入 App DI 与既有接线保持一致。
/// </summary>
public class ZCodeSqliteAdapter : IZCodeSqliteAdapter
{
    private readonly string? _configuredHome;
    private readonly ZCodeLogLocator _locator;

    public ZCodeSqliteAdapter(string? configuredHome = null, ZCodeLogLocator? locator = null)
    {
        _configuredHome = configuredHome;
        _locator = locator ?? new ZCodeLogLocator();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var resolved = _locator.ResolveDatabases(_configuredHome);
        var records = new List<UsageRecord>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = resolved.Databases.Count,
        };
        var problems = false;

        foreach (var database in resolved.Databases)
        {
            var rows = ReadDatabase(database, diagnostics, ref problems, ct);
            foreach (var row in rows)
            {
                if (!seenIds.Add(row.Id))
                {
                    diagnostics.DuplicateRecords++;
                    continue;
                }

                var record = ZCodeRecordMapper.Map(row);
                if (record is null)
                {
                    diagnostics.InvalidRecords++;
                    continue;
                }

                records.Add(record);
            }
        }

        // 稳定排序（同时间戳保持库内读取顺序，与蓝本 stable sort_by_key 一致）。
        var sorted = records.OrderBy(static record => record.Timestamp).ToList();
        diagnostics.MissingCostRecords = sorted.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = sorted.Count(static record => record.Model is null);
        diagnostics.Status = sorted.Count == 0
            ? JsonlScanStatus.NoData
            : problems || diagnostics.InvalidRecords > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;

        return Task.FromResult(new JsonlScanResult
        {
            Roots = resolved.Roots,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 打开单个库并读出 completed 行。打开失败 / schema 不支持 / 查询中断都记为问题
    /// （状态降级 Partial）并返回已读到的行，与蓝本"读失败即断、保留已读条目"的语义一致。
    /// </summary>
    private List<ZCodeUsageRow> ReadDatabase(
        string databasePath,
        JsonlScanDiagnostics diagnostics,
        ref bool problems,
        CancellationToken ct)
    {
        var rows = new List<ZCodeUsageRow>();
        try
        {
            using var connection = OpenReadOnly(databasePath);
            var schema = ZCodeSchemaInspector.Inspect(connection);
            if (!schema.IsSupported)
            {
                problems = true;
                return rows;
            }

            using var command = connection.CreateCommand();
            command.CommandText = BuildQuery(schema);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                // 行级读取失败（NULL 身份 / token 列类型异常）：与蓝本 read_usage_row 失败跳过该行、
                // 继续读取一致；查询级错误（ExecuteReader / Read 抛出）才中断本库。
                if (ReadRow(reader) is { } row)
                {
                    rows.Add(row);
                }
                else
                {
                    diagnostics.InvalidRecords++;
                }
            }

            diagnostics.FilesRead++;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            problems = true;
        }

        return rows;
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        return connection;
    }
    /// <summary>
    /// 查询语句与蓝本 loader.rs 完全同构：completed 过滤 + LEFT JOIN session 取项目目录，
    /// 可选列按 schema 拼接（缺失列以常量占位，保持列位固定）。
    /// </summary>
    internal static string BuildQuery(ZCodeSchemaInspector.Schema schema)
    {
        var cacheCreation = schema.HasCacheCreation ? "m.cache_creation_input_tokens" : "0";
        var cacheRead = schema.HasCacheRead ? "m.cache_read_input_tokens" : "0";
        var computedTotal = schema.HasComputedTotal
            ? "m.computed_total_tokens"
            : "m.input_tokens + m.output_tokens";
        var providerId = schema.HasProviderId ? "m.provider_id" : "NULL";
        return
            $"SELECT m.id, m.session_id, m.started_at, m.model_id, m.input_tokens, m.output_tokens, "
            + $"{cacheCreation}, {cacheRead}, {computedTotal}, {providerId}, s.directory "
            + $"FROM model_usage m LEFT JOIN session s ON s.id = m.session_id "
            + $"WHERE m.status = 'completed'";
    }

    /// <summary>
    /// 行读取；列序与 <see cref="BuildQuery"/> 固定对应。token 列出现 NULL / 负值 / 非整数
    /// 时返回 null（调用方记问题并停止本库），文本列 NULL 一律按可空处理。
    /// </summary>
    private static ZCodeUsageRow? ReadRow(SqliteDataReader reader)
    {
        var id = reader.IsDBNull(0) ? null : reader.GetString(0);
        var sessionId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var modelId = reader.IsDBNull(3) ? null : reader.GetString(3);
        if (id is null || sessionId is null || modelId is null)
        {
            return null;
        }

        if (!TryReadNonNegativeInt64(reader, 2, out var startedAt)
            || !TryReadNonNegativeInt64(reader, 4, out var inputTokens)
            || !TryReadNonNegativeInt64(reader, 5, out var outputTokens)
            || !TryReadNonNegativeInt64(reader, 6, out var cacheCreation)
            || !TryReadNonNegativeInt64(reader, 7, out var cacheRead)
            || !TryReadNonNegativeInt64(reader, 8, out var computedTotal))
        {
            return null;
        }

        return new ZCodeUsageRow(
            Id: id,
            SessionId: sessionId,
            StartedAt: startedAt,
            ModelId: modelId,
            ProviderId: reader.IsDBNull(9) ? null : reader.GetString(9),
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CacheCreationInputTokens: cacheCreation,
            CacheReadInputTokens: cacheRead,
            ComputedTotalTokens: computedTotal,
            Directory: reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static bool TryReadNonNegativeInt64(SqliteDataReader reader, int ordinal, out long value)
    {
        value = 0;
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        try
        {
            value = reader.GetInt64(ordinal);
            return value >= 0;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return false;
        }
    }
}
