using System;
using System.Globalization;

namespace Tokenmetry.App.Services;

/// <summary>
/// 计价器金额展示格式（I-CALC）：≥$1 两位小数、≥1 美分四位、更小六位——估算器要能在
/// 「20 亿 tokens × 便宜模型」的量级下仍读得出非零数；分组符随当前文化。单价统一带 /M 尾缀。
/// </summary>
public static class UsdCostFormatter
{
    public static string Format(decimal usd)
    {
        var abs = Math.Abs(usd);
        // 0 不是「太小」，就是零：固定两位，别渲染成 $0.000000。
        var decimals = usd == 0m || abs >= 1m ? 2 : abs >= 0.01m ? 4 : 6;
        return "$" + usd.ToString($"N{decimals}", CultureInfo.CurrentCulture);
    }

    public static string FormatPerMillion(decimal pricePerMillion)
    {
        return Format(pricePerMillion) + "/M";
    }
}
