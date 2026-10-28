using System.Globalization;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.GooseSqlite;
using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.JsonSource;
using Tokenmetry.Infrastructure.SqliteSource;
using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.GooseSqlite;

/// <summary>
/// Goose 本地会话 SQLite 直读适配器（I-012，移植蓝本 ../ccusage/rust/adapters/goose/src/loader.rs；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// 库定位（<see cref="GooseLogLocator"/>，双默认根 + GOOSE_PATH_ROOT 覆盖）→ 文件戳探针
/// （<see cref="FileStampMemo"/>，主库 + WAL 边车的 mtime/大小——WAL 库写入先落边车，主库 mtime 在
/// checkpoint 前不变）→ 账面未动复用上轮记录（零读取零映射）→ 否则逐库只读打开
/// （共享 <see cref="SqliteReadonly"/>）→ 固定列查询 sessions（model_config_json 非空行）→
/// 行映射（<see cref="GooseSessionMapper"/>，accumulated_* 优先、reasoning 并入 output）→
/// 按 (库路径, session id) 去重（蓝本键形状，canonicalize 后同库双候选折叠）→ 按时间排序。
/// 会话库随使用持续变化，mtime 增量不适用；以 I-042 文件戳记忆化替代每轮全量重读（与共享扫描器的
/// 零变更缓存同口径）。记忆化准入：全部发现库都成功读取才落账（FilesRead == 库数），有库打不开的
/// 轮次清空记忆下一轮重试（见 <see cref="FileStampMemo"/>）；库消失（定位器不再解析出）时指纹集合
/// 随之变化，不复用陈旧记录。只读打开兼容 Goose 运行中的 WAL。
/// 诊断口径沿用 <see cref="JsonlScanDiagnostics"/>（ZCodeSqlite 口径映射）：FilesDiscovered = 解析出的库数、
/// FilesRead = 打开并完成查询的库数（零变更轮为 0）、LinesSkipped = tokens 全零的会话行、
/// MalformedLines 恒 0（无 JSONL 行）、InvalidRecords = 身份/时间/模型配置不可用或时间戳越界的行、
/// DuplicateRecords = (库, session id) 折叠。蓝本单库读失败（打开/prepare/查询异常）按"该库本轮 0 条"
/// 处理不中断其它库（状态降级 Partial）。实例持有扫描记忆，非线程安全；以单例接入 App DI
/// （并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class GooseSqliteAdapter : IGooseSqliteAdapter
{
    /// <summary>
    /// 查询与蓝本 GOOSE_SESSION_QUERY 同构：固定列序（id, model_config_json, provider_name, created_at,
    /// total_tokens, input_tokens, output_tokens, accumulated_total_tokens, accumulated_input_tokens,
    /// accumulated_output_tokens），model_config_json 为空白的行在 SQL 层排除（蓝本同式）。
    /// </summary>
    internal const string SessionQuery =
        """
        SELECT
            id,
            model_config_json,
            provider_name,
            created_at,
            total_tokens,
            input_tokens,
            output_tokens,
            accumulated_total_tokens,
            accumulated_input_tokens,
            accumulated_output_tokens
        FROM sessions
        WHERE model_config_json IS NOT NULL
            AND TRIM(model_config_json) != ''
        """;

    private readonly string? _configuredRoot;
    private readonly GooseLogLocator _locator;
    private readonly FileStampMemo _memo = new();

    public GooseSqliteAdapter(string? configuredRoot = null, GooseLogLocator? locator = null)
    {
        _configuredRoot = configuredRoot;
        _locator = locator ?? new GooseLogLocator();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var databases = _locator.ResolveDatabases(_configuredRoot);
        var stamps = FileStampMemo.CaptureWithOptional(FileStampMemo.SqliteFingerprintFiles(databases));

        if (_memo.TryReuse(stamps, databases.Count, databases, out var reused))
        {
            return Task.FromResult(reused);
        }

        var records = new List<UsageRecord>();
        var seenSessionKeys = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = databases.Count,
        };
        var problems = false;

        foreach (var database in databases)
        {
            foreach (var row in ReadDatabase(database, diagnostics, ref problems, ct))
            {
                var record = GooseSessionMapper.Map(row, out var rejection);
                switch (rejection)
                {
                    case GooseRowRejection.NoUsage:
                        diagnostics.LinesSkipped++;
                        continue;
                    case GooseRowRejection.UnparsableTimestamp
                        or GooseRowRejection.UnparsableModelConfig
                        or GooseRowRejection.TimestampOutOfRange:
                        diagnostics.InvalidRecords++;
                        continue;
                }

                // 去重键与蓝本一致（db_path:session_id）；映射成功之后去重（I-006 口径）。
                if (!seenSessionKeys.Add($"{database}:{record!.MessageId}"))
                {
                    diagnostics.DuplicateRecords++;
                    continue;
                }

                records.Add(record!);
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
        // FilesChanged：走到全量读取即本轮账面有变化（有库填 1）；无库不设值，
        // 由管道按"本轮发现即空"规则处理（与 ZCode 探针同口径）。
        diagnostics.FilesChanged = databases.Count > 0 ? 1 : null;

        // 记忆化准入：全部发现库都成功读取才落账（有库打不开 → null 戳清空记忆，下轮重试）。
        _memo.Update(diagnostics.FilesRead == databases.Count ? stamps : null, sorted, diagnostics);

        return Task.FromResult(new JsonlScanResult
        {
            Roots = databases,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 打开单个库并读出会话行；打开 / prepare / 查询失败按"该库 0 条 + 问题标记"处理
    /// （蓝本 unwrap_or_else(error → 空列表) + 状态降级口径），行级读取失败静默跳过并计数。
    /// </summary>
    private List<GooseSessionMapper.SessionRow> ReadDatabase(
        string databasePath,
        JsonlScanDiagnostics diagnostics,
        ref bool problems,
        CancellationToken ct)
    {
        var rows = new List<GooseSessionMapper.SessionRow>();
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
            }

            diagnostics.FilesRead++;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            problems = true;
        }

        return rows;
    }

    /// <summary>
    /// 行读取；列序与 <see cref="SessionQuery"/> 固定对应。身份列（id / model_config_json）与时间列
    /// 不可读时返回 null（蓝本 read::&lt;String&gt;.ok()? 静默跳过，调用方计 InvalidRecords）；
    /// token 列按蓝本 read_token_value（i64 &gt; 0 才生效，accumulated_* 优先）解析。
    /// </summary>
    private static GooseSessionMapper.SessionRow? ReadRow(SqliteDataReader reader)
    {
        if (TryReadText(reader, 0) is not { } id
            || TryReadText(reader, 1) is not { } modelConfigJson
            || TryReadTimestampText(reader, 3) is not { } createdAtRaw)
        {
            return null;
        }

        return new GooseSessionMapper.SessionRow(
            Id: id,
            ModelConfigJson: modelConfigJson,
            ProviderName: TryReadText(reader, 2),
            CreatedAtRaw: createdAtRaw,
            // accumulated_*（列 7-9）优先，回退 total/input/output（列 4-6）。
            InputTokens: ReadTokenValue(reader, 8) ?? ReadTokenValue(reader, 5) ?? 0,
            OutputTokens: ReadTokenValue(reader, 9) ?? ReadTokenValue(reader, 6) ?? 0,
            TotalTokens: ReadTokenValue(reader, 7) ?? ReadTokenValue(reader, 4) ?? 0);
    }

    /// <summary>身份/文本列：NULL 或类型异常视为不可读（返回 null，整行跳过）。</summary>
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

    /// <summary>created_at（蓝本 read_timestamp_value）：文本优先，否则整数转文本，其余不可读。</summary>
    private static string? TryReadTimestampText(SqliteDataReader reader, int ordinal)
    {
        if (TryReadText(reader, ordinal) is { } text)
        {
            return text;
        }

        try
        {
            return reader.GetInt64(ordinal).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>token 列（蓝本 read_token_value）：仅正 i64 生效，NULL / 零 / 负值 / 类型异常 → null（走回退列）。</summary>
    private static long? ReadTokenValue(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            var value = reader.GetInt64(ordinal);
            return value > 0 ? value : null;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return null;
        }
    }
}
