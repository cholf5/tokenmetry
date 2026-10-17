using Tokenmetry.App.Tray;

namespace Tokenmetry.App.Tests.Tray;

/// <summary>
/// 紧凑 tokens+成本格式化（D-014 关卡后保留，D-015 弹层复用）：
/// 紧凑口径沿原型 tray chip（42.4K · $12.46，ASCII 千进制，语言无关——有意偏离图表的万/亿口径），
/// 尾数精度与越档守卫对齐图表 FormatCompact。D-014 状态栏文本直显经真机取证不可行
/// （Avalonia 方形状态项裁剪宽位图，卡内 B1），字形分派随托盘接线一并撤除。
/// </summary>
public class TrayStatusTextTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(937, "937")]
    [InlineData(1000, "1K")]
    [InlineData(1500, "1.5K")]
    [InlineData(42400, "42.4K")] // 原型示例值
    [InlineData(1234567, "1.23M")]
    [InlineData(999499, "999.5K")]
    [InlineData(999500, "1M")] // 尾数取整越档升入上一档（与图表 FormatCompact 同构）
    [InlineData(12345678901, "12.35B")]
    public void FormatTokens_uses_ascii_thousand_ladder(long tokens, string expected)
    {
        TrayStatusText.FormatTokens(tokens).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(12.46, "$12.46")] // 原型示例值
    [InlineData(999.99, "$999.99")]
    [InlineData(1000, "$1K")]
    [InlineData(1234.56, "$1.23K")]
    [InlineData(12345678, "$12.35M")]
    public void FormatCost_keeps_two_decimals_below_one_k_then_compact(decimal costUsd, string expected)
    {
        TrayStatusText.FormatCost(costUsd).Should().Be(expected);
    }

    [Fact]
    public void Build_matches_prototype_tray_chip()
    {
        TrayStatusText.Build(12.46m, 42400).Should().Be("42.4K · $12.46");
    }

    [Fact]
    public void Build_is_honest_for_zero_day_with_historical_data()
    {
        // 有历史数据但今日为 0：如实显示 0，不伪装成降级态
        TrayStatusText.Build(0m, 0).Should().Be("0 · $0.00");
    }
}
