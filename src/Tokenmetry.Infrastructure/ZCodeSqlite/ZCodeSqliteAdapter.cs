using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.SqliteSource;
using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.ZCodeSqlite;

/// <summary>
/// ZCode SQLite 账本直读适配器（I-006，蓝本 ../ccusage/rust/adapters/zcode/）：
/// 库定位（<see cref="ZCodeLogLocator"/>）→ 逐库探针（completed 行 count + max(id)）→ 账面未动
/// 直接复用上轮记录（零读取零映射）→ 否则逐库只读打开 → schema 检查（<see cref="ZCodeSchemaInspector"/>，
/// 按可选列拼查询）→ 读 completed 行 → 行映射（<see cref="ZCodeRecordMapper"/>）→ 按 usage id 去重 →
/// 按时间排序 → 统一记录 + 诊断计数。
/// 账本是随使用持续变化的单一库文件，mtime 增量不适用；以 I-042 轻量探针 + 零变更轮结果记忆化
/// （与 <see cref="Infrastructure.Jsonl.IncrementalJsonlScanner"/> 的零变更缓存同构）避免每轮全表
/// 重读重映射：探针快照 = 上轮<b>成功读取</b>时各库的 (count, max(id))，本轮逐库探针与库集完全一致
/// 才复用；探针失败 / 库集变化（新增、消失）→ 保守全量读取。已知边界：行原地改值（token 修正且
/// count 与 max(id) 不动）不会被发现——usage 行实务上只增不改（账本写入方语义）。
/// 只读模式打开且不改 journal mode，兼容 ZCode 运行中的 WAL。
/// 读取层的类型容错与蓝本 read_token_column / read_timestamp_ms 一致：token 列 i64 钳 0 →
/// f64 兜底（有限正数四舍五入）→ 其余按 0，行永保留；身份列读取失败才整行静默跳过；
/// provider / directory 列类型异常按"缺失"处理保留行。去重只对映射成功的记录生效
/// （与蓝本"仅对成功条目按 message id 去重"一致）。
/// 诊断字段沿用 <see cref="JsonlScanDiagnostics"/> 的最近口径：FilesDiscovered = 发现的库数、
/// FilesRead = 成功打开且 schema 支持并完成查询的库数（零变更轮为 0）、InvalidRecords =
/// 语义校验拒绝的行数（身份无效 / 时间无效 / 全零，经 <see cref="ZCodeRecordMapper.Map"/>）、
/// DuplicateRecords = 按 id 折叠的重复记录；行级 malformed 概念不存在（Malformed/LinesSkipped 恒 0）。
/// 实例持有探针与记录记忆（单例接入 App DI），并发由 LocalIngestionDataSource 串行化。
/// </summary>
public class ZCodeSqliteAdapter : IZCodeSqliteAdapter
{
    /// <summary>严格小于 2^63 的 double 才可安全 cast 到 long（2^63 本身可被 double 精确表示，cast 回绕）。</summary>
    private const double MaxRepresentableDoubleForLong = 9223372036854775808.0;

    private readonly string? _configuredHome;
    private readonly ZCodeLogLocator _locator;

    // I-042 延伸（探针前置 + 零变更轮结果记忆化）：_lastCompletedProbe = 上轮成功读取时逐库
    // 探针快照（库路径 → completed 行 count + max(id)）；_lastRecords / _lastDiagnostics = 上轮
    // 成功读取的记录集与累计诊断。账本库无 mtime 增量语义（单文件持续变化），以轻量聚合查询
    // 判断"自上轮成功读取以来账面是否动过"——库集与逐库探针完全一致才复用上轮记录（零读取
    // 零映射）。已知边界：行原地改值（token 修正且 count 与 max(id) 不动）不会被发现——usage 行
    // 实务上只增不改（账本写入方语义），且任何新行/删除/库集变化都会触发全量。
    private Dictionary<string, (int Count, string MaxId)> _lastCompletedProbe = new(StringComparer.Ordinal);
    private IReadOnlyList<UsageRecord>? _lastRecords;
    private JsonlScanDiagnostics? _lastDiagnostics;

    public ZCodeSqliteAdapter(string? configuredHome = null, ZCodeLogLocator? locator = null)
    {
        _configuredHome = configuredHome;
        _locator = locator ?? new ZCodeLogLocator();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var resolved = _locator.ResolveDatabases(_configuredHome);
        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = resolved.Databases.Count,
        };

        // 探针前置：逐库轻量聚合查询（打不开 / 表缺失 / 查询失败按未探到，保守有变化）。
        var probes = new Dictionary<string, (int Count, string MaxId)>(StringComparer.Ordinal);
        foreach (var database in resolved.Databases)
        {
            if (TryProbe(database) is { } probe)
            {
                probes[database] = probe;
            }
        }

