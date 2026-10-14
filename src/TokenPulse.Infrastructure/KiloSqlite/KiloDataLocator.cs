using TokenPulse.Infrastructure.Jsonl;

namespace TokenPulse.Infrastructure.KiloSqlite;

/// <summary>
/// Kilo 数据目录定位（I-017，逐条移植蓝本 ../ccusage/rust/adapters/kilo/src/paths.rs；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// KILO_DATA_DIR（逗号分隔多目录，去空白、仅保留真实存在的目录、按序去重；**设置即只用配置目录**，
/// 无效/为空不回退默认）→ 默认 &lt;home&gt;/.local/share/kilo（目录必须存在，否则空列表）；
/// 库固定为 &lt;目录&gt;/kilo.db。用户主目录沿用仓库共享 JsonlHomePath 约定（USERPROFILE 优先于 HOME；
/// 蓝本 home.rs 为 HOME 优先——I-006 系已登记差异）；`~` 展开 / GetFullPath 为仓库定位器共有增强。
/// 环境变量经可注入访问器读取，测试不依赖真实用户目录。
/// </summary>
public class KiloDataLocator
{
    /// <summary>Kilo 数据目录环境变量（蓝本 KILO_DATA_DIR_ENV）。</summary>
    public const string DataDirEnvironmentVariable = "KILO_DATA_DIR";

    /// <summary>库文件名（蓝本 KILO_DB_FILE_NAME）。</summary>
    public const string DatabaseFileName = "kilo.db";

    private readonly Func<string, string?> _getEnvironmentVariable;

    public KiloDataLocator(Func<string, string?>? environmentAccessor = null)
    {
        _getEnvironmentVariable = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>解析参与扫描的 Kilo 数据目录（按配置顺序去重，只保留真实存在的目录）。</summary>
    public IReadOnlyList<string> ResolveDataDirectories(string? configuredDataDir)
    {
        // 蓝本语义：env::var 成功（环境变量已设置，任意值）即只用配置目录并返回（即使为空也不回退）。
        var raw = configuredDataDir ?? _getEnvironmentVariable(DataDirEnvironmentVariable);
        if (raw is not null)
        {
            return ResolveConfiguredDirectories(raw);
        }

        return ResolveDefaultDirectories();
    }

    /// <summary>KILO_DATA_DIR 口径：逗号分隔、去空白、仅保留存在的目录、按序去重；设置即不回退默认。</summary>
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

    /// <summary>默认口径：&lt;home&gt;/.local/share/kilo（目录不存在即空列表，不回退）。</summary>
    private List<string> ResolveDefaultDirectories()
    {
        return JsonlHomePath.GetHomeDir(_getEnvironmentVariable) is { } home
               && Directory.Exists(Path.Combine(home, ".local", "share", "kilo"))
            ? [Path.Combine(home, ".local", "share", "kilo")]
            : [];
    }
}
