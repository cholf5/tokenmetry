using CCUsage.Infrastructure.ClaudeCodeJsonl;
using CCUsage.Infrastructure.CodexJsonl;
using CCUsage.Infrastructure.DshSession;
using CCUsage.Infrastructure.ZCodeSqlite;

namespace CCUsage.Reconciliation.Tests.Support;

/// <summary>
/// 本机真实数据对账（集成测试）的环境探测：
/// - 门控开关：环境变量 CCUSAGE_RECONCILE_REAL=1 显式开启；
/// - 数据存在性：复用产品定位器（与适配器同一套目录发现逻辑）；
/// - CLI 可用性：npx 运行 pinned 版本 ccusage，失败即视为不具备；
/// - DSH 对账（I-007）：golden 来自 ../dsh-usage-stats 蓝本库（Node 运行 Support/DshBlueprintGolden.mjs，
///   直读原始 zstd 字节 + 蓝本 fold），需 Node 可执行文件与蓝本目录（可用 CCUSAGE_DSH_BLUEPRINT 指定，
///   缺省从测试输出目录逐级向上找 dsh-usage-stats）。
/// 任何条件不满足时测试 Skip 而非 Fail（任务卡 TC-002）。
/// </summary>
public static class RealReconciliationEnvironment
{
    public const string GateVariable = "CCUSAGE_RECONCILE_REAL";

    /// <summary>DSH 蓝本目录覆盖环境变量（指向含 lib/usage.js 的 dsh-usage-stats 检出）。</summary>
    public const string BlueprintVariable = "CCUSAGE_DSH_BLUEPRINT";

    /// <summary>golden 录制与真实对账共用的 ccusage 版本（npx 标签，保证两侧行为一致）。</summary>
    public const string CcusageVersion = "20.0.29";

    public static bool IsGateEnabled()
        => Environment.GetEnvironmentVariable(GateVariable) == "1";

    public static bool HasCodexData()
        => new CodexLogLocator().ResolveSources(configuredHome: null).Count > 0;

    public static bool HasClaudeData()
        => new ClaudeCodeLogLocator().ResolveRoots(configuredRoot: null).Count > 0;

    public static bool HasZcodeData()
        => new ZCodeLogLocator().ResolveDatabases(configuredHome: null).Databases.Count > 0;

    public static bool HasDshData()
        => new DshLogLocator().CollectSessionArtifacts(configuredHome: null).Count > 0;

    /// <summary>
    /// dsh-usage-stats 蓝本目录（含 lib/usage.js）：CCUSAGE_DSH_BLUEPRINT 优先，
    /// 否则从测试运行目录逐级向上（含兄弟目录）查找；找不到返回 null（测试 Skip）。
    /// </summary>
    public static string? FindBlueprintDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(BlueprintVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "lib", "usage.js")))
        {
            return configured;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && current is not null; depth++, current = current.Parent)
        {
            var sibling = Path.Combine(current.FullName, "dsh-usage-stats", "lib", "usage.js");
            if (File.Exists(sibling))
            {
                return Path.Combine(current.FullName, "dsh-usage-stats");
            }
        }

        return null;
    }
}
