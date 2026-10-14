using TokenPulse.Infrastructure.Jsonl;
using TokenPulse.Infrastructure.JsonSource;

namespace TokenPulse.Infrastructure.OpenClaw;

/// <summary>
/// OpenClaw 数据目录定位（蓝本 ../ccusage/rust/adapters/openclaw/src/paths.rs 逐条移植，I-020；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 根目录：configuredDataDir / OPENCLAW_DIR 环境变量（<b>trim 后非空</b>才生效——env 已设置但
///   trim 为空时<b>回退默认四根</b>，与 Kimi/Gemini 的"空串即配置生效"不同，蓝本 env 分支条件含
///   <c>!trim().is_empty()</c> 同口径；逗号分隔 / 逐段去空白 / 仅保留实际存在的目录 / 按序去重）
///   → 用户主目录下 .openclaw、.clawdbot、.moltbot、.moldbot <b>四个</b>目录（存在者保留，按序）；
/// - 会话文件（蓝本 collect_session_files）：<b>整根递归</b>（非 sessions/ 子树限定），符号链接不跟随
///   （蓝本 file_type().is_symlink() 同口径），文件名含 ".jsonl" 后缀且后缀恰为 ".jsonl" 或以
///   ".jsonl.deleted." / ".jsonl.reset." 开头（归档 / 重置会话）；每个根内按完整路径排序
///   （蓝本 files.sort()），根间按根序拼接；
/// - agent 库（蓝本 sqlite.rs collect_agent_databases）：&lt;root&gt;/agents/&lt;agentId&gt;/agent/
///   openclaw-agent.sqlite，agents/ 与 agent/ 目录本身、库文件均不为符号链接，按路径排序。
/// 环境变量经可注入访问器读取，测试不依赖真实用户目录。
/// </summary>
public class OpenClawLogLocator
{
    /// <summary>OpenClaw 数据目录环境变量（蓝本 paths.rs OPENCLAW_DIR_ENV）。</summary>
    public const string OpenClawDirEnvVariable = "OPENCLAW_DIR";

    /// <summary>agent 库文件名（蓝本 sqlite.rs openclaw-agent.sqlite）。</summary>
    public const string DatabaseFileName = "openclaw-agent.sqlite";

    /// <summary>默认主目录候选（蓝本 paths.rs 默认分支的四目录，按序探测）。</summary>
    public static readonly string[] DefaultHomeDirectories = [".openclaw", ".clawdbot", ".moltbot", ".moldbot"];

    private readonly Func<string, string?> _environmentAccessor;

    public OpenClawLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析数据根目录列表（蓝本 paths）：configuredDataDir trim 非空即生效；否则 OPENCLAW_DIR
    /// trim 非空才生效（**设置但空白回退默认四根**）；否则主目录下存在者（按序）。
    /// </summary>
    public IReadOnlyList<string> ResolveDataDirectories(string? configuredDataDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return ExistingPathList(configuredDataDir);
        }

        var fromEnvironment = _environmentAccessor(OpenClawDirEnvVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return ExistingPathList(fromEnvironment);
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        if (home is null)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var directory in DefaultHomeDirectories)
        {
            var path = Path.Combine(home, directory);
            if (Directory.Exists(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// 收集单个根下的全部会话 JSONL（蓝本 collect_session_files）：整根递归、跳过符号链接，
    /// 文件名后缀判定 <see cref="IsOpenClawSessionFile"/>，按完整路径排序。
    /// </summary>
    public IReadOnlyList<string> CollectSessionFiles(string root)
    {
        var files = new List<string>();
        CollectSessionFilesInner(root, files);
        return files.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 会话文件名判定（蓝本 is_openclaw_session_file）：首个 ".jsonl" 起的后缀恰为 ".jsonl"
    /// 或以 ".jsonl.deleted." / ".jsonl.reset." 开头（含 ".jsonl" 子串但后缀不匹配者不命中）。
    /// </summary>
    public static bool IsOpenClawSessionFile(string name)
    {
        var index = name.IndexOf(".jsonl", StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var suffix = name[index..];
        return suffix == ".jsonl"
               || suffix.StartsWith(".jsonl.deleted.", StringComparison.Ordinal)
               || suffix.StartsWith(".jsonl.reset.", StringComparison.Ordinal);
    }

    /// <summary>
    /// 会话 id（蓝本 extract_session_id）：文件名首个 ".jsonl" 之前的部分；无 ".jsonl" 或主干为空
    /// 时回退整个文件名（C# 从完整路径取文件名恒非空，"unknown" 分支不可达，保真保留）。
    /// </summary>
    public static string SessionIdFromFileName(string sourceFile)
    {
        var filename = Path.GetFileName(sourceFile);
        if (string.IsNullOrEmpty(filename))
        {
            return "unknown";
        }

        var index = filename.IndexOf(".jsonl", StringComparison.Ordinal);
        if (index < 0)
        {
            return filename;
        }

        var stem = filename[..index];
        return stem.Length == 0 ? filename : stem;
    }

    /// <summary>
    /// 蓝本 existing_path_list：逗号分隔、逐段去首尾空白、空段丢弃、仅保留实际存在的目录、按出现序
    /// 去重（HashSet insert 同口径；仓库定位器共有增强：~ 展开 + GetFullPath 规范化后去重，
    /// 蓝本按原文 PathBuf 去重——"./x" 与 "x" 在蓝本为两条、此处折叠为一条，已登记偏差⑥）。
    /// </summary>
    private List<string> ExistingPathList(string raw)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in raw.Split(','))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var directory = Path.GetFullPath(JsonlHomePath.ExpandHomePath(trimmed, _environmentAccessor));
            if (Directory.Exists(directory) && seen.Add(directory))
            {
                paths.Add(directory);
            }
        }

        return paths;
    }

    /// <summary>
    /// 整根递归收集（蓝本 collect_session_files_inner）：符号链接（reparse point）不跟随；
    /// 读不了的目录静默跳过（蓝本此处错误传播致整次加载失败——桌面采集按仓库 I-004 口径
    /// 静默跳过，见卡片偏差清单）。
    /// </summary>
    private static void CollectSessionFilesInner(string path, List<string> files)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            return;
        }

        FileSystemInfo[] entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if (entry is DirectoryInfo subdirectory)
            {
                CollectSessionFilesInner(subdirectory.FullName, files);
            }
            else if (entry is FileInfo && IsOpenClawSessionFile(entry.Name))
            {
                files.Add(entry.FullName);
            }
        }
    }
}
