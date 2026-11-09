namespace Tokenmetry.Relay.Tests;

using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

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

/// <summary>
/// 排行榜 HTTP 层（leaderboard 计划 Task 3）：公开读（匿名 + CORS）、偏好读写（Bearer）、
/// push / 解绑 / 删号 / 换 token 四个触发点的端到端语义。走 RelayTestSync 助手；每个用例独立中继实例。
/// </summary>
public sealed class LeaderboardHttpTests
{
    // 固定月窗口：榜单 GET 一律显式 month=2026-11（默认取当前 UTC 月，固定数据跨月会假红）。
    private const string Month = "2026-11";
    private const string Day1 = "2026-11-01";
    private const string Day5 = "2026-11-05";

    private static readonly DateTimeOffset T1 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 11, 5, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Board_Anonymous_WithCorsHeader()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var anonymous = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/leaderboard");
        request.Headers.Add("Origin", "https://tokenmetry.example");
        using var board = await anonymous.SendAsync(request);
        board.StatusCode.Should().Be(HttpStatusCode.OK, "公开榜单读匿名可达（中间件豁免 /api/leaderboard 前缀）");
        board.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue("公开端点必须带 CORS 头（AllowAnyOrigin）");
        var body = await board.Content.ReadFromJsonAsync(SyncJsonContext.Default.LeaderboardResponse);
        body!.Participants.Should().Be(0, "空库空榜");

