using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.Cindy;

/// <summary>
/// Cindy 数据目录发现：Cindy 是 Electron 应用，各 runtime 的私有数据都写在它的 <c>userData</c> 下——
/// Pi 在 <c>pi-agent-home/sessions</c>（本机实测：<c>%APPDATA%\Cindy\pi-agent-home\sessions\*.jsonl</c>），
/// Codex 在 <c>codex-home</c>（默认网关账号）与 <c>codex-accounts/&lt;owner-hash&gt;/&lt;provider&gt;</c>（订阅账号）。
/// <list type="bullet">
/// <item>显式配置的 userData 根优先（测试 / 调用方钉定；不做 ~ 展开，与 pi 会话目录同口径）；</item>
/// <item>否则按平台 appData 根 + Cindy 的区域目录清单发现（Electron <c>app.getPath("userData")</c> 同构）：
/// Windows = <c>%APPDATA%</c>、macOS = <c>~/Library/Application Support</c>、
/// Linux = <c>$XDG_CONFIG_HOME</c> 或 <c>~/.config</c>；</item>
/// <item>区域目录名可同机并存：cn = <c>Cindy</c>、global = <c>CindyGlobal</c>、历史 = <c>xdt-maker</c>
/// （见 Cindy <c>brandIdentity.ts</c> 的 <c>userDataDirNameByRegion</c> /
/// <c>legacyUserDataDirNames</c>）；只收集实际存在的目录。dev 的 <c>CindyDev</c> 有意不统计：那是开发者
/// 本机的开发构建身份，普通用户不会接触，且常含测试/合成数据。</item>
/// <item>独立启动的 Cindy Host 默认 profile 在 <c>&lt;appData&gt;/CindyHost/&lt;profile&gt;</c>
/// （见 Cindy host <c>profile-path.mjs</c>），其内布局与桌面 userData 同构，也一并发现。</item>
/// <item>最终每个 runtime 仍要求目标目录实际存在才作为扫描根（不存在 → 空清单 = NoData）。</item>
/// </list>
/// Cindy 未提供可被子进程读取的 userData 环境变量（app.asar 内无 CINDY_USER_DATA 之类开关），
/// 因此这里刻意<b>不发明</b>环境变量：平台默认 + 显式配置两条路，读不到就如实 NoData。
/// </summary>
public class CindyLogLocator
{
    /// <summary>cn 区域 userData 目录名（Electron productName 默认派生，三平台同值）。</summary>
    public const string UserDataFolderName = "Cindy";

    /// <summary>
    /// 可同机并存的 userData 区域目录名（cn / global / 历史 xdt-maker）。
    /// dev 的 <c>CindyDev</c> 有意排除：普通用户不会接触开发构建，且开发机常含测试/合成数据。
    /// 顺序固定，保证扫描根签名与诊断输出稳定。
    /// </summary>
    public static readonly IReadOnlyList<string> UserDataFolderNames =
        ["Cindy", "CindyGlobal", "xdt-maker"];

    /// <summary>独立启动的 Cindy Host 默认 profile 根目录名（其下再按区域 / 自定义 profile 名分目录）。</summary>
    public const string HostDataFolderName = "CindyHost";

    /// <summary>pi runtime home 相对 userData 的路径（Cindy 自有布局）。</summary>
    public const string PiHomeRelativePath = "pi-agent-home";

    /// <summary>pi 会话目录相对 userData 的路径。</summary>
    public const string SessionsRelativePath = PiHomeRelativePath + "/sessions";

    /// <summary>Codex home 相对 userData 的路径（默认网关账号 / Host profile 同布局）。</summary>
    public const string CodexHomeRelativePath = "codex-home";

    /// <summary>Codex 订阅账号 home 根相对 userData 的路径（其下为 &lt;owner-hash&gt;/&lt;provider&gt;）。</summary>
    public const string CodexAccountsRelativePath = "codex-accounts";

    /// <summary>owner 主库文件名前缀（<c>&lt;prefix&gt;-&lt;ownerId&gt;.db</c>）。</summary>
    public const string OwnerDatabasePrefix = "cindy";

    /// <summary>历史 owner 主库文件名前缀（改名前的 CN 版）。</summary>
    public const string LegacyOwnerDatabasePrefix = "xdt-maker";

    private readonly Func<string, string?> _environmentAccessor;

    public CindyLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析 Pi 会话目录清单：每个 userData 根 → <c>pi-agent-home/sessions</c>，仅保留实际存在的目录。
    /// 区域目录可同机并存（cn + global 双装），因此返回的是清单而非单根。
    /// </summary>
    public IReadOnlyList<string> ResolveSessionDirectories(string? configuredUserDataRoot)
    {
        var directories = new List<string>();
        foreach (var userDataRoot in ResolveUserDataRoots(configuredUserDataRoot))
        {
            var sessions = Path.Combine(userDataRoot, PiHomeRelativePath, "sessions");
            if (Directory.Exists(sessions))
            {
                directories.Add(sessions);
            }
        }

        return directories;
    }

