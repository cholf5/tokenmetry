using System;
using System.Globalization;

namespace Tokenmetry.App.Services;

/// <summary>
/// 计价器输入解析（I-CALC，独立工具）：像系统计算器一样随手敲——token 用量支持纯数字、
/// 千分位逗号（含全角「，」）、e 记法（2e9）与数量级后缀（万 / 亿 / k / M / B，大小写同义，
/// M=百万、B=十亿），「20亿」不必敲九个零；命中率按百分比读（80 / 80% / 80.5％）。
/// 数字统一按 InvariantCulture 解析（小数点恒为「.」，不随界面语言漂移）；
/// 负数不是合法用量/单价，一律拒绝。
/// </summary>
public static class TokenAmountTextParser
{
    /// <summary>解析 token 用量。空串/纯空白不算成功（调用方按需把空输入当 0）；超 long 域拒绝。</summary>
    public static bool TryParseTokens(string? text, out long tokens)
    {
        tokens = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!TryParseAmount(text, out var value))
        {
            return false;
        }

        if (value > long.MaxValue)
        {
            return false;
        }

        tokens = (long)Math.Round(value, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>
    /// 解析缓存命中率（百分比）：空串视为 0（未填 = 无缓存命中，不算错）；"80" / "80%" /
    /// "80.5％" 同义；钳到 0–100%，返回 0–1 小数（<see cref="ModelCostCalculator"/> 口径）。
    /// </summary>
    public static bool TryParsePercent(string? text, out decimal fraction)
    {
        fraction = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var cleaned = text.Trim();
        if (cleaned.EndsWith('%') || cleaned.EndsWith('％'))
        {
            cleaned = cleaned[..^1].TrimEnd();
        }

        if (!decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            || percent < 0m)
        {
            return false;
        }

        fraction = Math.Clamp(percent, 0m, 100m) / 100m;
        return true;
    }

    /// <summary>
    /// 解析单价（USD / 百万 tokens）：非负小数。空串不算成功——调用方需区分「未填」与「填错」
    /// （计价器手动价：输入/输出必填，缓存读可空回落 input × 0.1，与快照解析同规则）。
    /// </summary>
    public static bool TryParsePrice(string? text, out decimal price)
    {
        price = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return TryParseAmount(text, out price);
    }

    /// <summary>纯数字（含千分位逗号 / e 记法 / 数量级后缀）→ 非负 decimal。</summary>
    private static bool TryParseAmount(string text, out decimal value)
    {
        value = 0m;
        var cleaned = text.Trim().Replace(",", string.Empty).Replace("，", string.Empty);
        var magnitude = TakeMagnitudeSuffix(ref cleaned);
        if (!decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0m)
        {
            return false;
        }

        value = parsed * magnitude;
        return true;
    }

    /// <summary>剥一个尾缀数量级（万/亿/k/M/B）；无尾缀返回 1。一元数量级，不允许「2亿k」连缀。</summary>
    private static decimal TakeMagnitudeSuffix(ref string cleaned)
    {
        if (cleaned.Length == 0)
        {
            return 1m;
        }

        var last = cleaned[^1];
        decimal magnitude;
        switch (last)
        {
            case '亿':
                magnitude = 100_000_000m;
                break;
            case '万':
                magnitude = 10_000m;
                break;
            case 'k':
            case 'K':
                magnitude = 1_000m;
                break;
            case 'm':
            case 'M':
                magnitude = 1_000_000m;
                break;
            case 'b':
            case 'B':
                magnitude = 1_000_000_000m;
                break;
            default:
                return 1m;
        }

        var rest = cleaned[..^1].TrimEnd();
        if (rest.Length == 0)
        {
            return 1m; // 只有尾缀（"亿"）不是数，回落按原文解析失败
        }

        cleaned = rest;
        return magnitude;
    }
}
