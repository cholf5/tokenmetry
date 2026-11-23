using System.Globalization;

namespace Tokenmetry.Relay;

/// <summary>
/// 自建多用户成员管理 CLI（docs/selfhost.md「多用户」）：
/// <c>Tokenmetry.Relay token add|list|revoke</c>。在启动 Web 主机之前分流，
/// 因此 Docker 里可 <c>docker exec</c>、systemd 裸机可直接跑同一二进制。
/// 只读写同一 SQLite 库，不增加任何网络管理面。
/// </summary>
internal static class RelayTokenCli
{
    public const string Usage = """
        Tokenmetry Relay — 自建多用户成员管理

        Usage:
          Tokenmetry.Relay token add --name <展示名> [--db <relay.db>]
          Tokenmetry.Relay token list [--db <relay.db>]
          Tokenmetry.Relay token revoke --id <account-id> [--db <relay.db>]
          Tokenmetry.Relay token revoke --name <展示名> [--db <relay.db>]

        不带 token 子命令 = 正常启动中继服务。
        成员账号与 TOKENMETRY_RELAY_TOKEN 的单租户账号互相隔离；每个成员用自己的 token
        在客户端「设置 → 跨设备同步 → 自建中继」里接入。token 只在 add 时显示一次。
        """;

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default) =>
        RunAsync(
            args,
            Environment.GetEnvironmentVariable("TOKENMETRY_RELAY_DB") ?? "relay.db",
            output,
            error,
            ct);

    public static async Task<int> RunAsync(
        string[] args,
        string defaultDbPath,
        TextWriter output,
        TextWriter error,
        CancellationToken ct = default)
    {
        if (args.Length == 0)
        {
            return await UsageErrorAsync(error, "缺少 token 子命令（add / list / revoke）。");
        }

        var subcommand = args[0].ToLowerInvariant();
        if (subcommand is "help" or "-h" or "--help")
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }

        if (subcommand is not ("add" or "list" or "revoke"))
        {
            return await UsageErrorAsync(error, $"未知 token 子命令：{subcommand}");
        }

        string? dbPath = null;
        string? name = null;
        string? id = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db":
                    if (!TryTakeValue(args, ref i, out dbPath))
                    {
                        return await UsageErrorAsync(error, "--db 需要一个路径。");
                    }

                    break;
                case "--name":
                    if (!TryTakeValue(args, ref i, out name))
                    {
                        return await UsageErrorAsync(error, "--name 需要一个展示名。");
                    }

                    break;
                case "--id":
                    if (!TryTakeValue(args, ref i, out id))
                    {
                        return await UsageErrorAsync(error, "--id 需要一个账号 id。");
                    }

                    break;
                case "-h":
                case "--help":
                    await output.WriteLineAsync(Usage);
                    return 0;
                default:
                    return await UsageErrorAsync(error, $"未知参数：{args[i]}");
            }
        }

        if (dbPath is not null && string.IsNullOrWhiteSpace(dbPath))
        {
            return await UsageErrorAsync(error, "--db 需要一个非空路径。");
        }

        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            return await UsageErrorAsync(error, "--name 需要一个非空展示名。");
        }

        if (id is not null && string.IsNullOrWhiteSpace(id))
        {
            return await UsageErrorAsync(error, "--id 需要一个非空账号 id。");
        }

        if (subcommand == "add" && name is null)
        {
            return await UsageErrorAsync(error, "token add 需要 --name <展示名>。");
        }

        if (subcommand == "add" && id is not null)
        {
            return await UsageErrorAsync(error, "token add 不接受 --id。");
        }

        if (subcommand == "list" && (name is not null || id is not null))
        {
            return await UsageErrorAsync(error, "token list 不接受 --name / --id。");
        }

        if (subcommand == "revoke" && (name is null) == (id is null))
        {
            return await UsageErrorAsync(error, "token revoke 需要且只能指定 --id 或 --name。");
        }

        try
        {
            var resolvedDbPath = dbPath ?? defaultDbPath;
            EnsureDatabaseDirectory(resolvedDbPath);
            var database = new RelayDatabase(resolvedDbPath);
            await database.EnsureCreatedAsync(ct);

            switch (subcommand)
            {
                case "add":
                    var added = await RelayTokenCommands.AddAsync(database, name!, ct);
                    await output.WriteLineAsync($"已创建成员 \"{added.DisplayName}\"（account id: {added.AccountId}）。");
                    await output.WriteLineAsync($"Token（只显示这一次，请立即保存）：{added.Token}");
                    await output.WriteLineAsync(
                        "把中继地址和这个 token 一起发给对方；对方在「设置 → 跨设备同步 → 自建中继」里填写。");
                    return 0;

                case "list":
                    var members = await RelayTokenCommands.ListAsync(database, ct);
                    if (members.Count == 0)
                    {
                        await output.WriteLineAsync("没有成员账号。用 `token add --name <展示名>` 添加。");
                        return 0;
                    }

                    await output.WriteLineAsync("成员账号：");
                    await output.WriteLineAsync($"{"ID",-32}  {"NAME",-20}  {"DEVICES",7}  LAST SEEN");
                    foreach (var member in members)
                    {
                        var lastSeen = member.LastSeenAt is { } seen
                            ? seen.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                            : "-";
                        await output.WriteLineAsync(
                            $"{member.AccountId,-32}  {Truncate(member.DisplayName, 20),-20}  {member.DeviceCount,7}  {lastSeen}");
                    }

                    return 0;

                case "revoke":
                    var revoked = await RelayTokenCommands.RevokeAsync(database, id, name, ct);
                    await output.WriteLineAsync(
                        $"已吊销成员 \"{revoked.DisplayName}\"（account id: {revoked.AccountId}），其同步数据、设备与榜单记录已删除。");
                    return 0;

                default:
                    return await UsageErrorAsync(error, $"未知 token 子命令：{subcommand}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await error.WriteLineAsync($"token {subcommand}: {ex.Message}");
            return 1;
        }
    }

    private static bool TryTakeValue(string[] args, ref int index, out string? value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static async Task<int> UsageErrorAsync(TextWriter error, string message)
    {
        await error.WriteLineAsync(message);
        await error.WriteLineAsync(Usage);
        return 2;
    }

    private static void EnsureDatabaseDirectory(string dbPath)
    {
        var fullPath = Path.GetFullPath(dbPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
