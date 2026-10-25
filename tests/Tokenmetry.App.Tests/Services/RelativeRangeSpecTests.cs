using System;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using FluentAssertions;
using Xunit;

namespace Tokenmetry.App.Tests.Services;

/// <summary>
/// 相对时间范围统一模型对账（D-020）：内置档与用户预设共用同一 Spec——窗口端点、词表校验、
/// 去重键、自动标签。today 全部显式注入钉死「今天」，跨月/跨年/闰二月/周锚点边界不随运行日期漂移。
/// 口径基准：本地自然日含端点；周锚点固定周一；未完周期（偏移 0）终点收口 today。
/// </summary>
public class RelativeRangeSpecTests : IDisposable
{
    // Translations 进程级单例：标签用例保存并恢复语言基线（同 FilterStateServiceTests 约定）。
    private readonly AppLanguage _originalLanguage = Translations.Instance.CurrentLanguage;

    public void Dispose() => Translations.Instance.SetLanguage(_originalLanguage);

    // ── 单日 ──

    [Fact]
    public void SingleDay_today_bounds_both_ends_to_today()
    {
        var today = new DateOnly(2026, 10, 25);

        RelativeRangeSpec.SingleDay(0).FromDay(today).Should().Be(today);
        RelativeRangeSpec.SingleDay(0).ToDay(today).Should().Be(today);
    }

    [Fact]
    public void SingleDay_yesterday_is_one_day_before_today()
    {
        var today = new DateOnly(2026, 10, 25);

        RelativeRangeSpec.SingleDay(-1).FromDay(today).Should().Be(new DateOnly(2026, 10, 24));
        RelativeRangeSpec.SingleDay(-1).ToDay(today).Should().Be(new DateOnly(2026, 10, 24));
    }

    // ── 滚动 ──

    [Fact]
    public void Rolling_seven_days_includes_today_and_prior_six()
    {
        var today = new DateOnly(2026, 10, 25);

        RelativeRangeSpec.Rolling(7).FromDay(today).Should().Be(new DateOnly(2026, 10, 19));
        RelativeRangeSpec.Rolling(7).ToDay(today).Should().Be(today);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(366)]
    public void Rolling_days_outside_domain_is_invalid(int days) =>
        RelativeRangeSpec.Rolling(days).IsValid.Should().BeFalse();

    [Theory]
    [InlineData(2)]
    [InlineData(365)]
    public void Rolling_days_domain_bounds_are_valid(int days) =>
        RelativeRangeSpec.Rolling(days).IsValid.Should().BeTrue();

    // ── 周（锚点固定周一）──

    [Fact]
    public void Week_zero_starts_on_monday_and_ends_today()
    {
        // 2026-01-04 是周日：本周起点回溯到 2025-12-29（周一），终点 = today（未完周期收口）
        var sunday = new DateOnly(2026, 1, 4);

        RelativeRangeSpec.Week(0).FromDay(sunday).Should().Be(new DateOnly(2025, 12, 29));
        RelativeRangeSpec.Week(0).ToDay(sunday).Should().Be(sunday);
    }

    [Fact]
    public void Week_zero_on_monday_starts_same_day()
    {
        var monday = new DateOnly(2026, 1, 5);

        RelativeRangeSpec.Week(0).FromDay(monday).Should().Be(monday);
    }

    [Fact]
    public void Week_minus_one_is_the_previous_full_monday_sunday_window()
    {
        var wednesday = new DateOnly(2026, 1, 7); // 本周 = 01-05（一）..01-11（日），上周 = 2025-12-29 .. 2026-01-04

        RelativeRangeSpec.Week(-1).FromDay(wednesday).Should().Be(new DateOnly(2025, 12, 29));
        RelativeRangeSpec.Week(-1).ToDay(wednesday).Should().Be(new DateOnly(2026, 1, 4));
    }

    // ── 月 ──

    [Fact]
    public void Month_zero_starts_on_first_and_ends_today()
    {
        var today = new DateOnly(2026, 10, 25);

        RelativeRangeSpec.Month(0).FromDay(today).Should().Be(new DateOnly(2026, 10, 1));
        RelativeRangeSpec.Month(0).ToDay(today).Should().Be(today);
    }

    [Fact]
    public void Month_minus_one_crosses_year_boundary()
    {
        var today = new DateOnly(2026, 1, 15);

        RelativeRangeSpec.Month(-1).FromDay(today).Should().Be(new DateOnly(2025, 12, 1));
        RelativeRangeSpec.Month(-1).ToDay(today).Should().Be(new DateOnly(2025, 12, 31));
    }

    [Theory]
    [InlineData(2025, 3, 15, 2025, 2, 28)] // 平年二月
    [InlineData(2024, 3, 15, 2024, 2, 29)] // 闰年二月
    public void Month_minus_one_handles_february_lengths(
        int year, int month, int day, int endYear, int endMonth, int endDay)
    {
        var today = new DateOnly(year, month, day);

        RelativeRangeSpec.Month(-1).ToDay(today).Should().Be(new DateOnly(endYear, endMonth, endDay));
    }

    // ── 词表校验 ──

    [Theory]
    [InlineData(RelativeRangeKind.SingleDay, -2, 7)]
    [InlineData(RelativeRangeKind.Week, 1, 7)]
    [InlineData(RelativeRangeKind.Month, -2, 7)]
    public void Non_rolling_kinds_only_allow_offsets_minus_one_to_zero(RelativeRangeKind kind, int offset, int days) =>
        new RelativeRangeSpec(kind, offset, days).IsValid.Should().BeFalse();

    [Fact]
    public void Rolling_requires_zero_offset() =>
        new RelativeRangeSpec(RelativeRangeKind.RollingDays, -1, 7).IsValid.Should().BeFalse();

    // ── 去重键 ──

    [Fact]
    public void DedupKey_ignores_days_for_non_rolling_kinds() =>
        new RelativeRangeSpec(RelativeRangeKind.Week, 0, 5).DedupKey
            .Should().Be(new RelativeRangeSpec(RelativeRangeKind.Week, 0, 99).DedupKey);

    [Fact]
    public void DedupKey_keeps_days_for_rolling_kinds() =>
        RelativeRangeSpec.Rolling(14).DedupKey.Should().NotBe(RelativeRangeSpec.Rolling(30).DedupKey);

    // ── 自动标签（封闭词表 → Translations 实时取串）──

    [Fact]
    public void Labels_are_generated_from_closed_vocabulary_in_chinese()
    {
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);

        RelativeRangeSpec.Month(0).ToLabel().Should().Be("本月");
        RelativeRangeSpec.Month(-1).ToLabel().Should().Be("上月");
        RelativeRangeSpec.Week(0).ToLabel().Should().Be("本周");
        RelativeRangeSpec.Week(-1).ToLabel().Should().Be("上周");
        RelativeRangeSpec.SingleDay(-1).ToLabel().Should().Be("昨天");
        RelativeRangeSpec.Rolling(14).ToLabel().Should().Be("最近 14 天");
    }

    [Fact]
    public void Labels_are_generated_from_closed_vocabulary_in_english()
    {
        Translations.Instance.SetLanguage(AppLanguage.English);

        RelativeRangeSpec.Month(0).ToLabel().Should().Be("This month");
        RelativeRangeSpec.Week(-1).ToLabel().Should().Be("Last week");
        RelativeRangeSpec.Rolling(14).ToLabel().Should().Be("Last 14 days");
    }
}
