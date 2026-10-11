using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.CopilotSession;

/// <summary>
/// Copilot CLI 数据目录定位（蓝本 ../ccusage/rust/adapters/copilot/src/paths.rs，I-008；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 根目录：configuredHome（测试/调用方钉定）→ COPILOT_HOME 环境变量（去首尾空白，非空才生效）→
///   用户主目录下的 .copilot。用户主目录沿用仓库共享 <see cref="JsonlHomePath"/> 约定
///   （USERPROFILE 优先于 HOME；注意蓝本 ccusage-core/home.rs 为 HOME 优先——常规环境两者一致，
///   仅当两个环境变量指向不同目录时才有可观察区别）；
/// - 数据文件：session-state/&lt;session&gt;/events.jsonl——仅 session-state 的**直接子目录**，
///   不递归（蓝本 fs::read_dir + file_type.is_dir() 口径），嵌套目录不发现；
/// - 文件清单按完整路径排序（蓝本 paths() 同口径），保证解析与产出顺序确定。
/// otel/ 目录与 COPILOT_OTEL_FILE_EXPORTER_PATH 属 OpenTelemetry 合并范围（I-008 进阶验收，
/// 本卡未实现，见卡片 Development Log 实现边界），此定位器只覆盖 session-state 主线。
/// </summary>
public class CopilotLogLocator
{
    /// <summary>Copilot 数据根目录环境变量（蓝本 paths.rs COPILOT_HOME_ENV）。</summary>
    public const string CopilotHomeEnvVariable = "COPILOT_HOME";

    private readonly Func<string, string?> _environmentAccessor;

    public CopilotLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析 Copilot 数据根目录；无法解析（无 configuredHome、无 COPILOT_HOME、无用户主目录）返回 null。
    /// </summary>
    public string? ResolveHome(string? configuredHome)
    {
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return configuredHome;
        }

        var fromEnvironment = _environmentAccessor(CopilotHomeEnvVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        return home is null ? null : Path.Combine(home, ".copilot");
    }

    /// <summary>
    /// 收集 session-state 下各会话目录的 events.jsonl（仅一级会话目录，不递归），按路径排序。
    /// </summary>
    public IReadOnlyList<string> CollectSessionStateFiles(string? configuredHome)
    {
        var files = new List<string>();
        var root = ResolveHome(configuredHome);
        if (root is null)
        {
            return files;
        }

        var sessionStateDirectory = Path.Combine(root, "session-state");
        if (!Directory.Exists(sessionStateDirectory))
        {
            return files;
        }

        foreach (var sessionDirectory in Directory.EnumerateDirectories(sessionStateDirectory))
        {
            var eventsFile = Path.Combine(sessionDirectory, "events.jsonl");
            if (File.Exists(eventsFile))
            {
                files.Add(eventsFile);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
