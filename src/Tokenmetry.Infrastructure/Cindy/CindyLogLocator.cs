using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.Cindy;

/// <summary>
/// Cindy 数据目录发现：Cindy 是 Electron 应用，pi 会话写在它的 <c>userData</c> 下的
/// <c>pi-agent-home/sessions</c>（本机实测：<c>%APPDATA%\Cindy\pi-agent-home\sessions\*.jsonl</c>）。
/// <list type="bullet">
/// <item>显式配置的 userData 根优先（测试 / 调用方钉定；不做 ~ 展开，与 pi 会话目录同口径）；</item>
/// <item>否则按平台默认（Electron <c>app.getPath("userData")</c> 同构）：
/// Windows = <c>%APPDATA%\Cindy</c>、macOS = <c>~/Library/Application Support/Cindy</c>、
/// Linux = <c>$XDG_CONFIG_HOME/Cindy</c> 或 <c>~/.config/Cindy</c>；</item>
/// <item>最终仍要求 <c>pi-agent-home/sessions</c> 实际存在才作为扫描根（不存在 → 空清单 = NoData）。</item>
/// </list>
/// Cindy 未提供可被子进程读取的 userData 环境变量（app.asar 内无 CINDY_USER_DATA 之类开关），
/// 因此这里刻意<b>不发明</b>环境变量：平台默认 + 显式配置两条路，读不到就如实 NoData。
/// </summary>
public class CindyLogLocator
{
    /// <summary>userData 目录名（Electron app 名，三平台同值）。</summary>
    public const string UserDataFolderName = "Cindy";

    /// <summary>pi runtime home 相对 userData 的路径（Cindy 自有布局）。</summary>
    public const string PiHomeRelativePath = "pi-agent-home";

    /// <summary>会话目录相对 userData 的路径。</summary>
    public const string SessionsRelativePath = PiHomeRelativePath + "/sessions";

    private readonly Func<string, string?> _environmentAccessor;

    public CindyLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>解析会话目录清单：userData 根 → <c>pi-agent-home/sessions</c>，仅保留实际存在的目录。</summary>
    public IReadOnlyList<string> ResolveSessionDirectories(string? configuredUserDataRoot)
    {
        if (ResolveUserDataRoot(configuredUserDataRoot) is not { } userDataRoot)
        {
            return [];
        }

        var sessions = Path.Combine(userDataRoot, "pi-agent-home", "sessions");
        return Directory.Exists(sessions) ? [sessions] : [];
    }

    /// <summary>解析 Cindy userData 根：显式配置（trim 后非空）优先，否则平台默认；无法解析返回 null。</summary>
    public string? ResolveUserDataRoot(string? configuredUserDataRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredUserDataRoot))
        {
            return configuredUserDataRoot.Trim();
        }

        if (OperatingSystem.IsWindows())
        {
            return NonEmpty(_environmentAccessor("APPDATA")) is { } appData
                ? Path.Combine(appData, UserDataFolderName)
                : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return JsonlHomePath.GetHomeDir(_environmentAccessor) is { } home
                ? Path.Combine(home, "Library", "Application Support", UserDataFolderName)
                : null;
        }

        if (NonEmpty(_environmentAccessor("XDG_CONFIG_HOME")) is { } xdgConfigHome)
        {
            return Path.Combine(xdgConfigHome, UserDataFolderName);
        }

        return JsonlHomePath.GetHomeDir(_environmentAccessor) is { } unixHome
            ? Path.Combine(unixHome, ".config", UserDataFolderName)
            : null;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
