using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Tokenmetry.Relay;

/// <summary>
/// 自建多用户成员账号（docs/selfhost.md「多用户」）：每成员一个独立账号 + 独立 token，
/// 数据按 account_id 与运营者自用的 shared 账号完全隔离。
///
/// 刻意不复用 kind='shared'：BootstrapSharedAccountAsync 每次启动会退役其他 shared 账号
/// （换 TOKENMETRY_RELAY_TOKEN = 重签），成员账号用独立 kind 才不会被误清。
/// token 明文只在签发时返回一次，库里只存 SHA-256（与 RelayAuth 同一口径）。
/// </summary>
internal static class RelayTokenCommands
{
    /// <summary>成员账号类型。只对 kind='member' 做签发/列表/吊销，不碰 shared 与 github。</summary>
    public const string MemberKind = "member";

    /// <summary>与排行榜展示名上限一致（LeaderboardEndpoints.MaxDisplayNameLength）。</summary>
    public const int MaxDisplayNameLength = 64;

    private static readonly string[] AccountDeleteStatements =
    [
        "DELETE FROM usage_rows WHERE account_id = $accountId",
        "DELETE FROM session_rows WHERE account_id = $accountId",
        "DELETE FROM leaderboard_settings WHERE account_id = $accountId",
        "DELETE FROM leaderboard_rollup WHERE account_id = $accountId",
        "DELETE FROM devices WHERE account_id = $accountId",
        "DELETE FROM account_tokens WHERE account_id = $accountId",
        "DELETE FROM accounts WHERE id = $accountId",
    ];

    public sealed record AddedMember(string AccountId, string DisplayName, string Token);

    public sealed record MemberInfo(
        string AccountId,
        string DisplayName,
        DateTimeOffset CreatedAt,
        int DeviceCount,
        DateTimeOffset? LastSeenAt);

    public sealed record RevokedMember(string AccountId, string DisplayName);

    /// <summary>签发一个成员账号并返回明文 token（只此一次）。展示名在成员中不区分大小写唯一。</summary>
    public static async Task<AddedMember> AddAsync(RelayDatabase database, string displayName, CancellationToken ct = default)
    {
        var name = NormalizeName(displayName);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var accountId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                SELECT 1 FROM accounts
                WHERE kind = $kind AND display_name = $name COLLATE NOCASE
                LIMIT 1
                """;
            duplicate.Parameters.AddWithValue("$kind", MemberKind);
            duplicate.Parameters.AddWithValue("$name", name);
            if (await duplicate.ExecuteScalarAsync(ct) is not null)
            {
                throw new InvalidOperationException($"成员 \"{name}\" 已存在；请换一个展示名，或先 revoke 旧账号。");
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO accounts (id, kind, credential_hash, github_sub, display_name, avatar_url, created_at, seq)
                VALUES ($id, $kind, $hash, NULL, $name, NULL, $now, 0)
                """;
            insert.Parameters.AddWithValue("$id", accountId);
            insert.Parameters.AddWithValue("$kind", MemberKind);
            insert.Parameters.AddWithValue("$hash", RelayAuth.HashToken(token));
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new AddedMember(accountId, name, token);
    }

    /// <summary>列出全部成员账号（按创建时间）；Devices/LastSeen 用于识别仍在用的账号。</summary>
    public static async Task<IReadOnlyList<MemberInfo>> ListAsync(RelayDatabase database, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.display_name, a.created_at,
                   (SELECT COUNT(*) FROM devices d WHERE d.account_id = a.id),
                   (SELECT MAX(d.last_seen_at) FROM devices d WHERE d.account_id = a.id)
            FROM accounts a
            WHERE a.kind = $kind
            ORDER BY a.created_at, a.id
            """;
        cmd.Parameters.AddWithValue("$kind", MemberKind);

        var members = new List<MemberInfo>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var displayName = reader.IsDBNull(1) ? "anonymous" : reader.GetString(1);
            var createdAt = DateTimeOffset.Parse(
                reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            DateTimeOffset? lastSeen = reader.IsDBNull(4)
                ? null
                : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            members.Add(new MemberInfo(reader.GetString(0), displayName, createdAt, (int)reader.GetInt64(3), lastSeen));
        }

        return members;
    }

    /// <summary>
    /// 吊销成员：按 id 或展示名（不区分大小写）唯一定位，事务内删账号的全部行。
    /// 只允许 kind='member'——运营者 shared 账号与 GitHub 账号不能从这里误删。
    /// </summary>
    public static async Task<RevokedMember> RevokeAsync(
        RelayDatabase database,
        string? accountId,
        string? displayName,
        CancellationToken ct = default)
    {
        var byId = !string.IsNullOrWhiteSpace(accountId);
        var byName = !string.IsNullOrWhiteSpace(displayName);
        if (byId == byName)
        {
            throw new InvalidOperationException("必须且只能指定 --id 或 --name。");
        }

        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        string resolvedId;
        string resolvedName;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            if (byId)
            {
                find.CommandText = "SELECT id, display_name FROM accounts WHERE kind = $kind AND id = $value";
                find.Parameters.AddWithValue("$value", accountId!.Trim());
            }
            else
            {
                find.CommandText = """
                    SELECT id, display_name FROM accounts
                    WHERE kind = $kind AND display_name = $value COLLATE NOCASE
                    """;
                find.Parameters.AddWithValue("$value", displayName!.Trim());
            }

            find.Parameters.AddWithValue("$kind", MemberKind);

            var matches = new List<(string Id, string Name)>();
            using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                matches.Add((reader.GetString(0), reader.IsDBNull(1) ? "anonymous" : reader.GetString(1)));
            }

            if (matches.Count == 0)
            {
                throw new InvalidOperationException("没有匹配的成员账号。");
            }

            if (matches.Count > 1)
            {
                throw new InvalidOperationException("展示名匹配到多个成员；请改用 --id。");
            }

            (resolvedId, resolvedName) = matches[0];
        }

        foreach (var sql in AccountDeleteStatements)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = sql;
            delete.Parameters.AddWithValue("$accountId", resolvedId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new RevokedMember(resolvedId, resolvedName);
    }

    private static string NormalizeName(string displayName)
    {
        var name = displayName.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("展示名不能为空。");
        }

        if (name.Length > MaxDisplayNameLength)
        {
            throw new InvalidOperationException($"展示名最多 {MaxDisplayNameLength} 个字符。");
        }

        if (name.Any(char.IsControl))
        {
            throw new InvalidOperationException("展示名不能包含控制字符。");
        }

        return name;
    }
}