        // 零变更轮（库集与逐库探针与上轮成功读取完全一致）：复用上轮记录（同实例列表，消费方
        // 视为只读），零读取零映射；FilesChanged=0 驱动采集管道短路。本轮诊断只反映"未读取"
        // （FilesRead=0、无解析事件），累计口径（Status / Invalid / Duplicate / Missing / Unknown）
        // 沿用上轮——与"重建后必然得到的结果"一致（IncrementalJsonlScanner 零变更缓存同款）。
        if (_lastRecords is { } cachedRecords && _lastDiagnostics is { } lastDiagnostics && ProbesUnchanged(probes))
        {
            return Task.FromResult(new JsonlScanResult
            {
                Roots = resolved.Roots,
                Records = cachedRecords,
                Diagnostics = new JsonlScanDiagnostics
                {
                    Status = lastDiagnostics.Status,
                    FilesDiscovered = diagnostics.FilesDiscovered,
                    FilesRead = 0,
                    InvalidRecords = lastDiagnostics.InvalidRecords,
                    DuplicateRecords = lastDiagnostics.DuplicateRecords,
                    MissingCostRecords = lastDiagnostics.MissingCostRecords,
                    UnknownModelRecords = lastDiagnostics.UnknownModelRecords,
                    FilesChanged = 0,
                },
            });
        }

