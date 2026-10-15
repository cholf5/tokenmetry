using System.Text;

namespace Tokenmetry.Core.Models;

/// <summary>
/// 模型 ID 约定（I-035）：小写字母 / 数字，段间单个连字符——<c>^[a-z0-9]+(?:-[a-z0-9]+)*$</c>。
/// 这是模型目录里用户可见、可手填的稳定主键：显示名可改、ID 不可改（改 ID 等于换一个模型，
/// 用户已配的关联会全丢）。
/// <list type="bullet">
/// <item><see cref="Normalize"/> 把导入来源的键折叠成该约定：models.dev 的模型 id 带点号
/// （<c>gpt-5.6-sol</c>）与斜杠（provider 前缀），统一折成连字符；大小写折叠到小写。</item>
/// <item>归一化只做"字符级收敛"，不做任何模糊匹配——跨 ID 的近似合并永远由用户显式关联
/// （对齐项目"宁可分开不猜"的口径纪律）。</item>
/// </list>
/// </summary>
public static class ModelId
{
    /// <summary>点号 / 下划线 / 空格 / 斜杠 / @ 折叠为连字符（与 <c>PricingModelMatcher</c> 的键归一化同源口径）。</summary>
    private static readonly char[] FoldedSeparators = ['.', '_', '@', ' ', '/', '\\', ':'];

    /// <summary>ID 是否合约定（空串 / 大写 / 连续或首尾连字符 / 含其它字符均不合规）。</summary>
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var previousWasHyphen = false;
        for (var index = 0; index < id.Length; index++)
        {
            var ch = id[index];
            if (ch == '-')
            {
                if (index == 0 || previousWasHyphen || index == id.Length - 1)
                {
                    return false;
                }

                previousWasHyphen = true;
                continue;
            }

            if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }

            previousWasHyphen = false;
        }

        return true;
    }

    /// <summary>
    /// 把任意模型名 / 来源键折叠为合约定的 ID：小写、分隔符折连字符、折叠产生的连续与首尾连字符削平。
    /// 结果可能为空串（输入全为分隔符）——调用方按 <see cref="IsValid"/> 判定。
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            var folded = Array.IndexOf(FoldedSeparators, ch) >= 0
                ? '-'
                : char.ToLowerInvariant(ch);
            if (folded == '-')
            {
                // 连续分隔符折叠成一个连字符；首个字符是分隔符时跳过（削首）。
                if (builder.Length == 0 || builder[^1] == '-')
                {
                    continue;
                }
            }

            builder.Append(folded);
        }

        // 削尾：折叠在末尾产生的连字符不是段分隔符。
        while (builder.Length > 0 && builder[^1] == '-')
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}
