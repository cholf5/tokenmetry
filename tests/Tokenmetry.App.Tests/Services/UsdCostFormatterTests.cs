namespace Tokenmetry.App.Tests.Services;

using Tokenmetry.App.Services;

/// <summary>
/// 计价器金额格式（I-CALC）：三档小数位（≥$1 两位 / ≥1¢ 四位 / 更小六位）——估算器要在
/// 「20 亿 tokens × 便宜模型」量级下仍读得出非零数。测试文化固定 zh-Hans（TestCulture），
/// 分组与小数点形状与 invariant 一致，期望串可直写。
/// </summary>
public class UsdCostFormatterTests
{
    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(1, "$1.00")]
    [InlineData(234, "$234.00")]
    [InlineData(1234.5, "$1,234.50")]
    [InlineData(0.05, "$0.0500")]
    [InlineData(0.0012, "$0.001200")]
    [InlineData(0.0000012, "$0.000001")]
    public void Format_picks_decimals_by_magnitude(decimal usd, string expected)
    {
        UsdCostFormatter.Format(usd).Should().Be(expected);
    }

    [Fact]
    public void FormatPerMillion_appends_suffix()
    {
        UsdCostFormatter.FormatPerMillion(3m).Should().Be("$3.00/M");
        UsdCostFormatter.FormatPerMillion(0.3m).Should().Be("$0.3000/M");
    }
}
