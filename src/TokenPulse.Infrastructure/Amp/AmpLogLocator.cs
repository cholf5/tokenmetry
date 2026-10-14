using TokenPulse.Infrastructure.Jsonl;
using TokenPulse.Infrastructure.JsonSource;

namespace TokenPulse.Infrastructure.Amp;

/// <summary>
/// Amp 数据目录定位（蓝本 ../ccusage/rust/adapters/amp/src/paths.rs 逐条移植，I-014；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// - 根目录：configuredDataDir（测试/调用方钉定，仓库共有增强，非蓝本）→ AMP_DATA_DIR 环境变量
///   （<b>已设置即只用配置</b>，含空串——Rust env::var 对空串返回 Ok，空串目录项被丢弃后返回空列表，
///   不回退默认）→ 用户主目录下 .local/share/amp（目录必须实际存在，不存在即无数据）；
///   环境变量语义：逗号分隔、逐项去首尾空白、空段丢弃、仅保留实际存在的目录、按出现序去重；
/// - 数据文件：&lt;根&gt;/threads 子目录树下递归收集 .json（单层 JSON 线程文件，非 JSONL；发现由适配器
///   完成，蓝本 loader.rs 同结构）。
/// </summary>
public class AmpLogLocator
{
    /// <summary>Amp 数据目录环境变量（蓝本 paths.rs AMP_DATA_DIR_ENV）。</summary>
    public const string AmpDataDirEnvVariable = "AMP_DATA_DIR";

    private readonly Func<string, string?> _environmentAccessor;

    public AmpLogLocator(Func<string, string?>? environmentAccessor = null)
    {
        _environmentAccessor = environmentAccessor ?? (name => Environment.GetEnvironmentVariable(name));
    }

    /// <summary>
    /// 解析数据根目录列表：configuredDataDir 非空即生效（目录可以不存在，与 Copilot configuredHome
    /// 同款仓库增强）；否则 AMP_DATA_DIR 已设置即只用配置（可为空列表、不回退）；否则默认
    /// ~/.local/share/amp（不存在即空）。
    /// </summary>
    public IReadOnlyList<string> ResolveDataDirectories(string? configuredDataDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
        {
            return [configuredDataDir];
        }

        var fromEnvironment = EnvDataDirResolver.Resolve(_environmentAccessor, AmpDataDirEnvVariable);
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        var home = JsonlHomePath.GetHomeDir(_environmentAccessor);
        if (home is null)
        {
            return [];
        }

        var defaultDirectory = Path.Combine(home, ".local", "share", "amp");
        return Directory.Exists(defaultDirectory) ? [defaultDirectory] : [];
    }

    /// <summary>收集各根目录 threads 子树下全部 .json 线程文件，按完整路径排序（确定性增强，见卡片偏差④）。</summary>
    public IReadOnlyList<string> CollectThreadFiles(string? configuredDataDir)
    {
        var files = new List<string>();
        foreach (var root in ResolveDataDirectories(configuredDataDir))
        {
            SourceFileCollector.Collect(Path.Combine(root, "threads"), "json", files);
        }

        return files
            .OrderBy(path => path, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
