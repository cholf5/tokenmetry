namespace Tokenmetry.Infrastructure.JsonSource;

/// <summary>
/// 数据目录环境变量的解析（蓝本 adapters/*/src/paths.rs 的 env::var 分支移植，I-010 Gemini /
/// I-014 Amp / I-015 Droid 共用）：变量<b>已设置</b>（含空串——Rust env::var 对空串返回 Ok）即
/// 配置生效、不回退默认目录；逗号分隔、逐项去首尾空白、空段丢弃、仅保留实际存在的目录、按出现序
/// 去重（HashSet insert 同口径，路径不做规范化，蓝本 PathBuf 相等即文本相等）。变量未设置返回 null，
/// 调用方走默认目录分支。
/// </summary>
public static class EnvDataDirResolver
{
    public static List<string>? Resolve(Func<string, string?> environmentAccessor, string variable)
    {
        var raw = environmentAccessor(variable);
        if (raw is null)
        {
            return null;
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in raw.Split(','))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0 || !Directory.Exists(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            paths.Add(trimmed);
        }

        return paths;
    }
}
