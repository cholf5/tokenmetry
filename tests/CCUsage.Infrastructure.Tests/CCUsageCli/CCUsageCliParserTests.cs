namespace CCUsage.Infrastructure.Tests.CCUsageCli;

using CCUsage.Infrastructure.CCUsageCli;

public class CCUsageCliParserTests
{
    private readonly CCUsageCliParser _parser = new();

    [Fact]
    public async Task Parses_sample_daily_json_correctly()
    {
        var json = await File.ReadAllTextAsync("fixtures/sample-daily.json");
        var rows = _parser.ParseDailyJson(json);

        rows.Count.Should().Be(1);
        var row = rows.Single();
        row.Date.Should().Be("2026-01-09");
        row.TotalCostUsd.Should().Be(1.23m);
        row.TotalTokens.Should().Be(1000);
        row.Agents.Single().Agent.Should().Be("claude");
    }

    [Fact]
    public void Empty_json_returns_empty_list()
    {
        var rows = _parser.ParseDailyJson("[]");
        rows.Should().BeEmpty();
    }

    [Fact]
    public void Malformed_json_throws_parse_exception()
    {
        var action = () => _parser.ParseDailyJson("{invalid json}");
        action.Should().Throw<Exception>();
    }
}
