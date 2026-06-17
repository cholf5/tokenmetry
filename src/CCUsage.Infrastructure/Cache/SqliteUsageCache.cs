using Microsoft.Data.Sqlite;
using System.Text.Json;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.Cache;

public class SqliteUsageCache : IUsageCache
{
    private readonly string _dbPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public SqliteUsageCache(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);

        const string createDashboardTableSql = """
            CREATE TABLE IF NOT EXISTS dashboard_cache (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;

        const string createDailyRowsTableSql = """
            CREATE TABLE IF NOT EXISTS daily_rows_cache (
                date TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = createDashboardTableSql;
        await cmd.ExecuteNonQueryAsync(ct);

        cmd.CommandText = createDailyRowsTableSql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveDashboardAsync(DashboardSummary summary, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(summary, _jsonOptions);
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO dashboard_cache (id, json)
            VALUES (1, $json)
            """;
        cmd.Parameters.AddWithValue("$json", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DashboardSummary?> GetLatestDashboardAsync(CancellationToken ct = default)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM dashboard_cache WHERE id = 1 LIMIT 1";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var json = reader.GetString(0);
        return JsonSerializer.Deserialize<DashboardSummary>(json, _jsonOptions);
    }

    public async Task SaveDailyRowsAsync(List<DailyUsageSummary> rows, CancellationToken ct = default)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        foreach (var row in rows)
        {
            var json = JsonSerializer.Serialize(row, _jsonOptions);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO daily_rows_cache (date, json)
                VALUES ($date, $json)
                """;
            cmd.Parameters.AddWithValue("$date", row.Date);
            cmd.Parameters.AddWithValue("$json", json);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        transaction.Commit();
    }

    public async Task<List<DailyUsageSummary>> GetDailyRowsAsync(CancellationToken ct = default)
    {
        var result = new List<DailyUsageSummary>();
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM daily_rows_cache ORDER BY date DESC";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(0);
            var row = JsonSerializer.Deserialize<DailyUsageSummary>(json, _jsonOptions);
            if (row != null)
            {
                result.Add(row);
            }
        }

        return result;
    }
}