        var records = new List<UsageRecord>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var problems = false;
        // 记忆只收"本轮成功读取且探针成功"的库：读取失败的库不进记忆，下一轮库集比对必然
        // 判为变化、重新读取（保守自愈，与探针后置时代"只在成功读取后落账"一致）。
        var readMemory = new Dictionary<string, (int Count, string MaxId)>(StringComparer.Ordinal);
        foreach (var database in resolved.Databases)
        {
            var rows = ReadDatabase(database, diagnostics, ref problems, ct, out var readSucceeded);
            if (readSucceeded && probes.TryGetValue(database, out var probe))
            {
                readMemory[database] = probe;
            }

            foreach (var row in rows)
            {
                var record = ZCodeRecordMapper.Map(row);
                if (record is null)
                {
                    diagnostics.InvalidRecords++;
                    continue;
                }

                // 去重发生在映射成功之后（蓝本先 row_to_entry 再按 id 去重）：
                // 同 id 的无效首现行不占用去重名额，另一库的有效行得以保留。
                // MessageId 取自已通过身份校验的 row.Id（Map 保证非 null），此处非空断言仅为可空标注。
                if (!seenIds.Add(record.MessageId!))
                {
                    diagnostics.DuplicateRecords++;
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
        // FilesChanged：走到全量读取即本轮账面有变化或探针失败保守处理（有库填 1）；
        // 无库不设值，由管道按"本轮发现即空"规则处理（与既有口径一致）。
        diagnostics.FilesChanged = resolved.Databases.Count > 0 ? 1 : null;

        _lastCompletedProbe = readMemory;
        _lastRecords = sorted;
        _lastDiagnostics = diagnostics;

        return Task.FromResult(new JsonlScanResult
        {
            Roots = resolved.Roots,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 打开单个库并读出 completed 行。打开失败 / schema 不支持 / 查询中断都记为问题
    /// （状态降级 Partial）并返回已读到的行，与蓝本"读失败即断、保留已读条目"的语义一致；
    /// 行级读取失败（身份列 NULL / 类型异常）按蓝本静默跳过该行、继续读取。
    /// <paramref name="readSucceeded"/> = 打开、schema 检查与查询全部成功——只有成功读取的库
    /// 才允许进入探针记忆（失败的库下一轮必须重新读取）。
    /// </summary>
    private List<ZCodeUsageRow> ReadDatabase(
        string databasePath,
        JsonlScanDiagnostics diagnostics,
        ref bool problems,
        CancellationToken ct,
        out bool readSucceeded)
    {
        var rows = new List<ZCodeUsageRow>();
        readSucceeded = false;
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
                if (ReadRow(reader) is { } row)
                {
                    rows.Add(row);
                }
            }

            diagnostics.FilesRead++;
            readSucceeded = true;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            problems = true;
        }

        return rows;
    }

    /// <summary>
    /// I-042 探针（前置）：completed 行 count + max(id)。打不开 / 表缺失 / 查询失败返回 null
    /// （调用方保守按"有变化"全量读取）——探针是优化信号，绝不影响扫描结果的正确性。
    /// </summary>
    private static (int Count, string MaxId)? TryProbe(string databasePath)
    {
        try
        {
            using var connection = SqliteReadonly.Open(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), COALESCE(MAX(id), '') FROM model_usage WHERE status = 'completed'";
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (reader.GetInt32(0), reader.GetString(1))
                : null;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            return null;
        }
    }

    /// <summary>零变更判定：本轮探针的库集与逐库指纹与上轮成功读取的记忆完全一致
    /// （含库集收缩——消失的库不进本轮探针，集合比对必然判为变化）。</summary>
    private bool ProbesUnchanged(Dictionary<string, (int Count, string MaxId)> probes)
    {
        if (probes.Count != _lastCompletedProbe.Count)
        {
            return false;
        }

        foreach (var (databasePath, probe) in probes)
        {
            if (!_lastCompletedProbe.TryGetValue(databasePath, out var known) || known != probe)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>只读打开（共享原语 <see cref="SqliteReadonly"/>，I-009 起与 OpenCode 适配器共用）。</summary>
    private static SqliteConnection OpenReadOnly(string databasePath) => SqliteReadonly.Open(databasePath);

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
    /// 行读取；列序与 <see cref="BuildQuery"/> 固定对应。身份列（id / session_id / model_id）
    /// 为 NULL 或类型异常时返回 null（静默跳过，蓝本 read::&lt;String&gt;.ok()? 语义）；
    /// token / 时间列按蓝本容错转换永不失败；文本元数据列（provider / directory）类型异常按缺失处理。
    /// </summary>
    private static ZCodeUsageRow? ReadRow(SqliteDataReader reader)
    {
        var id = TryReadText(reader, 0);
        var sessionId = TryReadText(reader, 1);
        var modelId = TryReadText(reader, 3);
        if (id is null || sessionId is null || modelId is null)
        {
            return null;
        }

        if (!TryReadTimestampMs(reader, 2, out var startedAt))
        {
            return null;
        }

        return new ZCodeUsageRow(
            Id: id,
            SessionId: sessionId,
            StartedAt: startedAt,
            ModelId: modelId,
            ProviderId: TryReadOptionalText(reader, 9),
            InputTokens: ReadTokenColumn(reader, 4),
            OutputTokens: ReadTokenColumn(reader, 5),
            CacheCreationInputTokens: ReadTokenColumn(reader, 6),
            CacheReadInputTokens: ReadTokenColumn(reader, 7),
            ComputedTotalTokens: ReadTokenColumn(reader, 8),
            Directory: TryReadOptionalText(reader, 10));
    }

    /// <summary>身份列：NULL 或类型异常视为整行跳过。</summary>
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

    /// <summary>元数据列：NULL 或类型异常按"缺失"处理，行保留（蓝本 read::&lt;Option&lt;String&gt;&gt;.ok().flatten()）。</summary>
    private static string? TryReadOptionalText(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : TryReadText(reader, ordinal);
    }

    /// <summary>
    /// token 列（蓝本 read_token_column）：i64 负值钳 0 → f64 有限正数四舍五入（钳 long 上限）→
    /// 其余（NULL / 非数值文本）按 0，永不抛出、行保留。
    /// </summary>
    private static long ReadTokenColumn(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        try
        {
            return Math.Max(0, reader.GetInt64(ordinal));
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            try
            {
                var value = reader.GetDouble(ordinal);
                if (!double.IsFinite(value) || value <= 0)
                {
                    return 0;
                }

                // double 域钳到 long 可表示范围（2^63 恰为 double 可精确表示值，直接 cast 会溢出回绕）。
                if (value >= MaxRepresentableDoubleForLong)
                {
                    return long.MaxValue;
                }

                var rounded = (long)Math.Round(value);
                return rounded < 0 ? 0 : rounded;
            }
            catch (Exception fallbackException) when (fallbackException is FormatException or InvalidCastException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// 时间列（蓝本 read_timestamp_ms）：i64 直读 → f64 有限且在 i64 域内四舍五入 → 其余失败
    /// （返回 false，整行跳过）。
    /// </summary>
    private static bool TryReadTimestampMs(SqliteDataReader reader, int ordinal, out long value)
    {
        value = 0;
        if (!reader.IsDBNull(ordinal))
        {
            try
            {
                value = reader.GetInt64(ordinal);
                return true;
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException)
            {
                try
                {
                    var doubleValue = reader.GetDouble(ordinal);
                    if (double.IsFinite(doubleValue)
                        && doubleValue >= long.MinValue
                        && doubleValue < MaxRepresentableDoubleForLong)
                    {
                        value = (long)Math.Round(doubleValue);
                        return true;
                    }
                }
                catch (Exception fallbackException) when (fallbackException is FormatException or InvalidCastException)
                {
                    // 落入最外层 return false：类型完全不可读，整行跳过。
                }
            }
        }

        return false;
    }
}
