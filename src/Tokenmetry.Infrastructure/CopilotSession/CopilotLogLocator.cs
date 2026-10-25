using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.CopilotSession;

/// <summary>
/// Copilot CLI 数据目录定位（蓝本 ../ccusage/rust/adapters/copilot/src/paths.rs，I-008；
/// otel 数据源补齐 I-023。蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 根目录：configuredHome（测试/调用方钉定）→ COPILOT_HOME 环境变量（去首尾空白，非空才生效）→
///   用户主目录下的 .copilot。用户主目录沿用仓库共享 <see cref="JsonlHomePath"/> 约定
///   （USERPROFILE 优先于 HOME；注意蓝本 ccusage-core/home.rs 为 HOME 优先——常规环境两者一致，
///   仅当两个环境变量指向不同目录时才有可观察区别）；
/// - session-state：&lt;root&gt;/session-state/&lt;session&gt;/events.jsonl——仅 session-state 的
///   **直接子目录**，不递归（蓝本 fs::read_dir + file_type.is_dir() 口径），嵌套目录不发现；
/// - otel（I-023）：&lt;root&gt;/otel/ 下全部 *.jsonl **递归**收集（蓝本 collect_files_with_extension，
///   扩展名 Ordinal 精确匹配），加 <see cref="OtelExporterPathEnvVariable"/>（单文件，非空去空白且
///   确为文件才生效，蓝本 copilot_exporter_path 口径）；
/// - 文件清单按完整路径排序并去重（蓝本 paths() 末尾 sort_by + HashSet seen 同口径——otel/ 递归
///   与单文件导出可能指向同一路径），保证解析与产出顺序确定。
/// </summary>
public class CopilotLogLocator
{
    /// <summary>Copilot 数据根目录环境变量（蓝本 paths.rs COPILOT_HOME_ENV）。</summary>
    public const string CopilotHomeEnvVariable = "COPILOT_HOME";

    /// <summary>Copilot OpenTelemetry 单文件导出路径环境变量（蓝本 paths.rs COPILOT_OTEL_FILE_EXPORTER_PATH_ENV）。</summary>
    public const string OtelExporterPathEnvVariable = "COPILOT_OTEL_FILE_EXPORTER_PATH";

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
    /// 收集全部数据源文件（session-state + otel，蓝本 paths()）：otel/ 递归 *.jsonl +
    /// session-state 一级会话目录的 events.jsonl + COPILOT_OTEL_FILE_EXPORTER_PATH 单文件，
    /// 按路径去重并排序（蓝本 add_file seen + 末尾 sort_by 同口径）。
    /// </summary>
    public IReadOnlyList<CopilotSourceFile> CollectSourceFiles(string? configuredHome)
    {
        var files = new List<CopilotSourceFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var root = ResolveHome(configuredHome);
        if (root is not null)
        {
            var otelDirectory = Path.Combine(root, "otel");
            if (Directory.Exists(otelDirectory))
            {
                foreach (var path in CollectFilesWithJsonlExtension(otelDirectory))
                {
                    AddFile(files, seen, new CopilotSourceFile(path, CopilotSourceKind.Otel));
                }
            }

            var sessionStateDirectory = Path.Combine(root, "session-state");
            if (Directory.Exists(sessionStateDirectory))
            {
                foreach (var sessionDirectory in Directory.EnumerateDirectories(sessionStateDirectory))
                {
                    var eventsFile = Path.Combine(sessionDirectory, "events.jsonl");
                    if (File.Exists(eventsFile))
                    {
                        AddFile(files, seen, new CopilotSourceFile(eventsFile, CopilotSourceKind.SessionState));
                    }
                }
            }
        }

        // 蓝本 copilot_exporter_path：环境变量非空（去空白）且指向既有文件才生效；
        // 与 otel/ 目录可能重复，add_file 的 seen 去重保证单次消费。
        var exporterPath = _environmentAccessor(OtelExporterPathEnvVariable)?.Trim();
        if (!string.IsNullOrEmpty(exporterPath) && File.Exists(exporterPath))
        {
            AddFile(files, seen, new CopilotSourceFile(exporterPath, CopilotSourceKind.Otel));
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return files;
    }

    /// <summary>
    /// 收集 session-state 下各会话目录的 events.jsonl（仅一级会话目录，不递归），按路径排序。
    /// 保留为数据存在性门控 / 测试便捷入口（完整口径见 <see cref="CollectSourceFiles"/>）。
    /// </summary>
    public IReadOnlyList<string> CollectSessionStateFiles(string? configuredHome)
        => CollectSourceFiles(configuredHome)
            .Where(file => file.Kind == CopilotSourceKind.SessionState)
            .Select(file => file.Path)
            .ToList();

    /// <summary>
    /// 蓝本 collect_files_with_extension：递归收集扩展名恰为 jsonl（Ordinal）的文件；
    /// 读不到的子目录静默跳过（蓝本 fs::read_dir else return 同口径）；reparse point 一律不跟随
    /// （与共享收集器同口径，B-011）。
    /// </summary>
    private static IEnumerable<string> CollectFilesWithJsonlExtension(string directory)
    {
        var files = new List<string>();
        CollectFilesWithJsonlExtensionCore(directory, files);
        return files;
    }

    private static void CollectFilesWithJsonlExtensionCore(string directory, List<string> files)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            bool isDirectory;
            try
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    // 与共享收集器一致：符号链接（reparse point）不跟随（junction 成环无限递归 /
                    // 不成环重复发现，B-011）；枚举与 stat 间条目消失按读不了条目跳过。
                    continue;
                }

                isDirectory = Directory.Exists(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!isDirectory && Path.GetExtension(entry) is { Length: > 1 } extension
                && string.CompareOrdinal(extension[1..], "jsonl") == 0)
            {
                files.Add(entry);
            }
            else if (isDirectory)
            {
                CollectFilesWithJsonlExtensionCore(entry, files);
            }
        }
    }

    private static void AddFile(List<CopilotSourceFile> files, HashSet<string> seen, CopilotSourceFile file)
    {
        if (seen.Add(file.Path))
        {
            files.Add(file);
        }
    }
}

/// <summary>Copilot 数据源种类（蓝本 paths.rs CopilotSourceKind）。</summary>
public enum CopilotSourceKind
{
    /// <summary>session-state/&lt;session&gt;/events.jsonl（累计 shutdown 快照 + 积分快照）。</summary>
    SessionState,

    /// <summary>otel/ 递归 *.jsonl 与 COPILOT_OTEL_FILE_EXPORTER_PATH 单文件（逐调用用量行）。</summary>
    Otel,
}

/// <summary>一个 Copilot 数据源文件（蓝本 paths.rs CopilotSourceFile）。</summary>
public readonly record struct CopilotSourceFile(string Path, CopilotSourceKind Kind);
