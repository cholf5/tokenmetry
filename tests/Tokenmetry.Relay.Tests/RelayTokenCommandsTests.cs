namespace Tokenmetry.Relay.Tests;

using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Sync;

/// <summary>
/// 自建多用户成员账号（docs/selfhost.md「多用户」）：签发/列表/吊销、与运营者 shared 账号隔离、
/// 运营者 token 轮换不清成员、成员间数据按 account_id 隔离。
/// </summary>
public sealed class RelayTokenCommandsTests
{
    private static readonly DateTimeOffset T1 = new(2026, 6, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Add_CreatesIsolatedMemberAccounts()
    {
        var dbPath = NewDbPath();
        try
        {
            var database = new RelayDatabase(dbPath);
            await database.EnsureCreatedAsync();

            var alice = await RelayTokenCommands.AddAsync(database, "Alice");
            var bob = await RelayTokenCommands.AddAsync(database, "Bob");

            alice.Token.Should().HaveLength(64);
            alice.Token.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f').Should().BeTrue();
            alice.AccountId.Should().NotBe(bob.AccountId);
            alice.Token.Should().NotBe(bob.Token);

            await using var connection = database.OpenConnection();
            var aliceAccount = await RelayAuth.MapAccountAsync(connection, alice.Token);
            aliceAccount.Should().NotBeNull();
            aliceAccount!.Id.Should().Be(alice.AccountId);
            aliceAccount.Kind.Should().Be(RelayTokenCommands.MemberKind);
            aliceAccount.DisplayName.Should().Be("Alice");

            var bobAccount = await RelayAuth.MapAccountAsync(connection, bob.Token);
            bobAccount.Should().NotBeNull();
            bobAccount!.Id.Should().Be(bob.AccountId);
            bobAccount.Id.Should().NotBe(aliceAccount.Id);
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    [Fact]
    public async Task Add_DuplicateDisplayName_IsRejectedCaseInsensitively()
    {
        var dbPath = NewDbPath();
        try
        {
            var database = new RelayDatabase(dbPath);
            await database.EnsureCreatedAsync();
            await RelayTokenCommands.AddAsync(database, "Alice");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => RelayTokenCommands.AddAsync(database, "  alice  "));
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    [Fact]
    public async Task List_ReportsDevicesAndLastSeen()
    {
        var dbPath = NewDbPath();
        try
        {
            var database = new RelayDatabase(dbPath);
            await database.EnsureCreatedAsync();
            var alice = await RelayTokenCommands.AddAsync(database, "Alice");

            await using (var connection = database.OpenConnection())
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO devices (account_id, device_id, name, last_seen_at)
                    VALUES ($accountId, 'devA', 'Work Mac', $seen)
                    """;
                cmd.Parameters.AddWithValue("$accountId", alice.AccountId);
                cmd.Parameters.AddWithValue("$seen", T1.ToString("O"));
                await cmd.ExecuteNonQueryAsync();
            }

            var members = await RelayTokenCommands.ListAsync(database);
            members.Should().ContainSingle();
            members[0].AccountId.Should().Be(alice.AccountId);
            members[0].DisplayName.Should().Be("Alice");
            members[0].DeviceCount.Should().Be(1);
            members[0].LastSeenAt.Should().Be(T1);
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    [Fact]
    public async Task OperatorTokenRotation_DoesNotRetireMembers()
    {
        var dbPath = NewDbPath();
        try
        {
            var database = new RelayDatabase(dbPath);
            await database.EnsureCreatedAsync();

            await RelayDatabase.BootstrapSharedAccountAsync(database, "operator-token");
            var alice = await RelayTokenCommands.AddAsync(database, "Alice");
            await RelayDatabase.BootstrapSharedAccountAsync(database, "rotated-operator-token");

            await using var connection = database.OpenConnection();
            (await RelayAuth.MapAccountAsync(connection, alice.Token))
                .Should().NotBeNull("成员账号是独立 kind，不受运营者 token 轮换清理影响");
            (await RelayAuth.MapAccountAsync(connection, "operator-token"))
                .Should().BeNull("换 token = 旧 shared 账号退役");
            (await RelayAuth.MapAccountAsync(connection, "rotated-operator-token"))
                .Should().NotBeNull();
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    [Fact]
    public async Task Revoke_DeletesAccountAndEveryAccountScopedRow()
    {
        var dbPath = NewDbPath();
        try
        {
            var database = new RelayDatabase(dbPath);
            await database.EnsureCreatedAsync();
            var alice = await RelayTokenCommands.AddAsync(database, "Alice");

            await using (var connection = database.OpenConnection())
            {
                foreach (var sql in new[]
                         {
                             """
                             INSERT INTO devices (account_id, device_id, name, last_seen_at)
                             VALUES ($accountId, 'devA', 'Work Mac', '2026-06-19T10:00:00.0000000Z')
                             """,
                             """
                             INSERT INTO usage_rows (account_id, device_id, day, grain, updated_at, seq)
                             VALUES ($accountId, 'devA', '2026-06-19', 'day', '2026-06-19T10:00:00.0000000Z', 1)
                             """,
                             """
                             INSERT INTO session_rows (account_id, device_id, session_id, first_seen_at, last_seen_at, updated_at, seq)
                             VALUES ($accountId, 'devA', 's-1', '2026-06-19T10:00:00.0000000Z', '2026-06-19T10:00:00.0000000Z', '2026-06-19T10:00:00.0000000Z', 2)
                             """,
                             """
                             INSERT INTO leaderboard_settings (account_id, opt_in, display_name, updated_at)
                             VALUES ($accountId, 1, NULL, '2026-06-19T10:00:00.0000000Z')
                             """,
                             """
                             INSERT INTO leaderboard_rollup (account_id, day, total_tokens, cost_usd, updated_at)
                             VALUES ($accountId, '2026-06-19', 100, '1.00', '2026-06-19T10:00:00.0000000Z')
                             """,
                             """
                             INSERT INTO account_tokens (account_id, credential_hash, created_at)
                             VALUES ($accountId, 'legacy-hash', '2026-06-19T10:00:00.0000000Z')
                             """,
                         })
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$accountId", alice.AccountId);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            var revoked = await RelayTokenCommands.RevokeAsync(database, accountId: null, displayName: "alice");
            revoked.AccountId.Should().Be(alice.AccountId);
            revoked.DisplayName.Should().Be("Alice");

            await using var after = database.OpenConnection();
            (await RelayAuth.MapAccountAsync(after, alice.Token)).Should().BeNull("吊销后 token 立即 401");
            foreach (var table in new[]
                     {
                         "usage_rows", "session_rows", "devices", "account_tokens", "leaderboard_settings", "leaderboard_rollup",
                     })
            {
                (await CountRowsAsync(after, table, alice.AccountId)).Should().Be(0, $"{table} 必须级联清空");
            }
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    [Fact]
    public async Task Revoke_UnknownMember_IsRejected()
    {
        var dbPath = NewDbPath();
        try
        {
            var database = new RelayDatabase(dbPath);
            await database.EnsureCreatedAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => RelayTokenCommands.RevokeAsync(database, accountId: null, displayName: "nobody"));
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    [Fact]
    public async Task Cli_AddListRevoke_RoundTrips()
    {
        var dbPath = NewDbPath();
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var addExit = await RelayTokenCli.RunAsync(
                ["add", "--name", "Alice", "--db", dbPath], "unused.db", output, error);
            addExit.Should().Be(0);
            error.ToString().Should().BeEmpty();
            var addText = output.ToString();
            addText.Should().Contain("Alice");
            var token = Regex.Match(addText, @"\b[0-9a-f]{64}\b").Value;
            token.Should().NotBeNullOrEmpty("add 输出必须包含只显示一次的明文 token");

            var database = new RelayDatabase(dbPath);
            await using (var connection = database.OpenConnection())
            {
                (await RelayAuth.MapAccountAsync(connection, token)).Should().NotBeNull();
            }

            output.GetStringBuilder().Clear();
            var listExit = await RelayTokenCli.RunAsync(["list", "--db", dbPath], "unused.db", output, error);
            listExit.Should().Be(0);
            output.ToString().Should().Contain("Alice");

            output.GetStringBuilder().Clear();
            var revokeExit = await RelayTokenCli.RunAsync(
                ["revoke", "--name", "Alice", "--db", dbPath], "unused.db", output, error);
            revokeExit.Should().Be(0);

            await using (var connection = database.OpenConnection())
            {
                (await RelayAuth.MapAccountAsync(connection, token)).Should().BeNull();
            }
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    [Fact]
    public async Task Cli_UsageErrors_ReturnExitCode2()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var unknown = await RelayTokenCli.RunAsync(["nope"], "unused.db", output, error);
        unknown.Should().Be(2);
        error.ToString().Should().Contain("未知 token 子命令");

        error.GetStringBuilder().Clear();
        var missingSelector = await RelayTokenCli.RunAsync(
            ["revoke", "--db", "unused.db"], "unused.db", output, error);
        missingSelector.Should().Be(2);
        error.ToString().Should().Contain("--id 或 --name");
    }

    [Fact]
    public async Task Members_SeeOnlyTheirOwnSyncedRows()
    {
        var dbPath = NewDbPath();
        await using var factory = new RelayFactory(token: "operator-token", dbPath: dbPath);
        try
        {
            var database = new RelayDatabase(dbPath);
            await database.EnsureCreatedAsync();
            var alice = await RelayTokenCommands.AddAsync(database, "Alice");
            var bob = await RelayTokenCommands.AddAsync(database, "Bob");

            using var aliceClient = RelayTestSync.Client(factory, alice.Token);
            using var bobClient = RelayTestSync.Client(factory, bob.Token);

            await RelayTestSync.RegisterAsync(aliceClient, "alice-dev", "Alice Mac");
            await RelayTestSync.RegisterAsync(bobClient, "bob-dev", "Bob PC");
            (await RelayTestSync.PushAsync(aliceClient, "alice-dev", new SyncPushRequest(
                new List<UsageFact> { RelayTestSync.UsageRow("alice-dev", "2026-06-19", 100, 1.00m, T1) },
                new List<SessionFact>()))).EnsureSuccessStatusCode();

            var bobPull = await RelayTestSync.PullAsync(bobClient, "bob-dev", since: 0);
            bobPull.UsageFacts.Should().BeEmpty("成员账号之间按 account_id 隔离");
            bobPull.SessionFacts.Should().BeEmpty();
            bobPull.Devices.Select(device => device.DeviceId).Should().Equal("bob-dev");

            var alicePull = await RelayTestSync.PullAsync(aliceClient, "alice-dev", since: 0);
            alicePull.UsageFacts.Should().BeEmpty("pull 不返回本机行");
            alicePull.Devices.Select(device => device.DeviceId).Should().Equal("alice-dev");
        }
        finally
        {
            TempDbCleanup.DeleteWithSidecars(dbPath);
        }
    }

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"tokenmetry-members-{Guid.NewGuid():N}.db");

    private static async Task<long> CountRowsAsync(SqliteConnection connection, string table, string accountId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE account_id = $accountId";
        cmd.Parameters.AddWithValue("$accountId", accountId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
