using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.HermesSqlite;

/// <summary>
/// Hermes state 库定位（I-016，逐条移植蓝本 ../ccusage/rust/adapters/hermes/src/paths.rs；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi（../ccusage/LICENSE））：
/// HERMES_HOME（逗号分隔多根，去空白、跳过空段）→ 各根下 state.db；未设置时默认
/// &lt;home&gt;/.hermes/state.db；只保留真实存在的库文件并按序去重。
/// 用户主目录沿用仓库共享 JsonlHomePath 约定（USERPROFILE 优先于 HOME；蓝本 home.rs 为 HOME 优先——
/// I-006/I-008/I-012 同款已登记差异）；`~` 展开 / GetFullPath 为仓库定位器共有增强。
/// 环境变量经可注入访问器读取，测试不依赖真实用户目录。
/// </summary>
public class HermesLogLocator
{
    /// <summary>Hermes home 环境变量（蓝本 HERMES_HOME_ENV）。</summary>
    public const string HomeEnvironmentVariable = "HERMES_HOME";

    /// <summary>库文件名（蓝本 state.db）。</summary>
    public const string DatabaseFileName = "state.db";

    private readonly Func<string, string?> _getEnvironmentVariable;

    public HermesLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _getEnvironmentVariable = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析参与扫描的 Hermes state 库（按序去重，只保留真实存在的文件）。
    /// 适配器的权威入口：configuredHome 与蓝本 env::var 同语义——**已传入（含空串）即只用该值**，
    /// 空串拆分为空列表返回空（不回退默认目录，蓝本同口径）；无 home 解析出库时返回空列表。
    /// </summary>
    public IReadOnlyList<string> ResolveDatabases(string? configuredHome)
    {
        var homes = configuredHome is not null
            ? SplitHomes(configuredHome)
            : ResolveHomeCandidates();

        var databases = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var home in homes)
        {
            var database = Path.Combine(home, DatabaseFileName);
            if (File.Exists(database) && seen.Add(database))
            {
                databases.Add(database);
            }
        }

        return databases;
    }

    /// <summary>
    /// HERMES_HOME 候选（蓝本：逗号分隔多根，去空白、跳过空段）。
    /// 注意与 <see cref="ResolveDatabases"/> 的口径差异：本助手把空串视为"未传入"继续走
    /// 环境变量 / 默认根回退（便于测试覆盖默认分支），而 <see cref="ResolveDatabases"/> 以
    /// "configuredHome 参数是否为 null" 判定是否传入——空串即只用该值（对齐蓝本 env::var 已设置
    /// 即不回退）。本助手仅供测试与诊断，适配器扫描走 <see cref="ResolveDatabases"/>。
    /// </summary>
    public IReadOnlyList<string> ResolveHomeCandidates(string? configuredHome = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return SplitHomes(configuredHome);
        }

        var fromEnvironment = _getEnvironmentVariable(HomeEnvironmentVariable);
        if (fromEnvironment is not null)
        {
            return SplitHomes(fromEnvironment);
        }

        return JsonlHomePath.GetHomeDir(_getEnvironmentVariable) is { } home
            ? [Path.Combine(home, ".hermes")]
            : [];
    }

    private List<string> SplitHomes(string raw)
    {
        return raw
            .Split(',')
            .Select(rawPath => rawPath.Trim())
            .Where(rawPath => rawPath.Length > 0)
            .Select(rawPath => Path.GetFullPath(JsonlHomePath.ExpandHomePath(rawPath, _getEnvironmentVariable)))
            .ToList();
    }
}
