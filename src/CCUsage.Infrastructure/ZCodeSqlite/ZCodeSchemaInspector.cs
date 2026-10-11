using CCUsage.Core.Models;
using Microsoft.Data.Sqlite;

namespace CCUsage.Infrastructure.ZCodeSqlite;

/// <summary>
/// ZCode 账本 schema 检查（蓝本 zcode/src/loader.rs read_schema）：
/// - model_usage 必需列：id, session_id, started_at, model_id, status, input_tokens, output_tokens；
/// - session 必需列：id, directory；
/// - 可选列按存在性回填查询（cache_creation / cache_read / computed_total / provider_id / session.version），
///   computed_total 缺失时回退 input + output；
/// - 不满足必需列的库整体跳过（蓝本：unrelated or incomplete schemas are skipped）。
/// </summary>
public static class ZCodeSchemaInspector
{
    public static readonly IReadOnlyList<string> RequiredModelColumns = new[]
    {
        "id", "session_id", "started_at", "model_id", "status", "input_tokens", "output_tokens",
    };

    public static readonly IReadOnlyList<string> RequiredSessionColumns = new[] { "id", "directory" };

    /// <summary>检查结果；<see cref="IsSupported"/> 为 false 表示必需列缺失，库应整体跳过。</summary>
    public sealed record Schema(
        bool IsSupported,
        bool HasCacheCreation,
        bool HasCacheRead,
        bool HasComputedTotal,
        bool HasProviderId,
        bool HasSessionVersion);

    public static Schema Inspect(SqliteConnection connection)
    {
        var modelColumns = ReadTableColumns(connection, "model_usage");
        var sessionColumns = ReadTableColumns(connection, "session");

        var supported = ContainsAll(modelColumns, RequiredModelColumns)
                        && ContainsAll(sessionColumns, RequiredSessionColumns);
        return new Schema(
            IsSupported: supported,
            HasCacheCreation: modelColumns.Contains("cache_creation_input_tokens"),
            HasCacheRead: modelColumns.Contains("cache_read_input_tokens"),
            HasComputedTotal: modelColumns.Contains("computed_total_tokens"),
            HasProviderId: modelColumns.Contains("provider_id"),
            HasSessionVersion: sessionColumns.Contains("version"));
    }

    private static HashSet<string> ReadTableColumns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        // PRAGMA table_info 行结构：(cid, name, type, notnull, dflt_value, pk)
        var nameOrdinal = reader.GetOrdinal("name");
        while (reader.Read())
        {
            if (!reader.IsDBNull(nameOrdinal))
            {
                columns.Add(reader.GetString(nameOrdinal));
            }
        }

        return columns;
    }

    private static bool ContainsAll(HashSet<string> columns, IReadOnlyList<string> required)
    {
        foreach (var column in required)
        {
            if (!columns.Contains(column))
            {
                return false;
            }
        }

        return true;
    }
}
