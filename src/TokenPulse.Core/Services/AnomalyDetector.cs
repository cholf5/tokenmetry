using System.Linq;
using TokenPulse.Core.Models;

namespace TokenPulse.Core.Services;

public class AnomalyDetector
{
    private const decimal AnomalyRatioThreshold = 2.0m;

    public List<AnomalyNotice> DetectAnomalies(
        DailyUsageSummary today,
        List<DailyUsageSummary> history,
        decimal minAbsoluteCost)
    {
        var result = new List<AnomalyNotice>();

        if (!history.Any() || today.TotalCostUsd < minAbsoluteCost)
        {
            return result;
        }

        var average = history.Average(h => h.TotalCostUsd);
        if (average <= 0)
        {
            return result;
        }

        var ratio = today.TotalCostUsd / average;
        if (ratio >= AnomalyRatioThreshold)
        {
            result.Add(new AnomalyNotice
            {
                Kind = "daily_cost_spike",
                Message = $"今日成本是过去 {history.Count} 日均值的 {ratio:0.0} 倍",
                Ratio = ratio
            });
        }

        return result;
    }
}
