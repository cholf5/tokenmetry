namespace TokenPulse.Infrastructure.Tests.CCUsageCli;

using TokenPulse.Infrastructure.CCUsageCli;

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
        row.Agents.Single().Agent.Should().Be("codex");
        row.Models.Should().ContainSingle();
        var model = row.Models.Single();
        model.Model.Should().Be("gpt-5.2-codex");
        model.CostUsd.Should().Be(1.23m);
        model.TotalTokens.Should().Be(1000);
    }

    [Fact]
    public void Parses_model_total_tokens_when_present()
    {
        var json = """
        {
          "daily": [
            {
              "period": "2026-01-09",
              "modelBreakdowns": [
                {
                  "modelName": "sonnet",
                  "cost": 1.23,
                  "totalTokens": 1234,
                  "inputTokens": 500,
                  "outputTokens": 300,
                  "cacheCreationTokens": 100,
                  "cacheReadTokens": 200
                }
              ]
            }
          ]
        }
        """;

        var row = _parser.ParseDailyJson(json).Single();

        row.Models.Single().TotalTokens.Should().Be(1234);
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