    /// <summary>
    /// 解析 Cindy 私有的 Codex home 清单（Codex 适配器的附加发现根）：
    /// 每个 userData 根下的 <c>codex-home</c>，以及 <c>codex-accounts/&lt;owner-hash&gt;/&lt;provider&gt;</c>
    /// 订阅账号 home；只返回实际存在的目录。
    /// </summary>
    public IReadOnlyList<string> ResolveCodexHomes(string? configuredUserDataRoot)
    {
        var homes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var userDataRoot in ResolveUserDataRoots(configuredUserDataRoot))
        {
            AddExistingDirectory(Path.Combine(userDataRoot, CodexHomeRelativePath), homes, seen);

            var accountsRoot = Path.Combine(userDataRoot, CodexAccountsRelativePath);
            if (!Directory.Exists(accountsRoot))
            {
                continue;
            }

            foreach (var ownerDirectory in SortedDirectories(accountsRoot))
            {
                foreach (var providerDirectory in SortedDirectories(ownerDirectory))
                {
                    AddExistingDirectory(providerDirectory, homes, seen);
                }
            }
        }

        return homes;
    }

    /// <summary>
    /// 解析 Cindy owner 主库清单（<c>&lt;root&gt;/cindy-&lt;ownerId&gt;.db</c>，历史 <c>xdt-maker-*</c>）：
    /// 只取 owner 主库，排除设备锁库与同步/迁移产物。归属索引只读这些库。
    /// </summary>
    public IReadOnlyList<string> ResolveOwnerDatabases(string? configuredUserDataRoot)
    {
        var databases = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var userDataRoot in ResolveUserDataRoots(configuredUserDataRoot))
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(userDataRoot, "*.db").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            files.Sort(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith(OwnerDatabasePrefix + "-", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith(LegacyOwnerDatabasePrefix + "-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 同目录还有 device-link-ownership-<user>.lock.db 之类的非 owner 主库。
                if (name.Contains(".lock", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("device-link", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(file))
                {
                    databases.Add(file);
                }
            }
        }

        return databases;
    }

    /// <summary>
    /// 解析 Cindy userData 根清单：显式配置（trim 后非空）优先且只返回它；否则返回平台 appData 下
    /// 实际存在的区域目录 + 独立 Host profile 目录。无法解析平台根时返回空清单。
    /// </summary>
    public IReadOnlyList<string> ResolveUserDataRoots(string? configuredUserDataRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredUserDataRoot))
        {
            return [configuredUserDataRoot.Trim()];
        }

        if (ResolveAppDataRoot() is not { } appDataRoot)
        {
            return [];
        }

        var roots = new List<string>();
        foreach (var folderName in UserDataFolderNames)
        {
            var root = Path.Combine(appDataRoot, folderName);
            if (Directory.Exists(root))
            {
                roots.Add(root);
            }
        }

        // 独立启动的 Host 默认 profile（<appData>/CindyHost/<region|profile>）：目录布局与桌面同构。
        var hostRoot = Path.Combine(appDataRoot, HostDataFolderName);
        if (Directory.Exists(hostRoot))
        {
            roots.AddRange(SortedDirectories(hostRoot));
        }

        return roots;
    }

    /// <summary>解析 Cindy userData 根：显式配置（trim 后非空）优先，否则平台默认 cn 目录；无法解析返回 null。</summary>
    public string? ResolveUserDataRoot(string? configuredUserDataRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredUserDataRoot))
        {
            return configuredUserDataRoot.Trim();
        }

        return ResolveAppDataRoot() is { } appDataRoot
            ? Path.Combine(appDataRoot, UserDataFolderName)
            : null;
    }

    /// <summary>
    /// 平台 appData 根（Electron <c>app.getPath("appData")</c> 同构）。区域 / Host 目录都拼在它下面。
    /// 与 userData 根分离，是为了让区域目录清单只写一个文件夹名。
    /// </summary>
    public string? ResolveAppDataRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return NonEmpty(_environmentAccessor("APPDATA"));
        }

        if (OperatingSystem.IsMacOS())
        {
            return JsonlHomePath.GetHomeDir(_environmentAccessor) is { } home
                ? Path.Combine(home, "Library", "Application Support")
                : null;
        }

        if (NonEmpty(_environmentAccessor("XDG_CONFIG_HOME")) is { } xdgConfigHome)
        {
            return xdgConfigHome;
        }

        return JsonlHomePath.GetHomeDir(_environmentAccessor) is { } unixHome
            ? Path.Combine(unixHome, ".config")
            : null;
    }

    private static IEnumerable<string> SortedDirectories(string directory)
    {
        List<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        children.Sort(StringComparer.Ordinal);
        return children;
    }

    private static void AddExistingDirectory(string path, List<string> homes, HashSet<string> seen)
    {
        if (Directory.Exists(path) && seen.Add(path))
        {
            homes.Add(path);
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
