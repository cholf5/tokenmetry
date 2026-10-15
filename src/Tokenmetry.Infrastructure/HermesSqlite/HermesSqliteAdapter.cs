using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.HermesSqlite;
using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.SqliteSource;
using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.HermesSqlite;

/// <summary>
/// Hermes 本地 state SQLite 直读适配器（I-016，移植蓝本
/// ../ccusage/rust/adapters/hermes/src/loader.rs / paths.rs；蓝本许可：MIT License，
/// Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// 库定位（<see cref="HermesLogLocator"/>：HERMES_HOME 多根 / 默认 ~/.hermes）→ 逐库只读打开
/// （共享 <see cref="SqliteReadonly"/>）→ 固定列查询 sessions（model 非空白行）→
/// 行映射（<see cref="HermesSessionParser"/>）→ 按 message id（"hermes:&lt;session_id&gt;"，与 session id
/// 一一对应）跨库去重（先扫的库优先）→ 按时间稳定排序。
/// state 库随使用持续变化，mtime 增量不适用，每次全量重读；只读打开兼容 Hermes 运行中的 WAL。
/// 诊断口径沿用 <see cref="JsonlScanDiagnostics"/>（ZCodeSqlite 口径映射）：FilesDiscovered = 解析出的库数、
/// FilesRead = 打开且完成查询的库数、LinesSkipped = 五桶与 cost 全零的会话行、MalformedLines 恒 0、
/// InvalidRecords = 身份列不可读 / 时间戳不可用或越界（蓝本静默跳过的行显式计数）、
/// DuplicateRecords = 跨库 session 折叠；状态 Ok/Partial/NoData 同 I-006。
/// 实例无扫描状态，线程安全；以单例接入 App DI。
/// </summary>
public class HermesSqliteAdapter : IHermesSqliteAdapter
{
    /// <summary>
    /// 查询与蓝本 loader.rs 同构：固定列序（id, model, billing_provider, started_at, message_count,
    /// input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,
    /// estimated_cost_usd, actual_cost_usd），model 为空白的行在 SQL 层排除（蓝本同式）。
    /// </summary>
    internal const string SessionQuery =
        """
        SELECT
            id,
            model,
            billing_provider,
            started_at,
            message_count,
            input_tokens,
            output_tokens,
            cache_read_tokens,
            cache_write_tokens,
            reasoning_tokens,
            estimated_cost_usd,
            actual_cost_usd
        FROM sessions
        WHERE model IS NOT NULL
            AND TRIM(model) != ''
        """;

    private readonly string? _configuredHome;
    private readonly HermesLogLocator _locator;

    public HermesSqliteAdapter(string? configuredHome = null, HermesLogLocator? locator = null)
    {
        _configuredHome = configuredHome;
        _locator = locator ?? new HermesLogLocator();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var databases = _locator.ResolveDatabases(_configuredHome);
        var records = new List<UsageRecord>();
        var seenSessions = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = databases.Count,
        };
        var problems = false;

