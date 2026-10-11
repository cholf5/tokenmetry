using Microsoft.Data.Sqlite;

namespace CCUsage.Infrastructure.SqliteSource;

/// <summary>
/// SQLite 账本只读连接的共享打开入口（I-006 ZCode / I-009 OpenCode 等 SQLite 直读适配器共用，
/// 从 I-006 ZCodeSqliteAdapter.OpenReadOnly 泛化）：
/// Mode=ReadOnly 打开，不改 journal mode——写端处于 WAL 时只读连接可读已提交数据
/// （I-006 单测与真实对账双验证）；打开失败由调用方按"该库本轮跳过"处理。
/// </summary>
public static class SqliteReadonly
{
    public static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        return connection;
    }
}
