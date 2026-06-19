namespace CCUsage.Infrastructure.Tests.Cache;

using CCUsage.Core.Models;
using CCUsage.Infrastructure.Cache;

public class SqliteUsageCacheTests : IAsyncLifetime
{
    private string _tempDbPath = null!;
    private SqliteUsageCache _cache = null!;

    public async Task InitializeAsync()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"ccusage-test-{Guid.NewGuid()}.db");
        _cache = new SqliteUsageCache(_tempDbPath);
        await _cache.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (File.Exists(_tempDbPath))
        {
            // SQLite may keep the file open for a short time. Retry a few times.
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.Delete(_tempDbPath);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    [Fact]
    public async Task Empty_db_returns_null_dashboard()
    {
        var result = await _cache.GetLatestDashboardAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task Dashboard_roundtrip_preserves_values()
    {
        var summary = new DashboardSummary
        {
            TodayCostUsd = 1.23m,
            TodayTokens = 1000,
            Status = "ok"
        };

        await _cache.SaveDashboardAsync(summary);
        var result = await _cache.GetLatestDashboardAsync();

        result.Should().NotBeNull();
        result!.TodayCostUsd.Should().Be(1.23m);
        result.TodayTokens.Should().Be(1000);
        result.Status.Should().Be("ok");
    }

    [Fact]
    public async Task Daily_rows_roundtrip_preserves_values()
    {
        var rows = new List<DailyUsageSummary>
        {
            new()
            {
                Date = "2026-01-09",
                TotalCostUsd = 1.23m,
                TotalTokens = 1000,
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "gpt-5.2-codex", CostUsd = 1.23m, TotalTokens = 1000 }
                }
            },
            new() { Date = "2026-01-10", TotalCostUsd = 2.34m, TotalTokens = 2000 }
        };

        await _cache.SaveDailyRowsAsync(rows);
        var result = await _cache.GetDailyRowsAsync();

        result.Count.Should().Be(2);
        var firstRow = result.First(r => r.Date == "2026-01-09");
        firstRow.TotalCostUsd.Should().Be(1.23m);
        firstRow.Models.Should().ContainSingle();
        var model = firstRow.Models.Single();
        model.Model.Should().Be("gpt-5.2-codex");
        model.CostUsd.Should().Be(1.23m);
        model.TotalTokens.Should().Be(1000);
    }
}
