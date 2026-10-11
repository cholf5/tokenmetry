using CCUsage.Infrastructure.SqliteSource;
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
        bool HasProviderId);

    public static Schema Inspect(SqliteConnection connection)
    {
        var modelColumns = ReadTableColumns(connection, "model_usage");
        var sessionColumns = ReadTableColumns(connection, "session");

        var supported = SqliteTableSchema.ContainsAll(modelColumns, RequiredModelColumns)
                        && SqliteTableSchema.ContainsAll(sessionColumns, RequiredSessionColumns);
        return new Schema(
            IsSupported: supported,
            HasCacheCreation: modelColumns.Contains("cache_creation_input_tokens"),
            HasCacheRead: modelColumns.Contains("cache_read_input_tokens"),
            HasComputedTotal: modelColumns.Contains("computed_total_tokens"),
            HasProviderId: modelColumns.Contains("provider_id"));
    }

    private static HashSet<string> ReadTableColumns(SqliteConnection connection, string table)
        => SqliteTableSchema.ReadColumns(connection, table);
}
