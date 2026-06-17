using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.CCUsageCli;

public class CCUsageCliDataSource : IUsageDataSource
{
    private readonly CCUsageCliParser _parser = new();
    private readonly string _cliPath;

    public CCUsageCliDataSource(string cliPath = "npx")
    {
        _cliPath = cliPath;
    }

    public async Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default)
    {
        var runner = new CCUsageCliRunner(_cliPath, "--yes ccusage@latest --json --offline daily");
        var json = await runner.GetOutputAsync(ct);
        return _parser.ParseDailyJson(json);
    }
}
