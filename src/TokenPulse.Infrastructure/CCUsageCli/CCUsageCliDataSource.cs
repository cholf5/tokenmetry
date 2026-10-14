using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;

namespace TokenPulse.Infrastructure.CCUsageCli;

public class CCUsageCliDataSource : IUsageDataSource
{
    private readonly CCUsageCliParser _parser = new();
    private readonly string _cliPath;

    public CCUsageCliDataSource(string cliPath = "ccusage")
    {
        _cliPath = cliPath;
    }

    public async Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default)
    {
        var runner = new CCUsageCliRunner(_cliPath, "--json --offline daily");
        var json = await runner.GetOutputAsync(ct);
        return _parser.ParseDailyJson(json);
    }
}
