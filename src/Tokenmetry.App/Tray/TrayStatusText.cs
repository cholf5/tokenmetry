using System;
using System.Globalization;

namespace Tokenmetry.App.Tray;

/// <summary>
/// 紧凑 tokens+成本格式化（D-014 关卡后保留，D-015 弹层复用）：纯数据文本，无词可译——沿
/// 用户原型（docs/design/tokenmetry_statusbar_prototype.html 底部 tray chip）的 ASCII 口径
/// <c>42.4K · $12.46</c>，不随 UI 语言换单位制。各语种原生大数口径仍归图表
/// （TrendChart.FormatCompactTokens），紧凑 chip 与图表的口径差异是原型自身的选择。
/// <para>尾数精度与越档守卫对齐图表 FormatCompact 的阶梯规则（≥1000 取整、≥100 一位小数、
/// 其余两位；取整越档升入上一档），单位阶梯取千进制 K/M/B/T。</para>
/// <para>D-014 状态栏直显经真机取证不可行（Avalonia 方形状态项中段裁剪宽位图，卡内 B1），
/// 状态栏回落模板图标；本格式化器及测试原样保留。</para>
/// </summary>
internal static class TrayStatusText
{
    /// <summary>成本 1000 以内保持两位小数（与菜单首行/对账口径一致），达到 1000 起进紧凑阶梯。</summary>
    private const decimal CompactCostThreshold = 1000m;

    private static readonly (double Threshold, double Divisor, string Unit)[] Units =
    [
        (1e3, 1e3, "K"),
        (1e6, 1e6, "M"),
        (1e9, 1e9, "B"),
        (1e12, 1e12, "T"),
    ];

    /// <summary>有数据时的状态栏文本：紧凑 tokens · 成本。</summary>
    public static string Build(decimal todayCostUsd, long todayTokens)
        => $"{FormatTokens(todayTokens)} · {FormatCost(todayCostUsd)}";

    public static string FormatTokens(long tokens)
        => tokens < 1000 ? tokens.ToString(CultureInfo.InvariantCulture) : Compact(tokens);
    public static string FormatCost(decimal costUsd)
        => costUsd < CompactCostThreshold
            ? $"${costUsd.ToString("F2", CultureInfo.InvariantCulture)}"
            : $"${Compact(costUsd)}";

    /// <summary>千进制紧凑阶梯；调用方保证 value 已达进位阈值，越档守卫与图表 FormatCompact 同构。</summary>
    private static string Compact(decimal value)
    {
        var abs = Math.Abs((double)value);
        var index = Array.FindLastIndex(Units, unit => abs >= unit.Threshold);
        if (index < 0)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        // 尾数取整达到本档上限（999500 → "1000K"）时升入上一档再缩放；阶梯末端（T）不设限。
        if (index + 1 < Units.Length && Math.Round(abs / Units[index].Divisor) >= 1e3)
        {
            index++;
        }

        var scaled = (double)value / Units[index].Divisor;
        var format = Math.Abs(scaled) >= 1000 ? "0"
            : Math.Abs(scaled) >= 100 ? "0.#"
            : "0.##";
        return scaled.ToString(format, CultureInfo.InvariantCulture) + Units[index].Unit;
    }
}
