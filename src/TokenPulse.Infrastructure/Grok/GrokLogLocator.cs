using TokenPulse.Infrastructure.Jsonl;
using TokenPulse.Infrastructure.JsonSource;

namespace TokenPulse.Infrastructure.Grok;

/// <summary>单个 Grok 会话：updates.jsonl 路径 + 可选的兄弟 summary.json（蓝本 GrokSessionFiles）。</summary>
public sealed record GrokSessionFiles(string Updates, string? Summary);

/// <summary>
/// Grok 数据目录定位（蓝本 ../ccusage/rust/adapters/grok/src/paths.rs 逐条移植，I-022；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 根目录：GROK_HOME 环境变量（<b>trim 后非空</b>才生效——与 pi 蓝本同款空串回退默认；单根，
///   必须实际存在且为目录）→ ~/.grok（存在才保留）；
/// - 会话发现（蓝本 discover_session_files）：&lt;root&gt;/sessions 子树递归收集 .jsonl，仅保留文件名
///   恰为 updates.jsonl 者；每个 updates 的兄弟 summary.json（存在为文件时）一并携带；
///   按完整路径排序（蓝本 files.sort_by，加载序跨运行稳定）。configuredDataDir 参数为仓库共有测试
///   钉定增强（非蓝本）。logs/unified.jsonl 等非 updates 命名的 jsonl 一律不发现。
/// </summary>
public class GrokLogLocator
{
    /// <summary>Grok home 环境变量（蓝本 paths.rs GROK_HOME_ENV）。</summary>
    public const string GrokHomeEnvVariable = "GROK_HOME";

    /// <summary>会话文件名（蓝本 discover_session_files 的 retain 条件）。</summary>
    public const string UpdatesFileName = "updates.jsonl";

    /// <summary>会话元数据文件名（蓝本 sibling_summary）。</summary>
    public const string SummaryFileName = "summary.json";

    private readonly Func<string, string?> _environmentAccessor;

    public GrokLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>解析数据根目录（蓝本 resolve_root）：configuredDataDir 非空（trim 后）即生效（必须为
    /// 存在目录）；否则 GROK_HOME trim 非空才生效；否则 ~/.grok（存在才保留）。不可得返回 null。</summary>
    public string? ResolveRoot(string? configuredDataDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return Directory.Exists(configuredDataDir) ? configuredDataDir : null;
        }

        var fromEnvironment = _environmentAccessor(GrokHomeEnvVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var trimmed = fromEnvironment.Trim();
            return Directory.Exists(trimmed) ? trimmed : null;
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        if (home is null)
        {
            return null;
        }

        var defaultRoot = Path.Combine(home, ".grok");
        return Directory.Exists(defaultRoot) ? defaultRoot : null;
    }

    /// <summary>发现全部会话文件（蓝本 discover_session_files）：sessions 子树递归 .jsonl 过滤
    /// updates.jsonl + 兄弟 summary.json，按完整路径排序。</summary>
    public IReadOnlyList<GrokSessionFiles> CollectSessionFiles(string? configuredDataDir)
    {
        var files = new List<GrokSessionFiles>();
        var root = ResolveRoot(configuredDataDir);
        if (root is not null)
        {
            var sessionsDirectory = Path.Combine(root, "sessions");
            if (Directory.Exists(sessionsDirectory))
            {
                var candidates = new List<string>();
                SourceFileCollector.Collect(sessionsDirectory, "jsonl", candidates);
                foreach (var candidate in candidates)
                {
                    if (string.Equals(Path.GetFileName(candidate), UpdatesFileName, StringComparison.Ordinal))
                    {
                        files.Add(new GrokSessionFiles(candidate, SiblingSummary(candidate)));
                    }
                }
            }
        }

        return files
            .OrderBy(file => file.Updates, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>兄弟 summary.json（蓝本 sibling_summary）：存在且为文件才携带。</summary>
    public static string? SiblingSummary(string updatesPath)
    {
        var summary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(updatesPath))!, SummaryFileName);
        return File.Exists(summary) ? summary : null;
    }
}
