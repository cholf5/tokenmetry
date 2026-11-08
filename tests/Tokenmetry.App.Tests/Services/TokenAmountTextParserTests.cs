namespace Tokenmetry.App.Tests.Services;

using Tokenmetry.App.Services;

/// <summary>
/// 计价器输入解析（I-CALC）：数量级后缀（万/亿/k/M/B）、e 记法、千分位逗号、百分比；
/// 负数一律拒绝（不是合法用量）。空串语义由调用方定义（用量当 0、命中率当 0），解析器如实报失败。
/// </summary>
public class TokenAmountTextParserTests
{
    [Theory]
    [InlineData("2000000000", 2_000_000_000L)]
    [InlineData("2e9", 2_000_000_000L)]
    [InlineData("2E9", 2_000_000_000L)]
    [InlineData("20亿", 2_000_000_000L)]
    [InlineData("2000万", 20_000_000L)]
    [InlineData("1500k", 1_500_000L)]
    [InlineData("1500K", 1_500_000L)]
    [InlineData("3M", 3_000_000L)]
    [InlineData("1.5B", 1_500_000_000L)]
    [InlineData("1.5e9", 1_500_000_000L)]
    [InlineData("12,345", 12_345L)]
    [InlineData("12，345", 12_345L)] // 全角逗号
    [InlineData(" 20亿 ", 2_000_000_000L)]
    [InlineData("0", 0L)]
    public void TryParseTokens_accepts_calculator_style_input(string text, long expected)
    {
        TokenAmountTextParser.TryParseTokens(text, out var tokens).Should().BeTrue(text);
        tokens.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("2亿k")] // 连缀数量级不是数
    [InlineData("亿")]   // 只有尾缀不是数
    [InlineData("1.2.3")]
    public void TryParseTokens_rejects_invalid_input(string text)
    {
        TokenAmountTextParser.TryParseTokens(text, out _).Should().BeFalse(text);
    }

    [Fact]
    public void TryParseTokens_rejects_nested_suffix()
    {
        // 只剥一层尾缀："2亿亿" 剥完剩 "2亿"，不是纯数字，如实拒绝。
        TokenAmountTextParser.TryParseTokens("2亿亿", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("80", 0.80)]
    [InlineData("80%", 0.80)]
    [InlineData("80.5％", 0.805)]
    [InlineData("100", 1.0)]
    [InlineData("0", 0.0)]
    [InlineData("", 0.0)]   // 空 = 未填 = 无缓存命中，不算错
    [InlineData("   ", 0.0)]
    [InlineData("150", 1.0)] // 钳到 100%
    public void TryParsePercent_reads_percent_scale(string text, double expectedFraction)
    {
        TokenAmountTextParser.TryParsePercent(text, out var fraction).Should().BeTrue(text);
        fraction.Should().Be((decimal)expectedFraction);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("80%%")]
    public void TryParsePercent_rejects_invalid_input(string text)
    {
        TokenAmountTextParser.TryParsePercent(text, out _).Should().BeFalse(text);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("0.15", 0.15)]
    [InlineData(" 2.5 ", 2.5)]
    public void TryParsePrice_accepts_plain_non_negative_decimal(string text, double expected)
    {
        TokenAmountTextParser.TryParsePrice(text, out var price).Should().BeTrue(text);
        price.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    public void TryParsePrice_rejects_empty_or_invalid(string text)
    {
        TokenAmountTextParser.TryParsePrice(text, out _).Should().BeFalse(text);
    }
}
