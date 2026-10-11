using CCUsage.Core.Services;

namespace CCUsage.Core.Tests.Services;

/// <summary>
/// I-007: DeepSeek 官方定价目录测试。目录数据移植自 ../dsh-usage-stats/lib/pricing.js（MIT），
/// 期望值按其规则手工核算（per 1M tokens 四桶分价 + Asia/Shanghai peak/offPeak + 生效区间）。
/// </summary>
public class DeepSeekPricingCatalogTests
{
    private static readonly DateTimeOffset FlatEra = new(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);       // flat 规则时代
    private static readonly DateTimeOffset BandEra = new(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);        // time-band v1（周二 10:00 上海，高峰窗）
    private static readonly DateTimeOffset WeekdayEraPeak = new(2026, 8, 24, 2, 0, 0, TimeSpan.Zero); // 周一 10:00 上海（高峰）
    private static readonly DateTimeOffset WeekdayEraOffPeak = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero); // 周一 08:00 上海（平峰）
    private static readonly DateTimeOffset WeekdayEraWeekend = new(2026, 8, 23, 2, 0, 0, TimeSpan.Zero); // 周日 10:00 上海（平峰）

    private readonly DeepSeekPricingCatalog _catalog = DeepSeekPricingCatalog.Instance;

    [Fact]
    public void Snapshot_carries_six_ported_rules()
    {
        _catalog.Rules.Should().HaveCount(6);
        _catalog.Rules.Select(rule => rule.Model).Should().OnlyContain(
            model => model == "deepseek-v4-flash" || model == "deepseek-v4-pro");
    }

    [Fact]
    public void Flat_era_prices_four_buckets_for_official_routes()
    {
        // deepseek-v4-flash flat：< 2026-08-16T16:00Z，input 0.14 / cacheRead 0.0028 / output 0.28 / cacheWrite 无。
        var ok = _catalog.TryEstimate("deepseek/deepseek-v4-flash", FlatEra,
            inputTokens: 1_000_000, outputTokens: 1_000_000, cacheReadTokens: 2_000_000, cacheWriteTokens: 0,
            out var cost);

        ok.Should().BeTrue();
        cost.Should().Be(0.14m + 0.0056m + 0.28m);
    }

    [Fact]
    public void Deepseek_official_route_id_is_accepted()
    {
        var ok = _catalog.TryEstimate("deepseek-official/deepseek-v4-pro", WeekdayEraPeak,
            inputTokens: 1_000_000, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0,
            out var cost);

        ok.Should().BeTrue();
        cost.Should().Be(1.32m); // weekday schedule 高峰价 input 1.32
    }

    [Fact]
    public void Weekday_schedule_resolves_peak_by_shanghai_clock()
    {
        // 周一 10:00 上海 ∈ [09:00,12:00) → peak input 0.44；08:00 → offPeak 0.22；周日 → offPeak。
        _catalog.TryEstimate("deepseek/deepseek-v4-flash", WeekdayEraPeak, 1_000_000, 0, 0, 0, out var peak).Should().BeTrue();
        _catalog.TryEstimate("deepseek/deepseek-v4-flash", WeekdayEraOffPeak, 1_000_000, 0, 0, 0, out var offPeak).Should().BeTrue();
        _catalog.TryEstimate("deepseek/deepseek-v4-flash", WeekdayEraWeekend, 1_000_000, 0, 0, 0, out var weekend).Should().BeTrue();

        peak.Should().Be(0.44m);
        offPeak.Should().Be(0.22m);
        weekend.Should().Be(0.22m);
    }

    [Fact]
    public void Time_band_v1_applies_daily_peak_windows()
    {
        // 2026-08-18（周二）∈ time-band v1（peakDays 全周）：10:00 上海 ∈ [09:00,12:00) → peak 0.44。
        _catalog.TryEstimate("deepseek/deepseek-v4-flash", BandEra, 1_000_000, 0, 0, 0, out var cost).Should().BeTrue();
        cost.Should().Be(0.44m);
    }

    [Fact]
    public void Effective_to_boundary_is_exclusive()
    {
        // 2026-08-16T16:00:00Z 起 flat 规则失效（time-band v1 接管）。
        var atBoundary = _catalog.TryEstimate("deepseek/deepseek-v4-flash",
            new DateTimeOffset(2026, 8, 16, 16, 0, 0, TimeSpan.Zero), 1_000_000, 0, 0, 0, out var boundaryCost);
        var beforeBoundary = _catalog.TryEstimate("deepseek/deepseek-v4-flash",
            new DateTimeOffset(2026, 8, 16, 15, 59, 59, TimeSpan.Zero), 1_000_000, 0, 0, 0, out var beforeCost);

        atBoundary.Should().BeTrue();
        boundaryCost.Should().Be(0.22m); // time-band v1 offPeak
        beforeBoundary.Should().BeTrue();
        beforeCost.Should().Be(0.14m);   // flat
    }

    [Fact]
    public void CacheWrite_tokens_fail_closed_for_every_rule()
    {
        // 内置目录所有规则的 cacheWrite 均无单价（蓝本 unitPrices 默认 null）→ 正桶缺价整体 fail-closed。
        _catalog.TryEstimate("deepseek/deepseek-v4-flash", FlatEra, 1, 1, 1, 1, out _).Should().BeFalse();
        _catalog.TryEstimate("deepseek/deepseek-v4-pro", WeekdayEraPeak, 1, 1, 1, 1, out _).Should().BeFalse();
        // 零 cacheWrite 不受影响。
        _catalog.TryEstimate("deepseek/deepseek-v4-flash", FlatEra, 1, 1, 1, 0, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("ark/glm-5.3-flash")]
    [InlineData("glm-5.3-flash")]          // 无 provider 前缀不匹配官方路由门
    [InlineData("deepseek/deepseek-chat")] // 官方路由但模型不在目录
    [InlineData("unknown/unknown")]
    [InlineData(null)]
    public void Non_catalog_models_fail_closed(string? model)
    {
        _catalog.TryEstimate(model, WeekdayEraPeak, 100, 100, 0, 0, out var cost).Should().BeFalse();
        cost.Should().Be(0m);
    }

    [Fact]
    public void Rules_do_not_overlap_for_same_model()
    {
        // 蓝本 validatePricingRules 的关键约束：同模型生效区间两两不重叠（歧义即 fail-closed）。
        foreach (var model in new[] { "deepseek-v4-flash", "deepseek-v4-pro" })
        {
            var windows = _catalog.Rules
                .Where(rule => rule.Model == model)
                .Select(rule => (From: rule.EffectiveFrom ?? DateTimeOffset.MinValue, To: rule.EffectiveTo ?? DateTimeOffset.MaxValue))
                .OrderBy(window => window.From)
                .ToList();
            for (var index = 1; index < windows.Count; index++)
            {
                windows[index].From.Should().BeOnOrAfter(windows[index - 1].To,
                    "生效区间 [from, to) 必须互不重叠，否则 TryEstimate 按歧义 fail-closed");
            }
        }
    }
}
