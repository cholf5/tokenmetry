using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.SqliteSource;

/// <summary>
/// SQLite 直读适配器共享的 schema 检查原语（从 I-006 ZCodeSchemaInspector 泛化，I-009 起共用）：
/// - <see cref="TableExists"/>：sqlite_master 按表名存在性（蓝本 loader.rs table_exists 同式）；
/// - <see cref="ReadColumns"/>：PRAGMA table_info 读列名集合（蓝本 table_columns 同口径）；
/// - <see cref="ContainsAll"/>：必需列全集判断。
/// 各适配器的"必需/可选列清单与不支持时的处置"仍由各自 schema 检查器/加载器声明——
/// ZCode 是"必需列缺失整体跳过"，OpenCode 是"按表存在性 + 可选列拼查询、缺列静默降级"，
/// 语义不同故只共享原语，不做统一策略。
/// </summary>
public static class SqliteTableSchema
{
    public static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table LIMIT 1;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    public static HashSet<string> ReadColumns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        // PRAGMA table_info 行结构：(cid, name, type, notnull, dflt_value, pk)。
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

    public static bool ContainsAll(HashSet<string> columns, IReadOnlyList<string> required)
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