        // 401 响应必须不带 CORS 头（设计 §6「CORS 头只在公开端点」测试项）：即使请求带 Origin，
        // 未挂 RequireCors 元数据的端点直通、不加任何头——豁免只限公开段前缀。
        using var preferenceRequest = new HttpRequestMessage(HttpMethod.Get, "/api/me/leaderboard");
        preferenceRequest.Headers.Add("Origin", "https://tokenmetry.example");
        using var preference = await anonymous.SendAsync(preferenceRequest);
        preference.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "豁免只限公开段前缀，偏好端点仍走 Bearer");
        preference.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            "CORS 头只在公开端点：401 的偏好端点即使带 Origin 也不得回 Access-Control-Allow-Origin");
    }

    [Fact]
    public async Task Preference_Roundtrip_AndFallback()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);

        var initial = await GetPreferenceAsync(client);
        initial.OptIn.Should().BeFalse("未设置过 → 未 opt-in");
        initial.DisplayName.Should().Be("self-hosted", "生效名回落 shared 账号默认名（L8 链）");

        (await PutPreferenceAsync(client, optIn: true, "Alice")).EnsureSuccessStatusCode();
        var optedIn = await GetPreferenceAsync(client);
        optedIn.OptIn.Should().BeTrue();
        optedIn.DisplayName.Should().Be("Alice", "PUT 与 GET 同形状往返");

        // 空串 = NULL（用默认名）→ 生效名回落 accounts 默认，而不是 "anonymous"（shared 账号有默认名）。
        (await PutPreferenceAsync(client, optIn: true, string.Empty)).EnsureSuccessStatusCode();
        var fallback = await GetPreferenceAsync(client);
        fallback.OptIn.Should().BeTrue();
        fallback.DisplayName.Should().Be("self-hosted", "空串归一为 NULL 后回落账号默认名");

        // opt-out = 关闭即删（settings 行删）→ optIn=false，生效名仍回落账号默认名。
        (await PutPreferenceAsync(client, optIn: false, null)).EnsureSuccessStatusCode();
        var optedOut = await GetPreferenceAsync(client);
        optedOut.OptIn.Should().BeFalse();
        optedOut.DisplayName.Should().Be("self-hosted");
    }

    [Fact]
    public async Task Preference_NameTooLong_400()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);

        (await PutPreferenceAsync(client, optIn: true, new string('字', 65)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "displayName trim 后 >64 字符必须 400");

        (await PutPreferenceAsync(client, optIn: true, new string('字', 64)))
            .StatusCode.Should().Be(HttpStatusCode.OK, "64 字符恰好在界内");

        var state = await GetPreferenceAsync(client);
        state.OptIn.Should().BeTrue();
        state.DisplayName.Should().Be(new string('字', 64), "65 字符的非法 PUT 不得产生任何落库");
    }

    [Fact]
    public async Task Board_InvalidParams_400()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/api/leaderboard?board=xyz"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "board 只有 cost|tokens");
        (await anonymous.GetAsync("/api/leaderboard?month=2026-13"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "月份越界（yyyy-MM TryParseExact 拒收）");
        (await anonymous.GetAsync("/api/leaderboard?month=abc"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "月份格式非法");
        (await anonymous.GetAsync("/api/leaderboard?limit=nope"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "limit 非整数");
    }

    [Fact]
    public async Task Board_MaxMonth_Overflow_400()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var anonymous = factory.CreateClient();

        // 9999-12 格式合法但月窗口端点（monthStart.AddMonths(1)）越出 DateOnly 可表示范围——
        // 必须在校验边界 400，而不是查询里 ArgumentOutOfRangeException 变 500（匿名公开端点）。
        (await anonymous.GetAsync("/api/leaderboard?month=9999-12"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "窗口端点不可表示的月份在校验边界拒收为 400（不许 500）");
    }

    [Fact]
    public async Task Board_LimitClamped()
    {
        var dbPath = NewDbPath();
        await using var factory = new RelayFactory(token: "abc", dbPath: dbPath);
        using var anonymous = factory.CreateClient(); // 首请求拉起中继主机（EnsureCreatedAsync 建表）
        await SeedBoardAccountsAsync(dbPath, 105);

        var floor = await GetBoardAsync(anonymous, $"/api/leaderboard?board=tokens&month={Month}&limit=0");
        floor.Entries.Should().HaveCount(1, "limit 钳到下界 1");
        floor.Participants.Should().Be(105, "participants 不受 limit 影响");

        var ceiling = await GetBoardAsync(anonymous, $"/api/leaderboard?board=tokens&month={Month}&limit=150");
        ceiling.Entries.Should().HaveCount(100, "limit 钳到上界 100");
        ceiling.Participants.Should().Be(105);
        ceiling.Entries.Select(entry => entry.Rank).Should().Equal(Enumerable.Range(1, 100), "rank = 行号");
    }

    [Fact]
    public async Task Push_RecomputesOnlyWhenOptedIn()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);
        using var anonymous = factory.CreateClient();

        // 未 opt-in：push 落库但不重算 → 榜单 0 participants。
        (await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", Day5, 100, 1.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var before = await BoardAsync(anonymous);
        before.Participants.Should().Be(0);

        // PUT opt-in=true → 全量重建拾回 push 历史。
        (await PutPreferenceAsync(client, optIn: true, "Alice")).EnsureSuccessStatusCode();
        var afterOptIn = await BoardAsync(anonymous);
        afterOptIn.Participants.Should().Be(1);
        afterOptIn.Entries.Should().ContainSingle().Which.TotalTokens.Should().Be(100);

        // push 当日新行（不同 model = 不同 PK）→ 增量重算即时反映到榜单。
        var sameDayExtra = RelayTestSync.UsageRow("devA", Day5, 50, 0.50m, T2);
        sameDayExtra.Model = "model-b";
        (await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact> { sameDayExtra },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var afterPush = await BoardAsync(anonymous);
        afterPush.Entries.Should().ContainSingle().Which.TotalTokens.Should().Be(150, "push 事务内按 day 增量重算");

        // push 历史 day 行 → batch 去重 day 集合同样拾取。
        (await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", Day1, 200, 2.00m, T2) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var afterHistory = await BoardAsync(anonymous);
        afterHistory.Entries.Should().ContainSingle().Which.TotalTokens.Should().Be(350, "历史 day 行同样拾取");

        // session-only batch 不触发重算（session 行不进总量）。
        (await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact>(),
            new List<SessionFact>
            {
                new() { Device = "devA", SessionId = "s-a", TotalTokens = 999, FirstSeenAt = T2, LastSeenAt = T2, UpdatedAt = T2 },
            }))).EnsureSuccessStatusCode();

        var afterSessionOnly = await BoardAsync(anonymous);
        afterSessionOnly.Entries.Should().ContainSingle().Which.TotalTokens.Should().Be(350, "session 行不进总量、不触发重算");
    }

    [Fact]
    public async Task Board_ExcludesOptedOut_AndOrphans()
    {
        var dbPath = NewDbPath();
        await using var factory = new RelayFactory(token: "abc", dbPath: dbPath);
        using var client = RelayTestSync.Client(factory);
        using var anonymous = factory.CreateClient();

        (await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", Day5, 100, 1.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await PutPreferenceAsync(client, optIn: true, "Alice")).EnsureSuccessStatusCode();

        var optedIn = await BoardAsync(anonymous);
        optedIn.Participants.Should().Be(1);
        optedIn.Entries.Should().ContainSingle().Which.DisplayName.Should().Be("Alice");

        // opt-out → 关闭即删（settings + rollup 全清）→ entries/participants 归零。
        (await PutPreferenceAsync(client, optIn: false, null)).EnsureSuccessStatusCode();
        var optedOut = await BoardAsync(anonymous);
        optedOut.Participants.Should().Be(0);
        optedOut.Entries.Should().BeEmpty();

        // 换 token 退役旧 shared 账号（Task 2 级联已清 settings/rollup）→ 榜单保持空。
        string retiredAccountId;
        await using (var connection = await OpenDbAsync(dbPath))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id FROM accounts WHERE kind = 'shared'";
            retiredAccountId = (string)(await cmd.ExecuteScalarAsync())!;
        }

        await RelayDatabase.BootstrapSharedAccountAsync(new RelayDatabase(dbPath), "new-token");
        var afterRotation = await BoardAsync(anonymous);
        afterRotation.Participants.Should().Be(0, "退役级联已清退旧账号榜单行");

        // 钉死 accounts JOIN 排除（Review Focus 2）：即使级联遗漏残留 settings + rollup（孤儿行），
        // 已不在 accounts 的退役账号也不得计入榜单。
        await using (var connection = await OpenDbAsync(dbPath))
        {
            using var settings = connection.CreateCommand();
            settings.CommandText = """
                INSERT INTO leaderboard_settings (account_id, opt_in, display_name, updated_at)
                VALUES ($id, 1, 'ghost', $now)
                """;
            settings.Parameters.AddWithValue("$id", retiredAccountId);
            settings.Parameters.AddWithValue("$now", T1.ToString("O"));
            await settings.ExecuteNonQueryAsync();

            using var rollup = connection.CreateCommand();
            rollup.CommandText = """
                INSERT INTO leaderboard_rollup (account_id, day, total_tokens, cost_usd, updated_at)
                VALUES ($id, $day, 999, '9.99', $now)
                """;
            rollup.Parameters.AddWithValue("$id", retiredAccountId);
            rollup.Parameters.AddWithValue("$day", Day5);
            rollup.Parameters.AddWithValue("$now", T1.ToString("O"));
            await rollup.ExecuteNonQueryAsync();
        }

        var withOrphan = await BoardAsync(anonymous);
        withOrphan.Participants.Should().Be(0, "孤儿账号残留行不得计入（accounts JOIN）");
        withOrphan.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Board_CostSum_IsExactDecimal()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);
        using var anonymous = factory.CreateClient();

        (await PutPreferenceAsync(client, optIn: true, "Alice")).EnsureSuccessStatusCode();
        var modelA = RelayTestSync.UsageRow("devA", Day5, 10, 1.10m, T1);
        modelA.Model = "model-a";
        var modelB = RelayTestSync.UsageRow("devA", Day5, 20, 2.20m, T1);
        modelB.Model = "model-b";
        (await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact> { modelA, modelB },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var board = await GetBoardAsync(anonymous, $"/api/leaderboard?board=cost&month={Month}");
        var entry = board.Entries.Should().ContainSingle().Subject;
        entry.TotalCostUsd.Should().Be(3.30m, "榜单成本经 JSON 反序列化必须精确 decimal（浮点求和会被此断言抓住）");
        entry.TotalTokens.Should().Be(30);
    }

    [Fact]
    public async Task UnbindDevice_RebuildsRollup()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);
        using var anonymous = factory.CreateClient();

        (await PutPreferenceAsync(client, optIn: true, "Alice")).EnsureSuccessStatusCode();
        (await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", Day5, 100, 1.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await RelayTestSync.PushAsync(client, "devB", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devB", Day1, 50, 0.50m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();

        var before = await BoardAsync(anonymous);
        before.Entries.Should().ContainSingle().Which.TotalTokens.Should().Be(150);

        // 解绑 devB：级联删该设备行 + 事务内全量重建 rollup → devB 独占的历史 day 虚高必须消失。
        using var unbind = await client.DeleteAsync("/api/devices/devB");
        unbind.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await BoardAsync(anonymous);
        after.Participants.Should().Be(1, "解绑不改变 opt-in 状态");
        after.Entries.Should().ContainSingle().Which.TotalTokens.Should().Be(100, "重建后 devB 独占 day 的 rollup 消失");
    }

    [Fact]
    public async Task ReOptIn_RebuildsFromAllDays()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);
        using var anonymous = factory.CreateClient();

        (await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", Day5, 100, 1.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await PutPreferenceAsync(client, optIn: true, "Alice")).EnsureSuccessStatusCode();
        (await BoardAsync(anonymous)).Entries.Should().ContainSingle().Which.TotalTokens.Should().Be(100);

        // 关闭即删 → 榜单归零；随后在未 opt-in 期间再 push 一行（落库、无 rollup）。
        (await PutPreferenceAsync(client, optIn: false, null)).EnsureSuccessStatusCode();
        (await BoardAsync(anonymous)).Entries.Should().BeEmpty();
        (await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", Day1, 50, 0.50m, T2) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await BoardAsync(anonymous)).Entries.Should().BeEmpty("未 opt-in 的 push 不产生 rollup");

        // 再次 opt-in → 全量重建必须拾回全部 usage_rows 历史（含 opt-out 窗口期间 push 的行）。
        (await PutPreferenceAsync(client, optIn: true, "Alice")).EnsureSuccessStatusCode();
        var rebuilt = await BoardAsync(anonymous);
        rebuilt.Participants.Should().Be(1);
        rebuilt.Entries.Should().ContainSingle().Which.TotalTokens.Should().Be(150, "重建从 usage_rows 全量聚合，不吃残留 rollup");
    }

    [Fact]
    public async Task DeleteAccount_ClearsBoard()
    {
        await using var factory = new RelayFactory(token: "abc");
        using var client = RelayTestSync.Client(factory);
        using var anonymous = factory.CreateClient();

        (await PutPreferenceAsync(client, optIn: true, "Alice")).EnsureSuccessStatusCode();
        (await RelayTestSync.PushAsync(client, "devA", new SyncPushRequest(
            new List<UsageFact> { RelayTestSync.UsageRow("devA", Day5, 100, 1.00m, T1) },
            new List<SessionFact>()))).EnsureSuccessStatusCode();
        (await BoardAsync(anonymous)).Participants.Should().Be(1);

        using var deleted = await client.DeleteAsync("/api/me");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var board = await BoardAsync(anonymous);
        board.Participants.Should().Be(0, "删号级联清退榜单行");
        board.Entries.Should().BeEmpty();

        using var preference = await client.GetAsync("/api/me/leaderboard");
        preference.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "删号后旧凭据失效");
    }

    // ---- HTTP 助手 ----

    private static Task<LeaderboardResponse> BoardAsync(HttpClient anonymous, string board = "tokens") =>
        GetBoardAsync(anonymous, $"/api/leaderboard?board={board}&month={Month}");

    private static async Task<LeaderboardResponse> GetBoardAsync(HttpClient client, string uri)
    {
        using var response = await client.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.LeaderboardResponse))!;
    }

    private static async Task<LeaderboardPreferenceResponse> GetPreferenceAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/me/leaderboard");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(SyncJsonContext.Default.LeaderboardPreferenceResponse))!;
    }

    private static async Task<HttpResponseMessage> PutPreferenceAsync(HttpClient client, bool optIn, string? displayName)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, "/api/me/leaderboard")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new LeaderboardPreferenceRequest(optIn, displayName),
                    SyncJsonContext.Default.LeaderboardPreferenceRequest),
                Encoding.UTF8,
                "application/json"),
        };
        return await client.SendAsync(message);
    }

    // ---- 直灌脚手架（限位/孤儿注入需要多账号，HTTP 层无造号通道，直接对库写入）----

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"tokenmetry-leaderboard-http-{Guid.NewGuid():N}.db");

    private static async Task<SqliteConnection> OpenDbAsync(string dbPath)
    {
        var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task SeedBoardAccountsAsync(string dbPath, int count)
    {
        await using var connection = await OpenDbAsync(dbPath);
        for (var i = 0; i < count; i++)
        {
            using (var account = connection.CreateCommand())
            {
                account.CommandText = """
                    INSERT INTO accounts (id, kind, credential_hash, github_sub, display_name, avatar_url, created_at, seq)
                    VALUES ($id, 'github', $hash, $sub, $name, NULL, $now, 0)
                    """;
                account.Parameters.AddWithValue("$id", $"seed-acct-{i:000}");
                account.Parameters.AddWithValue("$hash", $"seed-hash-{i:000}");
                account.Parameters.AddWithValue("$sub", $"seed-sub-{i:000}");
                account.Parameters.AddWithValue("$name", $"user-{i:000}");
                account.Parameters.AddWithValue("$now", T1.ToString("O"));
                await account.ExecuteNonQueryAsync();
            }

            using (var settings = connection.CreateCommand())
            {
                settings.CommandText = """
                    INSERT INTO leaderboard_settings (account_id, opt_in, display_name, updated_at)
                    VALUES ($id, 1, NULL, $now)
                    """;
                settings.Parameters.AddWithValue("$id", $"seed-acct-{i:000}");
                settings.Parameters.AddWithValue("$now", T1.ToString("O"));
                await settings.ExecuteNonQueryAsync();
            }

            using (var rollup = connection.CreateCommand())
            {
                rollup.CommandText = """
                    INSERT INTO leaderboard_rollup (account_id, day, total_tokens, cost_usd, updated_at)
                    VALUES ($id, $day, $tokens, NULL, $now)
                    """;
                rollup.Parameters.AddWithValue("$id", $"seed-acct-{i:000}");
                rollup.Parameters.AddWithValue("$day", Day1);
                rollup.Parameters.AddWithValue("$tokens", 1000 + i);
                rollup.Parameters.AddWithValue("$now", T1.ToString("O"));
                await rollup.ExecuteNonQueryAsync();
            }
        }
    }
}
