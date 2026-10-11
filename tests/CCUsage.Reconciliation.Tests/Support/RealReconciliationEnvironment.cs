using CCUsage.Infrastructure.ClaudeCodeJsonl;
using CCUsage.Infrastructure.CodexJsonl;
using CCUsage.Infrastructure.ZCodeSqlite;

namespace CCUsage.Reconciliation.Tests.Support;

/// <summary>
/// 本机真实数据对账（集成测试）的环境探测：
/// - 门控开关：环境变量 CCUSAGE_RECONCILE_REAL=1 显式开启；
/// - 数据存在性：复用产品定位器（与适配器同一套目录发现逻辑）；
/// - CLI 可用性：npx 运行 pinned 版本 ccusage，失败即视为不具备。
/// 任何条件不满足时测试 Skip 而非 Fail（任务卡 TC-002）。
/// </summary>
public static class RealReconciliationEnvironment
{
    public const string GateVariable = "CCUSAGE_RECONCILE_REAL";

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
}
