using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.OpenCodeSqlite;

/// <summary>
/// OpenCode 数据目录定位（I-009，逐条移植蓝本 ../ccusage/rust/adapters/opencode/src/paths.rs；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// - OPENCODE_DATA_DIR（逗号分隔多目录，去空白、跳过空段）：设置了该环境变量就**只**用配置目录
///   （目录必须真实存在、按序去重），无效/为空都不回退默认目录（蓝本 does_not_fall_back 系列测试）；
/// - 未设置时：XDG_DATA_HOME（必须为绝对路径，空/相对视为未设置）→ &lt;XDG&gt;/opencode，
///   否则 &lt;home&gt;/.local/share/opencode；目录不存在即空列表（不回退另一处）；
/// - 用户主目录沿用仓库共享 JsonlHomePath 约定（USERPROFILE 优先于 HOME；蓝本 home.rs 为
///   HOME 优先——与 I-006/I-008 相同的已登记差异，常规环境两者一致）。
/// 环境变量经可注入访问器读取，测试不依赖真实用户目录。
/// </summary>
public class OpenCodeDataLocator
{
    /// <summary>OpenCode 数据目录环境变量（蓝本 OPENCODE_DATA_DIR_ENV）。</summary>
    public const string DataDirEnvironmentVariable = "OPENCODE_DATA_DIR";

    /// <summary>XDG 数据根环境变量（蓝本 XDG_DATA_HOME_ENV）。</summary>
    public const string XdgDataHomeEnvironmentVariable = "XDG_DATA_HOME";

    private readonly Func<string, string?> _getEnvironmentVariable;

    public OpenCodeDataLocator(Func<string, string?>? environmentAccessor = null)
    {
        _getEnvironmentVariable = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析参与扫描的 OpenCode 数据目录（按配置顺序去重，只保留真实存在的目录）。
    /// 配置目录（OPENCODE_DATA_DIR）优先且不回退；默认口径为
    /// ${XDG_DATA_HOME:-$HOME/.local/share}/opencode。
    /// </summary>
    public IReadOnlyList<string> ResolveDataDirectories(string? configuredDataDir)
    {
        if (configuredDataDir is not null)
        {
            return ResolveConfiguredDirectories(configuredDataDir);
        }

        var fromEnvironment = _getEnvironmentVariable(DataDirEnvironmentVariable);
        if (fromEnvironment is not null)
        {
            return ResolveConfiguredDirectories(fromEnvironment);
        }

        return ResolveDefaultDirectories();
    }

    /// <summary>OPENCODE_DATA_DIR 口径：逗号分隔、去空白、仅保留存在的目录、按序去重；无回退。</summary>
    private List<string> ResolveConfiguredDirectories(string raw)
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawPath in raw.Split(','))
        {
            var trimmed = rawPath.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var directory = Path.GetFullPath(JsonlHomePath.ExpandHomePath(trimmed, _getEnvironmentVariable));
            if (Directory.Exists(directory) && seen.Add(directory))
            {
                directories.Add(directory);
            }
        }

        return directories;
    }

    /// <summary>默认口径：XDG_DATA_HOME（绝对路径才生效）→ $HOME/.local/share，追加 opencode。</summary>
    private List<string> ResolveDefaultDirectories()
    {
        var dataHome = ResolveDataHome();
        if (dataHome.Length == 0)
        {
            return [];
        }

        var directory = Path.Combine(dataHome, "opencode");
        return Directory.Exists(directory) ? [directory] : [];
    }

    private string ResolveDataHome()
    {
        var xdgDataHome = _getEnvironmentVariable(XdgDataHomeEnvironmentVariable);
        if (xdgDataHome is { } rawXdg && Path.IsPathRooted(rawXdg))
        {
            return rawXdg;
        }

        return JsonlHomePath.GetHomeDir(_getEnvironmentVariable) is { } home
            ? Path.Combine(home, ".local", "share")
            : string.Empty;
    }
}
