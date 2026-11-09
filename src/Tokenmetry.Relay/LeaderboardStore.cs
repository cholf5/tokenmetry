using System.Globalization;
using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

namespace Tokenmetry.Relay;

/// <summary>leaderboard_settings 行的存储投影：DisplayName = 自定义展示名（NULL = 用 accounts 默认名）。</summary>
public sealed record LeaderboardPrefRow(bool OptIn, string? DisplayName);

/// <summary>
/// 排行榜存储（leaderboard 设计 §4）：leaderboard_settings / leaderboard_rollup 两表的全部榜单 SQL 收口于此。
/// 重算复刻客户端合并视图的 grain 兜底口径（L3）：per (device, day) 组内存在 full 行只计 full，否则只计
/// day 行，agent/model/project 分解行不进总量——与 UsageFactsMapper.BuildDailySummary 的 per-device-day
/// 版本一致。成本求和一律 C# decimal（L4）：cost_usd 是 TEXT decimal，SQLite REAL 求和会引浮点误差。
/// 无后台任务：重算只发生在调用方（push / 偏好 / 解绑）的既有写事务内（设计 §4.2）。
/// </summary>
internal static class LeaderboardStore
{
    private const string CostBoard = "cost";

    public static async Task<bool> IsOptedInAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, CancellationToken ct = default)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT opt_in FROM leaderboard_settings WHERE account_id = $accountId";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long optIn && optIn != 0;
    }

    public static async Task UpsertSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        bool optIn,
        string? displayName,
        CancellationToken ct = default)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO leaderboard_settings (account_id, opt_in, display_name, updated_at)
            VALUES ($accountId, $optIn, $displayName, $now)
            ON CONFLICT(account_id) DO UPDATE SET
                opt_in = excluded.opt_in,
                display_name = excluded.display_name,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$optIn", optIn ? 1L : 0L);
        cmd.Parameters.AddWithValue("$displayName", (object?)displayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<LeaderboardPrefRow?> ReadSettingsAsync(SqliteConnection connection, string accountId, CancellationToken ct = default)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT opt_in, display_name FROM leaderboard_settings WHERE account_id = $accountId";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new LeaderboardPrefRow(reader.GetInt64(0) != 0, reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>
    /// 增量重算（设计 §4.2：push 事务内按 batch 涉及的去重 day 集合调用）：逐 day 取 usage_rows 按
    /// (device, day) 走 grain 兜底聚合；无可算行 → 删该 (account, day) rollup 行，有 → upsert（updated_at 刷新）。
    /// </summary>
    public static async Task RecomputeDaysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        IReadOnlyCollection<string> days,
        CancellationToken ct = default)
    {
        foreach (var day in days)
        {
            var rows = await ReadGrainRowsAsync(connection, transaction, accountId, day, ct);
            var totals = AggregateByDay(rows);
            if (totals.TryGetValue(day, out var total))
            {
                await UpsertRollupAsync(connection, transaction, accountId, day, total.Tokens, total.CostUsd, ct);
            }
            else
            {
                // 无行可算（该 day 无 usage 行，或仅有不进总量的 agent/model/project 分解行）→ 删 rollup 行（§4.2 步骤 3）。
                await DeleteRollupAsync(connection, transaction, accountId, day, ct);
            }
        }
    }

    /// <summary>
    /// 全量重建（设计 §4.2：opt-in=true 与设备解绑触发）：一次拉该账号全部 usage_rows 聚合，先清空该账号
    /// rollup 再重插——只由已解绑/已消失设备构成的历史 day 自动随之消失（解绑后不重算就虚高）。
    /// </summary>
    public static async Task RebuildAllDaysAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, CancellationToken ct = default)
    {
        var rows = await ReadGrainRowsAsync(connection, transaction, accountId, day: null, ct);
        var totals = AggregateByDay(rows);

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM leaderboard_rollup WHERE account_id = $accountId";
            clear.Parameters.AddWithValue("$accountId", accountId);
            await clear.ExecuteNonQueryAsync(ct);
        }

        foreach (var (day, total) in totals)
        {
            await UpsertRollupAsync(connection, transaction, accountId, day, total.Tokens, total.CostUsd, ct);
        }
    }

    /// <summary>关闭 opt-in = 删 settings 行 + 删该账号全部 rollup 行（设计 §4.2「关闭即删」、§4.1「删行即退出」）。</summary>
    public static async Task ClearAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, CancellationToken ct = default)
    {
        using (var settings = connection.CreateCommand())
        {
            settings.Transaction = transaction;
            settings.CommandText = "DELETE FROM leaderboard_settings WHERE account_id = $accountId";
            settings.Parameters.AddWithValue("$accountId", accountId);
            await settings.ExecuteNonQueryAsync(ct);
        }

        using (var rollup = connection.CreateCommand())
        {
            rollup.Transaction = transaction;
            rollup.CommandText = "DELETE FROM leaderboard_rollup WHERE account_id = $accountId";
            rollup.Parameters.AddWithValue("$accountId", accountId);
            await rollup.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// 公开榜单读（设计 §4.3）：rollup 按月窗口 INNER JOIN settings(opt_in=1) 与 accounts（退役/删号账号的
    /// 残留行不得计入），取回按账号分组的行后在 C# 求和与排名（L4）；board=cost 按金额降序否则 tokens 降序，
    /// 并列按展示名 ordinal；rank = 行号；participants = 满足同一 JOIN 条件的账号数（不受 limit 截断）。
    /// </summary>
    public static async Task<LeaderboardResponse> QueryBoardAsync(SqliteConnection connection, string board, string month, int limit, CancellationToken ct = default)
    {
        var monthStart = DateOnly.ParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture);
        var windowStart = monthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var windowEnd = monthStart.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var accounts = new Dictionary<string, AccountTotal>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT r.account_id, r.total_tokens, r.cost_usd, s.display_name, a.display_name, a.avatar_url
                FROM leaderboard_rollup r
                JOIN leaderboard_settings s ON s.account_id = r.account_id AND s.opt_in = 1
                JOIN accounts a ON a.id = r.account_id
                WHERE r.day >= $windowStart AND r.day < $windowEnd
                """;
            cmd.Parameters.AddWithValue("$windowStart", windowStart);
            cmd.Parameters.AddWithValue("$windowEnd", windowEnd);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var accountId = reader.GetString(0);
                if (!accounts.TryGetValue(accountId, out var account))
                {
                    var custom = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var fallback = reader.IsDBNull(4) ? null : reader.GetString(4);
                    account = new AccountTotal(ResolveDisplayName(custom, fallback), reader.IsDBNull(5) ? null : reader.GetString(5));
                    accounts[accountId] = account;
                }

                account.Tokens += reader.GetInt64(1);
                if (!reader.IsDBNull(2))
                {
                    account.CostUsd = (account.CostUsd ?? 0m) + decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
                }
            }
        }

        var ranked = board == CostBoard
            ? accounts.Values.OrderByDescending(account => account.CostUsd ?? 0m).ThenBy(account => account.DisplayName, StringComparer.Ordinal)
            : accounts.Values.OrderByDescending(account => account.Tokens).ThenBy(account => account.DisplayName, StringComparer.Ordinal);

        var entries = new List<LeaderboardEntry>();
        var rank = 1;
        foreach (var account in ranked.Take(limit))
        {
            entries.Add(new LeaderboardEntry(rank++, account.DisplayName, account.AvatarUrl, account.Tokens, account.CostUsd ?? 0m));
        }

        return new LeaderboardResponse(month, board, accounts.Count, entries);
    }

    /// <summary>usage_rows 重算投影（§4.2 步骤 1 的查询；day=null 时取全账号供全量重建）。</summary>
    private static async Task<List<GrainRow>> ReadGrainRowsAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, string? day, CancellationToken ct)
    {
        var rows = new List<GrainRow>();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        if (day is null)
        {
            cmd.CommandText = """
                SELECT day, device_id, grain, total_tokens, cost_usd
                FROM usage_rows
                WHERE account_id = $accountId
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT day, device_id, grain, total_tokens, cost_usd
                FROM usage_rows
                WHERE account_id = $accountId AND day = $day
                """;
            cmd.Parameters.AddWithValue("$day", day);
        }

        cmd.Parameters.AddWithValue("$accountId", accountId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new GrainRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>
    /// L3 兜底口径（与 UsageFactsMapper.BuildDailySummary 的 per-device-day 版本一致）：
    /// (day, device) 组内存在 full 行 → 只聚合 full 行；否则只聚合 day 行；agent/model/project 行不进总量。
    /// 返回值只含有可算行（full 或 day）的 day——纯分解行的 day 不产生 rollup 条目。
    /// </summary>
    private static Dictionary<string, DayTotal> AggregateByDay(IEnumerable<GrainRow> rows)
    {
        var totals = new Dictionary<string, DayTotal>(StringComparer.Ordinal);
        foreach (var deviceDay in rows.GroupBy(row => (row.Day, row.DeviceId)))
        {
            var eligible = deviceDay.Any(row => row.Grain == UsageFactGrain.Full)
                ? deviceDay.Where(row => row.Grain == UsageFactGrain.Full)
                : deviceDay.Where(row => row.Grain == UsageFactGrain.Day);

            long tokens = 0;
            decimal? cost = null;
            var hasEligibleRow = false;
            foreach (var row in eligible)
            {
                hasEligibleRow = true;
                tokens += row.TotalTokens;
                if (row.CostUsd is { } costText)
                {
                    cost = (cost ?? 0m) + decimal.Parse(costText, CultureInfo.InvariantCulture);
                }
            }

            if (!hasEligibleRow)
            {
                continue;
            }

            if (!totals.TryGetValue(deviceDay.Key.Day, out var total))
            {
                total = new DayTotal();
                totals[deviceDay.Key.Day] = total;
            }

            total.Tokens += tokens;
            if (cost is { } value)
            {
                total.CostUsd = (total.CostUsd ?? 0m) + value;
            }
        }

        return totals;
    }

    // L8 展示名回落链：settings.display_name → accounts.display_name → "anonymous"
    // （PUT 侧已把空串归一为 NULL，这里对空串兜底防御）。
    private static string ResolveDisplayName(string? custom, string? fallback) =>
        !string.IsNullOrEmpty(custom) ? custom
        : !string.IsNullOrEmpty(fallback) ? fallback
        : "anonymous";

    private static async Task UpsertRollupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountId,
        string day,
        long totalTokens,
        decimal? costUsd,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO leaderboard_rollup (account_id, day, total_tokens, cost_usd, updated_at)
            VALUES ($accountId, $day, $totalTokens, $costUsd, $now)
            ON CONFLICT(account_id, day) DO UPDATE SET
                total_tokens = excluded.total_tokens,
                cost_usd = excluded.cost_usd,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$totalTokens", totalTokens);
        cmd.Parameters.AddWithValue("$costUsd", costUsd.HasValue ? costUsd.Value.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteRollupAsync(SqliteConnection connection, SqliteTransaction transaction, string accountId, string day, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM leaderboard_rollup WHERE account_id = $accountId AND day = $day";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$day", day);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>usage_rows 重算投影行（只取重算需要的列；CostUsd 保持 TEXT 原样，求和在 C# decimal，L4）。</summary>
    private sealed record GrainRow(string Day, string DeviceId, string Grain, long TotalTokens, string? CostUsd);

    /// <summary>单 day 的聚合结果；CostUsd = 非 NULL 项 decimal 和（全 NULL → NULL，§4.2 步骤 3）。</summary>
    private sealed class DayTotal
    {
        public long Tokens { get; set; }

        public decimal? CostUsd { get; set; }
    }

    /// <summary>榜单单账号聚合（读路径 C# 求和，L4）；CostUsd 全 NULL = 账号当月无成本数据。</summary>
    private sealed class AccountTotal(string displayName, string? avatarUrl)
    {
        public string DisplayName { get; } = displayName;

        public string? AvatarUrl { get; } = avatarUrl;

        public long Tokens { get; set; }

        public decimal? CostUsd { get; set; }
    }
}
