using Tokenmetry.Reconciliation.Tests.Comparison;

namespace Tokenmetry.Reconciliation.Tests;

/// <summary>
/// 容差比较器（Q-001 subtask 1）的单元测试：tokens 精确一致、cost 相对误差 ≤ 0.5%、
/// 绝对误差 &lt; $0.01 视为通过、差异明细可定位到解析或定价原因。
/// </summary>
public class ReconciliationComparatorTests
{
    private static ReconciliationEntry Entry(
        string date = "2026-09-14",
        string? model = "gpt-5.3-codex",
        long input = 1000,
        long output = 200,
        long cacheCreation = 0,
        long cacheRead = 5000,
        decimal? cost = 0.01m)
        => new(date, model, input, output, cacheCreation, cacheRead, input + output + cacheCreation + cacheRead, cost);

    [Fact]
    public void Identical_entries_report_no_differences()
    {
        var golden = new[] { Entry(), Entry("2026-09-15", "gpt-5-codex", input: 42, cost: 0.5m) };
        var ours = new[] { Entry(), Entry("2026-09-15", "gpt-5-codex", input: 42, cost: 0.5m) };

        var report = ReconciliationComparator.Compare(golden, ours);

        report.HasDifferences.Should().BeFalse();
        report.ComparedEntries.Should().Be(2);
        report.Render().Should().Contain("无差异");
    }

    [Fact]
    public void Token_mismatch_is_reported_per_field_with_delta_and_parsing_cause()
    {
        var golden = new[] { Entry(input: 1000, output: 200, cacheRead: 5000) };
        // input 少 100 会让 totalTokens 同步少 100：两个 token 分量各报一条差异
        var ours = new[] { Entry(input: 900, output: 200, cacheRead: 5000) };

        var report = ReconciliationComparator.Compare(golden, ours);

        report.HasDifferences.Should().BeTrue();
        report.Differences.Should().HaveCount(2);
        var difference = report.Differences.Single(item => item.Field == "inputTokens");
        difference.Kind.Should().Be(ReconciliationDifferenceKind.TokenMismatch);
        difference.GoldenValue.Should().Be("1000");
        difference.OursValue.Should().Be("900");
        difference.Delta.Should().Be("-100");
        difference.Cause.Should().Be(ReconciliationCause.Parsing);
        difference.Hint.Should().Contain("解析");
        report.Differences.Single(item => item.Field == "totalTokens").Delta.Should().Be("-100");
    }

    [Fact]
    public void Cost_within_relative_tolerance_passes()
    {
        // 绝对误差 $0.04（≥ $0.01 阈值），但相对误差 0.4% < 0.5%：通过（相对容差路径）
        var golden = new[] { Entry(cost: 10.0000m) };
        var ours = new[] { Entry(cost: 10.0400m) };

        var report = ReconciliationComparator.Compare(golden, ours);

        report.HasDifferences.Should().BeFalse();
    }

    [Fact]
    public void Cost_beyond_relative_tolerance_is_reported_with_deviation_and_pricing_cause()
    {
        // 相对误差 2.3%：超出容差
        var golden = new[] { Entry(cost: 1.0000m) };
        var ours = new[] { Entry(cost: 1.0230m) };

        var report = ReconciliationComparator.Compare(golden, ours);

        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Kind.Should().Be(ReconciliationDifferenceKind.CostMismatch);
        difference.Cause.Should().Be(ReconciliationCause.Pricing);
        difference.RelativeDeviation.Should().BeApproximately(0.023m, 0.0000001m);
        difference.Hint.Should().Contain("定价");
    }

    [Fact]
    public void Small_absolute_cost_difference_passes_even_with_large_relative_deviation()
    {
        // 绝对误差 $0.005 < $0.01：小额日期的相对误差噪声不报差异
        var golden = new[] { Entry(cost: 0.0100m) };
        var ours = new[] { Entry(cost: 0.0150m) };

        var report = ReconciliationComparator.Compare(golden, ours);

        report.HasDifferences.Should().BeFalse();
    }

    [Fact]
    public void Small_absolute_threshold_is_strictly_less_than_one_cent()
    {
        // 绝对误差恰为 $0.01 且相对误差 2%：不满足"绝对误差 < $0.01"，应报差异
        var golden = new[] { Entry(cost: 0.5000m) };
        var ours = new[] { Entry(cost: 0.5100m) };

        var report = ReconciliationComparator.Compare(golden, ours);

        report.HasDifferences.Should().BeTrue();
        report.Differences.Single().Kind.Should().Be(ReconciliationDifferenceKind.CostMismatch);
    }

