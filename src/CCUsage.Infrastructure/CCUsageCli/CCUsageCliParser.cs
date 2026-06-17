using System.Text.Json;
using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.CCUsageCli;

public class CCUsageCliParser
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public List<DailyUsageSummary> ParseDailyJson(string json)
    {
        return JsonSerializer.Deserialize<List<DailyUsageSummary>>(json, _options)
            ?? throw new InvalidOperationException("Failed to parse daily JSON: result was null");
    }
}
