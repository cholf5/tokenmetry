namespace Tokenmetry.Relay.Tests;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;

/// <summary>
/// 排行榜中继存储（leaderboard 计划 Task 2）：leaderboard_settings / leaderboard_rollup 两表 +
/// LeaderboardStore 的重算（grain 兜底 per (device, day)，L3）、清退、榜单查询与 token 轮换级联。
/// 直接对临时库 new RelayDatabase（不走 HTTP），在调用方事务内调 store（与 push/偏好/解绑事务内调用同形）。
/// </summary>
public sealed class LeaderboardTests
{
    private const string Day1 = "2026-06-19";
    private const string Day2 = "2026-06-20";

    [Fact]
    public async Task Recompute_MixedGrains_CountsFullOnly()
    {
        // 同 (device, day)：full 100 + day 70 + agent 30 → 只计 full（L3）。
        var (database, dbPath) = await CreateTempDatabaseAsync();
        try
        {
            await using var connection = database.OpenConnection();
            var accountId = await CreateAccountAsync(database, "sub-1", "alice");
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Full, 100, 1.00m);
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Day, 70);
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Agent, 30, agent: "claude");

            await using var transaction = await BeginAsync(connection);
            await LeaderboardStore.RecomputeDaysAsync(connection, transaction, accountId, new[] { Day1 });
            await transaction.CommitAsync();

            var rollup = await ReadRollupAsync(connection, accountId, Day1);
            rollup.Should().NotBeNull();
            rollup!.TotalTokens.Should().Be(100, "同 (device, day) 有 full 行时只计 full 粒度");
            rollup.CostUsd.Should().Be(1.00m, "成本只来自参与计数的行");
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    [Fact]
    public async Task Recompute_DayGrainFallback()
    {
        // 仅 day 行 → rollup = day 行值；agent/model/project 分解行存在不改变结果（它们不进总量）。
        var (database, dbPath) = await CreateTempDatabaseAsync();
        try
        {
            await using var connection = database.OpenConnection();
            var accountId = await CreateAccountAsync(database, "sub-1", "alice");
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Day, 70);
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Agent, 999, agent: "claude");
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Model, 888, model: "opus");
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Project, 777, project: "/tmp/p");

            await using var transaction = await BeginAsync(connection);
            await LeaderboardStore.RecomputeDaysAsync(connection, transaction, accountId, new[] { Day1 });
            await transaction.CommitAsync();

            var rollup = await ReadRollupAsync(connection, accountId, Day1);
            rollup.Should().NotBeNull();
            rollup!.TotalTokens.Should().Be(70, "无 full 行时兜底计 day 行，分解行不进总量");
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    [Fact]
    public async Task Recompute_PerDeviceIsolation()
    {
        // 兜底是 per (device, day) 而非 per day：devA 有 full 行（其 agent 行不进总量）、
        // devB 仅 day 行（该组内无 full → 落 day）→ 合计 = devA.full + devB.day。
        var (database, dbPath) = await CreateTempDatabaseAsync();
        try
        {
            await using var connection = database.OpenConnection();
            var accountId = await CreateAccountAsync(database, "sub-1", "alice");
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Full, 100, 1.00m);
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Agent, 500, agent: "claude");
            await InsertUsageRowAsync(connection, accountId, "devB", Day1, UsageFactGrain.Day, 70);

            await using var transaction = await BeginAsync(connection);
            await LeaderboardStore.RecomputeDaysAsync(connection, transaction, accountId, new[] { Day1 });
            await transaction.CommitAsync();

            var rollup = await ReadRollupAsync(connection, accountId, Day1);
            rollup.Should().NotBeNull();
            rollup!.TotalTokens.Should().Be(170, "devA 计 full 100、devB 兜底计 day 70；agent 行只属于 devA 组");
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    [Fact]
    public async Task Recompute_EmptyDayDeletesRow()
    {
        // 有行 → upsert；行删光后再重算同一 day → 该 (account, day) rollup 行被删除（§4.2 步骤 3）。
        var (database, dbPath) = await CreateTempDatabaseAsync();
        try
        {
            await using var connection = database.OpenConnection();
            var accountId = await CreateAccountAsync(database, "sub-1", "alice");
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Full, 100, 1.00m);

            await using (var transaction = await BeginAsync(connection))
            {
                await LeaderboardStore.RecomputeDaysAsync(connection, transaction, accountId, new[] { Day1 });
                await transaction.CommitAsync();
            }

            (await ReadRollupAsync(connection, accountId, Day1)).Should().NotBeNull("重算有行 day 应 upsert");

            await DeleteUsageRowsAsync(connection, accountId, day: Day1);
            await using (var transaction = await BeginAsync(connection))
            {
                await LeaderboardStore.RecomputeDaysAsync(connection, transaction, accountId, new[] { Day1 });
                await transaction.CommitAsync();
            }

            (await ReadRollupAsync(connection, accountId, Day1)).Should().BeNull("无行可算的 day 必须删除 rollup 行");
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    [Fact]
    public async Task CostSum_DecimalExact_And_NullPreserved()
    {
        // L4：成本求和在 C# decimal 做——1.10 + 2.20 精确 = 3.30（REAL 求和会得 3.3000001907… 被此断言抓住）；
        // 参与行全 NULL → rollup cost 保持 NULL（诚实缺省）。
        var (database, dbPath) = await CreateTempDatabaseAsync();
        try
        {
            await using var connection = database.OpenConnection();
            var accountId = await CreateAccountAsync(database, "sub-1", "alice");
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Full, 10, 1.10m, model: "model-a");
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Full, 20, 2.20m, model: "model-b");
            await InsertUsageRowAsync(connection, accountId, "devA", Day2, UsageFactGrain.Full, 5, null, model: "model-a");
            await InsertUsageRowAsync(connection, accountId, "devA", Day2, UsageFactGrain.Full, 6, null, model: "model-b");

            await using var transaction = await BeginAsync(connection);
            await LeaderboardStore.RecomputeDaysAsync(connection, transaction, accountId, new[] { Day1, Day2 });
            await transaction.CommitAsync();

            var withCost = await ReadRollupAsync(connection, accountId, Day1);
            withCost.Should().NotBeNull();
            withCost!.TotalTokens.Should().Be(30);
            withCost.CostUsd.Should().Be(3.30m, "decimal 求和必须精确");

            var withoutCost = await ReadRollupAsync(connection, accountId, Day2);
            withoutCost.Should().NotBeNull();
            withoutCost!.TotalTokens.Should().Be(11);
            withoutCost.CostUsd.Should().BeNull("全 NULL 成本保持 NULL");
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    [Fact]
    public async Task QueryBoard_Rank_Month_Participants()
    {
        var (database, dbPath) = await CreateTempDatabaseAsync();
        try
        {
            await using var connection = database.OpenConnection();
            var alice = await CreateAccountAsync(database, "sub-alice", "alice");
            var bob = await CreateAccountAsync(database, "sub-bob", "bob");
            var carol = await CreateAccountAsync(database, "sub-carol", "carol");

            await using (var transaction = await BeginAsync(connection))
            {
                // alice 用默认名（settings.display_name NULL → 回落 accounts.display_name，L8）；
                // bob 自定义名；carol 有 rollup 行但未 opt-in（反目标：未 opt-in 数字不得出现）。
                await LeaderboardStore.UpsertSettingsAsync(connection, transaction, alice, optIn: true, displayName: null);
                await LeaderboardStore.UpsertSettingsAsync(connection, transaction, bob, optIn: true, displayName: "Bobby");
                await transaction.CommitAsync();
            }

            await InsertRollupRowAsync(connection, alice, "2026-11-01", 100, 1.00m);
            await InsertRollupRowAsync(connection, alice, "2026-11-02", 200, 2.00m);
            await InsertRollupRowAsync(connection, bob, "2026-11-01", 150, 1.50m);
            await InsertRollupRowAsync(connection, bob, "2026-12-05", 999, 9.99m); // 次月行
            await InsertRollupRowAsync(connection, carol, "2026-11-03", 500, 5.00m); // 未 opt-in

            var tokens = await LeaderboardStore.QueryBoardAsync(connection, "tokens", "2026-11", limit: 100);
            tokens.Month.Should().Be("2026-11");
            tokens.Board.Should().Be("tokens");
            tokens.Participants.Should().Be(2, "participants = 有 rollup 行且 opt-in 的账号数（carol 未 opt-in 不计）");
            tokens.Entries.Should().HaveCount(2);
            tokens.Entries[0].Rank.Should().Be(1);
            tokens.Entries[0].DisplayName.Should().Be("alice", "settings NULL 回落 accounts.display_name");
            tokens.Entries[0].AvatarUrl.Should().NotBeNull();
            tokens.Entries[0].TotalTokens.Should().Be(300, "多日求和");
            tokens.Entries[1].Rank.Should().Be(2);
            tokens.Entries[1].DisplayName.Should().Be("Bobby", "自定义展示名优先（L8）");
            tokens.Entries[1].TotalTokens.Should().Be(150, "2026-12 行不计入 2026-11 窗口");
            tokens.Entries.Select(entry => entry.DisplayName).Should().NotContain("carol", "未 opt-in 账号不得出现在榜单");

            var cost = await LeaderboardStore.QueryBoardAsync(connection, "cost", "2026-11", limit: 100);
            cost.Board.Should().Be("cost");
            cost.Participants.Should().Be(2);
            cost.Entries[0].DisplayName.Should().Be("alice", "cost 榜按金额降序");
            cost.Entries[0].TotalCostUsd.Should().Be(3.00m);
            cost.Entries[1].TotalCostUsd.Should().Be(1.50m);
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    [Fact]
    public async Task QueryBoard_Tie_BreaksByDisplayNameOrdinal()
    {
        // 并列按 DisplayName 字典序（L7 确定性）："Bob"(0x42) < "alice"(0x61) 按 ordinal；
        // 文化敏感排序会给出相反顺序，此断言钉死 ordinal。
        var (database, dbPath) = await CreateTempDatabaseAsync();
        try
        {
            await using var connection = database.OpenConnection();
            var alice = await CreateAccountAsync(database, "sub-alice", "alice");
            var bob = await CreateAccountAsync(database, "sub-bob", "bob");

            await using (var transaction = await BeginAsync(connection))
            {
                await LeaderboardStore.UpsertSettingsAsync(connection, transaction, alice, optIn: true, displayName: "alice");
                await LeaderboardStore.UpsertSettingsAsync(connection, transaction, bob, optIn: true, displayName: "Bob");
                await transaction.CommitAsync();
            }

            await InsertRollupRowAsync(connection, alice, "2026-11-01", 100, 1.00m);
            await InsertRollupRowAsync(connection, bob, "2026-11-01", 100, 1.00m);

            var board = await LeaderboardStore.QueryBoardAsync(connection, "tokens", "2026-11", limit: 100);
            board.Entries.Should().HaveCount(2);
            board.Entries[0].DisplayName.Should().Be("Bob", "并列按 ordinal：'B'(0x42) < 'a'(0x61)");
            board.Entries[0].Rank.Should().Be(1);
            board.Entries[1].DisplayName.Should().Be("alice");
            board.Entries[1].Rank.Should().Be(2);
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    [Fact]
    public async Task RebuildAllDays_RebuildsFromCurrentRows_AndDropsStaleDays()
    {
        // 全量重建（opt-in=true / 设备解绑触发）：devB 的 usage 行删除后，其独占 day2 的 rollup 必须消失，
        // 未删的 day1 保留，新 day3 进入——一次性反映当前 usage_rows 全貌。
        var (database, dbPath) = await CreateTempDatabaseAsync();
        try
        {
            await using var connection = database.OpenConnection();
            var accountId = await CreateAccountAsync(database, "sub-1", "alice");
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Full, 100, 1.00m);
            await InsertUsageRowAsync(connection, accountId, "devB", Day2, UsageFactGrain.Day, 70);

            await using (var transaction = await BeginAsync(connection))
            {
                await LeaderboardStore.RecomputeDaysAsync(connection, transaction, accountId, new[] { Day1, Day2 });
                await transaction.CommitAsync();
            }

            (await ReadRollupAsync(connection, accountId, Day2)).Should().NotBeNull();

            // 模拟设备解绑级联：devB 的 usage_rows 已删；devA 新增 day3 行。
            await DeleteUsageRowsAsync(connection, accountId, device: "devB");
            await InsertUsageRowAsync(connection, accountId, "devA", "2026-06-21", UsageFactGrain.Day, 50);

            await using (var transaction = await BeginAsync(connection))
            {
                await LeaderboardStore.RebuildAllDaysAsync(connection, transaction, accountId);
                await transaction.CommitAsync();
            }

            var day1 = await ReadRollupAsync(connection, accountId, Day1);
            day1.Should().NotBeNull();
            day1!.TotalTokens.Should().Be(100);

            var day3 = await ReadRollupAsync(connection, accountId, "2026-06-21");
            day3.Should().NotBeNull();
            day3!.TotalTokens.Should().Be(50);

            (await ReadRollupAsync(connection, accountId, Day2)).Should().BeNull("只由已消失设备构成的历史 day 随重建消失");
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    [Fact]
    public async Task UpsertSettings_ReadBack()
    {
        var (database, dbPath) = await CreateTempDatabaseAsync();
        try
        {
            await using var connection = database.OpenConnection();
            var accountId = await CreateAccountAsync(database, "sub-1", "alice");

            (await LeaderboardStore.ReadSettingsAsync(connection, accountId)).Should().BeNull("未设置过 → 无行");
            await using (var transaction = await BeginAsync(connection))
            {
                (await LeaderboardStore.IsOptedInAsync(connection, transaction, accountId)).Should().BeFalse("无 settings 行 = 未 opt-in");
                await transaction.CommitAsync();
            }

            await using (var transaction = await BeginAsync(connection))
            {
                await LeaderboardStore.UpsertSettingsAsync(connection, transaction, accountId, optIn: true, displayName: "Alice");
                await transaction.CommitAsync();
            }

            var first = await LeaderboardStore.ReadSettingsAsync(connection, accountId);
            first.Should().NotBeNull();
            first!.OptIn.Should().BeTrue();
            first.DisplayName.Should().Be("Alice");
            await using (var transaction = await BeginAsync(connection))
            {
                (await LeaderboardStore.IsOptedInAsync(connection, transaction, accountId)).Should().BeTrue();
                await transaction.CommitAsync();
            }

            // 二次 upsert 走覆盖更新路径；清空自定义名 = NULL（用默认名）；opt_in=false 存行（清退由 ClearAsync 负责）。
            await using (var transaction = await BeginAsync(connection))
            {
                await LeaderboardStore.UpsertSettingsAsync(connection, transaction, accountId, optIn: false, displayName: null);
                await transaction.CommitAsync();
            }

            var second = await LeaderboardStore.ReadSettingsAsync(connection, accountId);
            second.Should().NotBeNull();
            second!.OptIn.Should().BeFalse();
            second.DisplayName.Should().BeNull();
            await using (var transaction = await BeginAsync(connection))
            {
                (await LeaderboardStore.IsOptedInAsync(connection, transaction, accountId)).Should().BeFalse();
                await transaction.CommitAsync();
            }
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    [Fact]
    public async Task Clear_RemovesSettingsAndRollup()
    {
        // 关闭 opt-in = 删 settings 行 + 删该账号全部 rollup（§4.2「关闭即删」）——榜单即时消失。
        var (database, dbPath) = await CreateTempDatabaseAsync();
        try
        {
            await using var connection = database.OpenConnection();
            var accountId = await CreateAccountAsync(database, "sub-1", "alice");
            await InsertUsageRowAsync(connection, accountId, "devA", Day1, UsageFactGrain.Full, 100, 1.00m);

            await using (var transaction = await BeginAsync(connection))
            {
                await LeaderboardStore.UpsertSettingsAsync(connection, transaction, accountId, optIn: true, displayName: "Alice");
                await LeaderboardStore.RecomputeDaysAsync(connection, transaction, accountId, new[] { Day1 });
                await transaction.CommitAsync();
            }

            (await RollupCountAsync(connection, accountId)).Should().Be(1);

            await using (var transaction = await BeginAsync(connection))
            {
                await LeaderboardStore.ClearAsync(connection, transaction, accountId);
                await transaction.CommitAsync();
            }

            (await LeaderboardStore.ReadSettingsAsync(connection, accountId)).Should().BeNull();
            (await RollupCountAsync(connection, accountId)).Should().Be(0, "清退必须连 rollup 一并删除");
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    [Fact]
    public async Task BootstrapRotation_RetiresLeaderboardRows()
    {
        // 换 token = 重签（终审 I4）：退役级联清单含两张 leaderboard 表——否则旧账号设置与 rollup 变孤儿。
        var dbPath = NewDbPath();
        var database = new RelayDatabase(dbPath);
        try
        {
            await database.EnsureCreatedAsync();
            await RelayDatabase.BootstrapSharedAccountAsync(database, "old-token");

            var oldAccountId = await SharedAccountIdAsync(database);
            await using (var connection = database.OpenConnection())
            {
                await using var transaction = await BeginAsync(connection);
                await LeaderboardStore.UpsertSettingsAsync(connection, transaction, oldAccountId, optIn: true, displayName: "self");
                await InsertRollupRowAsync(connection, oldAccountId, Day1, 100, 1.00m);
                await transaction.CommitAsync();
            }

            await RelayDatabase.BootstrapSharedAccountAsync(database, "new-token");

            await using var after = database.OpenConnection();
            (await LeaderboardStore.ReadSettingsAsync(after, oldAccountId)).Should().BeNull("退役账号的 settings 必须级联清除");
            (await RollupCountAsync(after, oldAccountId)).Should().Be(0, "退役账号的 rollup 必须级联清除");
        }
        finally
        {
            TryDeletePath(dbPath);
        }
    }

    // ---- 测试脚手架：临时库 + 直灌 SQL（与 store 同库同事务形态，不走 HTTP）----

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"tokenmetry-leaderboard-{Guid.NewGuid():N}.db");

    private static async Task<(RelayDatabase Database, string DbPath)> CreateTempDatabaseAsync()
    {
        var dbPath = NewDbPath();
        var database = new RelayDatabase(dbPath);
        await database.EnsureCreatedAsync();
        return (database, dbPath);
    }

    private static void TryDeletePath(string dbPath)
    {
        try
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
        catch (IOException)
        {
            // 句柄晚释放：留到临时目录自清理（同 RelayFactory 约定）。
        }
    }

    private static async Task<SqliteTransaction> BeginAsync(SqliteConnection connection) =>
        (SqliteTransaction)await connection.BeginTransactionAsync();

    private static async Task<string> CreateAccountAsync(RelayDatabase database, string githubSub, string login)
    {
        await database.UpsertGitHubAccountAsync(githubSub, login, $"https://avatars.githubusercontent.com/{githubSub}", $"hash-{githubSub}");
        await using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM accounts WHERE github_sub = $sub";
        cmd.Parameters.AddWithValue("$sub", githubSub);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> SharedAccountIdAsync(RelayDatabase database)
    {
        await using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM accounts WHERE kind = 'shared'";
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static long _seq;

    private static async Task InsertUsageRowAsync(
        SqliteConnection connection,
        string accountId,
        string device,
        string day,
        string grain,
        long totalTokens,
        decimal? costUsd = null,
        string agent = "",
        string model = "",
        string project = "")
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO usage_rows (
                account_id, device_id, day, grain, agent, model, provider, project, project_display_name,
                input_tokens, output_tokens, cache_creation_tokens,
                cache_creation_ephemeral_5m_tokens, cache_creation_ephemeral_1h_tokens,
                cache_read_tokens, total_tokens, cost_usd, updated_at, seq)
            VALUES (
                $accountId, $deviceId, $day, $grain, $agent, $model, '', $project, '',
                0, 0, 0, 0, 0, 0, $totalTokens, $costUsd, $updatedAt, $seq)
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$deviceId", device);
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$grain", grain);
        cmd.Parameters.AddWithValue("$agent", agent);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$project", project);
        cmd.Parameters.AddWithValue("$totalTokens", totalTokens);
        cmd.Parameters.AddWithValue("$costUsd", costUsd.HasValue ? costUsd.Value.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$updatedAt", "2026-06-19T10:00:00.0000000Z");
        cmd.Parameters.AddWithValue("$seq", ++_seq);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertRollupRowAsync(
        SqliteConnection connection, string accountId, string day, long totalTokens, decimal? costUsd)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_rollup (account_id, day, total_tokens, cost_usd, updated_at)
            VALUES ($accountId, $day, $totalTokens, $costUsd, $updatedAt)
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$day", day);
        cmd.Parameters.AddWithValue("$totalTokens", totalTokens);
        cmd.Parameters.AddWithValue("$costUsd", costUsd.HasValue ? costUsd.Value.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$updatedAt", "2026-06-19T10:00:00.0000000Z");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DeleteUsageRowsAsync(SqliteConnection connection, string accountId, string? day = null, string? device = null)
    {
        var conditions = new List<string> { "account_id = $accountId" };
        if (day is not null)
        {
            conditions.Add("day = $day");
        }

        if (device is not null)
        {
            conditions.Add("device_id = $device");
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM usage_rows WHERE {string.Join(" AND ", conditions)}";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        if (day is not null)
        {
            cmd.Parameters.AddWithValue("$day", day);
        }

        if (device is not null)
        {
            cmd.Parameters.AddWithValue("$device", device);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record RollupRow(long TotalTokens, decimal? CostUsd);

    private static async Task<RollupRow?> ReadRollupAsync(SqliteConnection connection, string accountId, string day)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT total_tokens, cost_usd FROM leaderboard_rollup WHERE account_id = $accountId AND day = $day";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$day", day);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new RollupRow(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
    }

    private static async Task<int> RollupCountAsync(SqliteConnection connection, string accountId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM leaderboard_rollup WHERE account_id = $accountId";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }
}
