using Tokenmetry.Infrastructure.Jsonl;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.Droid;

/// <summary>
/// Droid (Factory) 会话目录定位（蓝本 ../ccusage/rust/adapters/droid/src/paths.rs 逐条移植，I-015；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 根目录：configuredDataDir（测试/调用方钉定，仓库共有增强，非蓝本）→ DROID_SESSIONS_DIR 环境变量
///   （<b>已设置即只用配置</b>，含空串——Rust env::var 对空串返回 Ok，空串目录项被丢弃后返回空列表，
///   不回退默认）→ 用户主目录下 .factory/sessions（目录必须实际存在，不存在即无数据）；
///   环境变量语义：逗号分隔、逐项去首尾空白、空段丢弃、仅保留实际存在的目录、按出现序去重；
/// - 数据文件：根目录树下递归收集 .json，仅保留文件名以 <b>.settings.json</b> 结尾的会话快照
///   （蓝本 discover_settings_files 同口径；.jsonl 侧车文件不参与发现，由解析器按名回退读取）。
/// </summary>
public class DroidLogLocator
{
    /// <summary>Droid 会话目录环境变量（蓝本 paths.rs DROID_SESSIONS_DIR_ENV）。</summary>
    public const string DroidSessionsDirEnvVariable = "DROID_SESSIONS_DIR";

    /// <summary>会话快照文件名后缀（蓝本 discover_settings_files 过滤条件）。</summary>
    public const string SettingsFileSuffix = ".settings.json";

    private readonly Func<string, string?> _environmentAccessor;

    public DroidLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析会话根目录列表：configuredDataDir 非空即生效（目录可以不存在，与 Copilot configuredHome
    /// 同款仓库增强）；否则 DROID_SESSIONS_DIR 已设置即只用配置（可为空列表、不回退）；否则默认
    /// ~/.factory/sessions（不存在即空）。
    /// </summary>
    public IReadOnlyList<string> ResolveDataDirectories(string? configuredDataDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return [configuredDataDir];
        }

        var fromEnvironment = EnvDataDirResolver.Resolve(_environmentAccessor, DroidSessionsDirEnvVariable);
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        if (home is null)
        {
            return [];
        }

        var defaultDirectory = Path.Combine(home, ".factory", "sessions");
        return Directory.Exists(defaultDirectory) ? [defaultDirectory] : [];
    }

    /// <summary>收集根目录树下全部 *.settings.json 会话快照，按完整路径排序（蓝本 files.sort() 同口径）。</summary>
    public IReadOnlyList<string> CollectSettingsFiles(string? configuredDataDir)
    {
        var files = new List<string>();
        foreach (var root in ResolveDataDirectories(configuredDataDir))
        {
            SourceFileCollector.Collect(root, "json", files);
        }

        return files
            .Where(path => Path.GetFileName(path).EndsWith(SettingsFileSuffix, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
