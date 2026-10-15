using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.GeminiCli;

/// <summary>
/// Gemini CLI 数据目录定位（蓝本 ../ccusage/rust/adapters/gemini/src/paths.rs 逐条移植，I-010；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 根目录：configuredDataDir（测试/调用方钉定，仓库共有增强，非蓝本）→ GEMINI_DATA_DIR 环境变量
///   （<b>已设置即只用配置</b>，含空串——Rust env::var 对空串返回 Ok，空串目录项被丢弃后返回空列表，
///   不回退默认）→ 用户主目录下 .gemini/tmp（目录必须实际存在，不存在即无数据）；
///   环境变量语义：逗号分隔、逐项去首尾空白、空段丢弃、仅保留实际存在的目录、按出现序去重；
/// - 数据文件：根目录树下递归收集 <b>.json 与 .jsonl</b> 两种扩展名（精确小写匹配），合并后按完整
///   路径排序 + 去重（蓝本 discover_log_files 同口径）。
/// antigravity 的 SQLite 会话库（ANTIGRAVITY_DATA_DIR / conversations）归 I-021，本定位器只覆盖
/// Gemini CLI 的 chats 数据，两者互不影响（蓝本内联测试 antigravity_override_does_not_replace_
/// gemini_discovery 同场景）。
/// </summary>
public class GeminiLogLocator
{
    /// <summary>Gemini 数据目录环境变量（蓝本 paths.rs GEMINI_DATA_DIR_ENV）。</summary>
    public const string GeminiDataDirEnvVariable = "GEMINI_DATA_DIR";

    private readonly Func<string, string?> _environmentAccessor;

    public GeminiLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析数据根目录列表：configuredDataDir 非空即生效（目录可以不存在，与 Copilot configuredHome
    /// 同款仓库增强）；否则 GEMINI_DATA_DIR 已设置即只用配置（可为空列表、不回退）；否则默认
    /// ~/.gemini/tmp（不存在即空）。
    /// </summary>
    public IReadOnlyList<string> ResolveDataDirectories(string? configuredDataDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return [configuredDataDir];
        }

        var fromEnvironment = EnvDataDirResolver.Resolve(_environmentAccessor, GeminiDataDirEnvVariable);
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        if (home is null)
        {
            return [];
        }

        var defaultDirectory = Path.Combine(home, ".gemini", "tmp");
        return Directory.Exists(defaultDirectory) ? [defaultDirectory] : [];
    }

    /// <summary>收集根目录树下全部 .json / .jsonl 日志文件，按完整路径排序去重（蓝本同口径）。</summary>
    public IReadOnlyList<string> CollectLogFiles(string? configuredDataDir)
        => CollectLogFiles(ResolveDataDirectories(configuredDataDir));

    /// <summary>收集已解析根目录列表下的全部 .json / .jsonl 日志文件（调用方已做目录解析时避免双跑）。</summary>
    public IReadOnlyList<string> CollectLogFiles(IReadOnlyList<string> roots)
    {
        var files = new List<string>();
        foreach (var root in roots)
        {
            SourceFileCollector.Collect(root, "json", files);
            SourceFileCollector.Collect(root, "jsonl", files);
        }

        return files
            .OrderBy(path => path, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
