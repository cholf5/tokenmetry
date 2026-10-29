using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Tests.ViewModels;

/// <summary>
/// KPI 环比颜色语义（KpiChangeValence）的真值表锁：颜色按**指标价值极性**，不按涨跌方向。
/// <list type="bullet">
/// <item>Neutral（tokens / 会话数）：涨跌都是用量的中性事实，恒 muted——方向只由箭头表达；</item>
/// <item>HigherIsBetter（缓存命中占比）：升 = 好（绿）、降 = 坏（红）；</item>
/// <item>LowerIsBetter（成本）：降 = 好（绿）、升 = 坏（红）——成本上涨不能画成绿色。</item>
/// </list>
/// 三布尔互斥完备是硬约束：视图只绑这三个类，重叠会让颜色互相覆盖。
/// </summary>
public class KpiChangeSemanticsTests
{
    private static readonly MetricChange Up = new(ChangeDirection.Increase, 23.6m);
    private static readonly MetricChange Down = new(ChangeDirection.Decrease, 18.2m);

    public static TheoryData<string, MetricChange, KpiChangeValence, bool, bool, bool> Cases => new()
    {
        // 中性指标：方向不给任何好坏暗示
        { "tokens up", Up, KpiChangeValence.Neutral, false, false, true },
        { "tokens down", Down, KpiChangeValence.Neutral, false, false, true },
        { "tokens flat", MetricChange.Neutral, KpiChangeValence.Neutral, false, false, true },
        // 越高越好（缓存命中占比）
        { "cache up", Up, KpiChangeValence.HigherIsBetter, true, false, false },
        { "cache down", Down, KpiChangeValence.HigherIsBetter, false, true, false },
        { "cache flat", MetricChange.Neutral, KpiChangeValence.HigherIsBetter, false, false, true },
        // 越低越好（成本）
        { "cost down", Down, KpiChangeValence.LowerIsBetter, true, false, false },
        { "cost up", Up, KpiChangeValence.LowerIsBetter, false, true, false },
        { "cost flat", MetricChange.Neutral, KpiChangeValence.LowerIsBetter, false, false, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Change_colour_follows_valence_not_direction(
        string label,
        MetricChange change,
        KpiChangeValence valence,
        bool expectPositive,
        bool expectNegative,
        bool expectNeutral)
    {
        var card = new KpiCardViewModel("1", change, valence);

        Assert.True(card.IsPositive == expectPositive, $"{label}: IsPositive 应为 {expectPositive}");
        Assert.True(card.IsNegative == expectNegative, $"{label}: IsNegative 应为 {expectNegative}");
        Assert.True(card.IsNeutral == expectNeutral, $"{label}: IsNeutral 应为 {expectNeutral}");

        // 互斥完备（视图绑三个类，重叠会让颜色互相覆盖）
        var trueCount = (card.IsPositive ? 1 : 0) + (card.IsNegative ? 1 : 0) + (card.IsNeutral ? 1 : 0);
        Assert.Equal(1, trueCount);
    }

    [Fact]
    public void Change_text_keeps_arrow_and_absolute_percentage()
    {
        // 文案不变：箭头表达方向、数值取绝对值（"↓ 18.2%" 不写成 "↓ -18.2%"）
        var card = new KpiCardViewModel("$286.41", Down, KpiChangeValence.LowerIsBetter);

        Assert.Equal("↓ 18.2%", card.ChangeText);
        Assert.True(card.IsPositive);
    }

    [Fact]
    public void Pending_placeholder_is_neutral()
    {
        Assert.Equal("—", KpiCardViewModel.Pending.Value);
        Assert.True(KpiCardViewModel.Pending.IsNeutral);
    }
}
