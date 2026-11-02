using Tokenmetry.App.Localization;
using Tokenmetry.App.Tray;

namespace Tokenmetry.App.Tests.Tray;

/// <summary>
/// 托盘 tooltip 摘要行（今日 tokens / 成本 / 缓存命中）格式化：纯函数，数据态分派由
/// TrayService 接线测试覆盖。测试进程文化固定 zh-Hans（TestCulture）——tokens 紧凑串按
/// 当前语言取大数口径，断言与 FormatCompactTokens 输出比对保持语言无关；成本恒 ASCII
/// （TrayStatusText.FormatCost 口径）；命中率经 TrayTooltipBuilder.FormatShare 出 x.x%。
/// </summary>
public class TrayTooltipBuilderTests
{
    [Fact]
    public void Build_lines_keep_product_title_and_add_today_summary()
    {
        var lines = Build(tokens: 255_000_000, costUsd: 8.80m, cacheShare: 0.983m);

        lines.Should().HaveCount(2);
        lines[0].Should().Be(Translations.Instance.TrayTooltip);
        // 255,000,000 → zh 万进制 2.55亿（口语直读，非 2.6亿）
        lines[1].Should().Be($"今日: {TrendChartTokens(255_000_000)} tok, 成本 $8.80, 缓存命中 98.3%");
    }

    [Fact]
    public void Summary_line_is_honest_for_zero_day_with_data()
    {
        // 有历史数据但今日为 0：0 token 如实显示，成本 $0.00，无缓存读数无命中率（null 不冒充 0%）
        var lines = Build(tokens: 0, costUsd: 0m, cacheShare: null);

        lines[1].Should().Be($"今日: {TrendChartTokens(0)} tok, 成本 $0.00, 缓存命中 —");
    }

    [Fact]
    public void NoData_keeps_title_plus_honest_no_data_line()
    {
        // 无数据不空转：旧 TrayCostNoData 的诚实口径保留为第二行（新装 / 错误轮不冒充 0）
        TrayTooltipBuilder.BuildLines(hasData: false, todayTokens: 0, todayCostUsd: 0m, todayCacheShare: null)
            .Should().BeEquivalentTo([Translations.Instance.TrayTooltip, Translations.Instance.TrayTodayNoData]);
    }

    private static string TrendChartTokens(long tokens) =>
        global::Tokenmetry.App.Controls.TrendChart.FormatCompactTokens(tokens);

    private static IReadOnlyList<string> Build(long tokens, decimal costUsd, decimal? cacheShare) =>
        TrayTooltipBuilder.BuildLines(hasData: true, todayTokens: tokens, todayCostUsd: costUsd, todayCacheShare: cacheShare);
}
