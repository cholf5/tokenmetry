using System;
using Avalonia.Controls;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 图表悬浮提示的统一装饰与文案口径（B-006）。
///
/// 问题：Semi.Avalonia 12.1 的 ToolTip 配色与文本错配且不随本应用令牌走
/// （Light = 浅底白字、Dark = 深底黑字，两档都近乎不可读；源自 SemiColorTertiaryActive +
/// SemiColorBlack/White），呈现为一块"没有样式的白板/黑板"；数值用全量千分位在气泡里也放不下。
///
/// 方案：显式 ToolTip 实例挂 <c>chart-tip</c> 类（SharedStyles.axaml 的令牌化规则：
/// chip 底 + 卡片描边 + 正文文字色，随明暗翻转），文案 token 数用
/// <see cref="TrendChart.FormatCompactTokens"/>（随 UI 语言的万/亿或 K/M 口径）。
/// ScottPlot 图内 tooltip（TrendChart）不使用本类——它在库内自绘，走
/// <see cref="TrendChart.FormatCompactTooltipValue"/>（恒 ASCII）与 ApplyTooltipChrome 同源令牌。
/// </summary>
public static class ChartTooltips
{
    /// <summary>给宿主控件挂一个按 chart-tip 类装饰的文本 tooltip。</summary>
    public static void Set(Control host, string text)
    {
        ArgumentNullException.ThrowIfNull(host);

        var tooltip = new ToolTip { Content = text };
        tooltip.Classes.Add(ChartTipClass);
        ToolTip.SetTip(host, tooltip);
    }

    /// <summary>chart-tip 类名（SharedStyles.axaml 消费）；挂类与否是主题化样式生效的开关。</summary>
    public const string ChartTipClass = "chart-tip";
}
