using Tokenmetry.App.Tray;

namespace Tokenmetry.App.Tests.Tray;

/// <summary>
/// D-014 状态栏直显文本的纯格式化与三态分派（headless 集成行为见 Headless 的 TrayServiceTests）：
/// 紧凑口径沿原型 tray chip（42.4K · $12.46，ASCII 千进制，语言无关——有意偏离图表的万/亿口径），
/// 尾数精度与越档守卫对齐图表 FormatCompact；字形分派覆盖有值 / 无数据 / 扫描中 / 刷新失败四态。
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

    [Theory]
    [InlineData("ok", true, false, 12.46, 42400, "42.4K · $12.46")]
    [InlineData("ok", false, false, 0, 0, "—")]
    [InlineData("ok", false, true, 0, 0, "…")]
    [InlineData("ok", true, true, 12.46, 42400, "42.4K · $12.46")] // 周期刷新保持最后真值，不闪动
    [InlineData("error", true, false, 12.46, 42400, null)] // 刷新失败回落模板图标，不留陈旧数字
    [InlineData("error", false, true, 0, 0, null)]
    public void ResolveStatusGlyph_dispatches_four_states(
        string status, bool hasData, bool isLoading, decimal costUsd, long tokens, string? expected)
    {
        TrayService.ResolveStatusGlyph(status, hasData, isLoading, costUsd, tokens).Should().Be(expected);
    }
}
