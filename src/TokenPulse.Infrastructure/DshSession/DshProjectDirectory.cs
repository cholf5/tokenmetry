using System.Globalization;
using System.Text;

namespace TokenPulse.Infrastructure.DshSession;

/// <summary>
/// DSH 项目目录名编解码（I-007，对应 DSH session-persistence-jsonl/src/format.ts 的 projectKey）：
/// 编码把 / \ : 分隔（连续分隔折叠成一个 '-'）替换为 '-'，安全字符 [A-Za-z0-9._-] 原样保留，
/// 其余码元（含 '~'）转义为 "~XXXX"（大写十六进制），去前导 '-' 后以 "--…--" 包裹并截断到 251 字符。
/// 编码是有意的有损映射（分隔符与段内 '-' 不可区分），只能尽力还原：~XXXX 转义精确还原，
/// '-' 保持原样（不猜测是否为路径分隔）。非 "--…--" 形态（如 _no-cwd）返回 null。
/// </summary>
public static class DshProjectDirectory
{
    public static string? DecodeProjectKey(string? directoryName)
    {
        if (directoryName is null
            || directoryName.Length < 5
            || !directoryName.StartsWith("--", StringComparison.Ordinal)
            || !directoryName.EndsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        var slug = directoryName[2..^2];
        if (slug.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(slug.Length);
        for (var index = 0; index < slug.Length; index++)
        {
            var ch = slug[index];
            if (ch == '~')
            {
                if (index + 4 >= slug.Length
                    || !int.TryParse(slug.AsSpan(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
                    || code > 0xFFFF)
                {
                    // 转义序列损坏：保留原样，尽力还原不抛错。
                    builder.Append(ch);
                    continue;
                }

                builder.Append((char)code);
                index += 4;
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
