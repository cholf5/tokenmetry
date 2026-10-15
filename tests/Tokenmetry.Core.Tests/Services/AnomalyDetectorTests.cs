namespace Tokenmetry.Core.Tests.Services;

using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

public class AnomalyDetectorTests
{
    private readonly AnomalyDetector _detector = new();
    private const decimal MinAbsoluteCost = 0.05m;

    [Fact]
    public void No_history_returns_no_anomalies()
    {
        var history = new List<DailyUsageSummary>();
        var today = new DailyUsageSummary { Date = "2026-01-10", TotalCostUsd = 1.0m };

        var anomalies = _detector.DetectAnomalies(today, history, MinAbsoluteCost);

        anomalies.Should().BeEmpty();
    }

    [Fact]
    public void Cost_below_min_absolute_threshold_is_not_anomaly_even_when_high_ratio()
    {
        var history = Enumerable.Range(1, 7).Select(i =>
            new DailyUsageSummary { Date = $"2026-01-0{i}", TotalCostUsd = 0.01m }).ToList();
        var today = new DailyUsageSummary { Date = "2026-01-10", TotalCostUsd = 0.03m };

        var anomalies = _detector.DetectAnomalies(today, history, MinAbsoluteCost);

        anomalies.Should().BeEmpty();
    }

    [Fact]
    public void Cost_above_2x_average_is_daily_cost_spike()
    {
        var history = Enumerable.Range(1, 7).Select(i =>
            new DailyUsageSummary { Date = $"2026-01-0{i}", TotalCostUsd = 1.0m }).ToList();
        var today = new DailyUsageSummary { Date = "2026-01-10", TotalCostUsd = 2.5m };

        var anomalies = _detector.DetectAnomalies(today, history, MinAbsoluteCost);

        var spike = anomalies.Single();
        spike.Kind.Should().Be("daily_cost_spike");
        spike.Message.Should().Contain("2.5 倍");
    }

    [Fact]
    public void Cost_below_2x_average_is_not_anomaly()
    {
        var history = Enumerable.Range(1, 7).Select(i =>
            new DailyUsageSummary { Date = $"2026-01-0{i}", TotalCostUsd = 1.0m }).ToList();
        var today = new DailyUsageSummary { Date = "2026-01-10", TotalCostUsd = 1.9m };

        var anomalies = _detector.DetectAnomalies(today, history, MinAbsoluteCost);

        anomalies.Should().BeEmpty();
    }
}