    [Fact]
    public void Entry_missing_on_our_side_is_reported_as_missing()
    {
        var golden = new[] { Entry(), Entry("2026-09-15", "gpt-5-codex") };
        var ours = new[] { Entry() };

        var report = ReconciliationComparator.Compare(golden, ours);

        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Kind.Should().Be(ReconciliationDifferenceKind.MissingInOurs);
        difference.Date.Should().Be("2026-09-15");
        difference.Model.Should().Be("gpt-5-codex");
        difference.Cause.Should().Be(ReconciliationCause.Parsing);
        difference.OursValue.Should().Be("(无)");
    }

    [Fact]
    public void Entry_only_on_our_side_is_reported_as_missing_in_golden()
    {
        var golden = Array.Empty<ReconciliationEntry>();
        var ours = new[] { Entry(model: "claude-haiku-4-5", input: 7, output: 0, cacheRead: 0) };

        var report = ReconciliationComparator.Compare(golden, ours);

        var difference = report.Differences.Should().ContainSingle().Subject;
        difference.Kind.Should().Be(ReconciliationDifferenceKind.MissingInGolden);
        difference.GoldenValue.Should().Be("(无)");
        difference.OursValue.Should().Contain("tokens=7");
    }

    [Fact]
    public void Null_cost_on_either_side_skips_cost_comparison()
    {
        // codex per-model 口径：CLI 不提供成本（null），不因成本缺失报差异
        var golden = new[] { Entry(cost: null) };
        var ours = new[] { Entry(cost: 12.3456m) };

        var report = ReconciliationComparator.Compare(golden, ours);

        report.HasDifferences.Should().BeFalse();
    }

    [Fact]
    public void Zero_golden_cost_only_passes_within_absolute_tolerance()
    {
        // golden 成本为 0 时相对误差无定义：只有绝对误差 < $0.01 才通过
        var passing = ReconciliationComparator.Compare(new[] { Entry(cost: 0m) }, new[] { Entry(cost: 0.005m) });
        passing.HasDifferences.Should().BeFalse();

        var failing = ReconciliationComparator.Compare(new[] { Entry(cost: 0m) }, new[] { Entry(cost: 0.02m) });
        failing.HasDifferences.Should().BeTrue();
        failing.Differences.Single().RelativeDeviation.Should().BeNull();
    }

    [Fact]
    public void Daily_total_entries_are_compared_like_any_other_entry()
    {
        // 总计行以 Model=null 进入比较（分桶口径），key 与 per-model 条目互不冲突
        var golden = new[] { Entry(model: null, input: 10), Entry(model: "m", input: 10) };
        var ours = new[] { Entry(model: null, input: 10), Entry(model: "m", input: 10) };

        var report = ReconciliationComparator.Compare(golden, ours);

        report.HasDifferences.Should().BeFalse();
        report.ComparedEntries.Should().Be(2);
    }

    [Fact]
    public void Render_lists_date_model_field_delta_and_hint()
    {
        var golden = new[] { Entry(model: "gpt-5.3-codex", input: 1000, cost: 1m) };
        var ours = new[] { Entry(model: "gpt-5.3-codex", input: 900, cost: 2m) };

        var report = ReconciliationComparator.Compare(golden, ours);
        var text = report.Render();

        text.Should().Contain("2026-09-14 gpt-5.3-codex");
        text.Should().Contain("[token·inputTokens]");
        text.Should().Contain("golden=1000");
        text.Should().Contain("ours=900");
        text.Should().Contain("差=-100");
        text.Should().Contain("[cost]");
        text.Should().Contain("相对偏差=");
        text.Should().Contain("解析");
        text.Should().Contain("定价");
    }

    [Fact]
    public void Custom_tolerance_is_honoured()
    {
        var strict = new ReconciliationTolerance(CostRelativeTolerance: 0.0001m, CostAbsoluteToleranceUsd: 0.0001m);

        var report = ReconciliationComparator.Compare(
            new[] { Entry(cost: 1m) },
            new[] { Entry(cost: 1.001m) },
            tolerance: strict);

        report.HasDifferences.Should().BeTrue();
        report.Tolerance.Should().Be(strict);
    }
}