        foreach (var database in databases)
        {
            var (rows, invalidIdentityRows, completed) = ReadDatabase(database, ref problems, ct);
            if (completed)
            {
                diagnostics.FilesRead++;
            }

            foreach (var row in rows)
            {
                var record = HermesSessionParser.Map(row, out var rejection);
                switch (rejection)
                {
                    case HermesRowRejection.NoUsage:
                        diagnostics.LinesSkipped++;
                        continue;
                    case HermesRowRejection.InvalidTimestamp
                        or HermesRowRejection.TimestampOutOfRange:
                        diagnostics.InvalidRecords++;
                        continue;
                }

                // 蓝本按 session id 跨库去重（先扫的库保留）；message id 为 "hermes:<session>" 双射同键。
                if (!seenSessions.Add(record!.MessageId!))
                {
                    diagnostics.DuplicateRecords++;
                    continue;
                }

                records.Add(record!);
            }

            // 身份列不可读的行（蓝本静默 continue）显式计数。
            diagnostics.InvalidRecords += invalidIdentityRows;
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
            Roots = databases,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 打开单个库并读出会话行；打开 / prepare / 查询失败按"该库 0 条 + 问题标记"处理
    /// （蓝本返回空列表同语义）；身份列不可读的行计数返回供调用方显式计入 InvalidRecords。
    /// </summary>
    private (List<HermesSessionParser.SessionRow> Rows, int InvalidIdentityRows, bool Completed) ReadDatabase(
        string databasePath,
        ref bool problems,
        CancellationToken ct)
    {
        var rows = new List<HermesSessionParser.SessionRow>();
        var invalidIdentityRows = 0;
        try
        {
            using var connection = SqliteReadonly.Open(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = SessionQuery;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (ReadRow(reader) is { } row)
                {
                    rows.Add(row);
                }
                else
                {
                    invalidIdentityRows++;
                }
            }

            return (rows, invalidIdentityRows, Completed: true);
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            problems = true;
        }

        return (rows, invalidIdentityRows, Completed: false);
    }

    /// <summary>
    /// 行读取；列序与 <see cref="SessionQuery"/> 固定对应。身份列（id / model）不可读或空白时返回 null
    /// （蓝本 read::&lt;String&gt;.ok()? + 空白检查；model 空白已被 SQL 过滤，此处为防御）；
    /// token / message_count 列按蓝本 read_u64 容错（i64 max(0) → 有限正 f64 截断 → 0），
    /// started_at / cost 列按 read_f64 容错（f64 优先 → i64 兜底，非有限按缺失）。
    /// </summary>
    private static HermesSessionParser.SessionRow? ReadRow(SqliteDataReader reader)
    {
        if (TryReadText(reader, 0) is not { } sessionId
            || TryReadText(reader, 1) is not { } model)
        {
            return null;
        }

        var trimmedModel = model.Trim();
        if (sessionId.Length == 0 || trimmedModel.Length == 0)
        {
            return null;
        }

        return new HermesSessionParser.SessionRow(
            SessionId: sessionId,
            Model: trimmedModel,
            ProviderRaw: TryReadText(reader, 2),
            StartedAt: ReadF64(reader, 3),
            MessageCount: ReadU64(reader, 4),
            InputTokens: ReadU64(reader, 5),
            OutputTokens: ReadU64(reader, 6),
            CacheReadTokens: ReadU64(reader, 7),
            CacheCreationTokens: ReadU64(reader, 8),
            ReasoningTokens: ReadU64(reader, 9),
            EstimatedCost: ReadNonNegativeF64(reader, 10),
            ActualCost: ReadNonNegativeF64(reader, 11));
    }

    /// <summary>文本列：NULL 或类型异常视为不可读（返回 null）。</summary>
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

    /// <summary>蓝本 read_u64：i64 max(0) → 有限正 f64 截断 → 其余 0（永不失败、行保留）。</summary>
    private static long ReadU64(SqliteDataReader reader, int ordinal)
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

                return value >= 9223372036854775808.0
                    ? long.MaxValue
                    : Math.Max(0, (long)Math.Truncate(value));
            }
            catch (Exception fallbackException) when (fallbackException is FormatException or InvalidCastException)
            {
                return 0;
            }
        }
    }

    /// <summary>蓝本 read_f64：f64 优先（非有限按缺失）→ i64 兜底 → 其余缺失。</summary>
    private static double? ReadF64(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            var value = reader.GetDouble(ordinal);
            return double.IsFinite(value) ? value : null;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            try
            {
                return reader.GetInt64(ordinal);
            }
            catch (Exception fallbackException) when (fallbackException is FormatException or InvalidCastException)
            {
                return null;
            }
        }
    }

    /// <summary>蓝本 read_non_negative_f64：read_f64 后 max(0)。</summary>
    private static double? ReadNonNegativeF64(SqliteDataReader reader, int ordinal)
        => ReadF64(reader, ordinal) is { } value ? Math.Max(0, value) : null;
}
