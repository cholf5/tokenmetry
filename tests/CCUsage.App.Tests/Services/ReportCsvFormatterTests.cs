using CCUsage.App.Services;
using CCUsage.App.ViewModels;

namespace CCUsage.App.Tests.Services;

public class ReportCsvFormatterTests
{
    [Fact]
    public void Format_WritesHeaderAndRowsInTableColumnOrder()
    {
        var formatter = new ReportCsvFormatter();

        var csv = formatter.Format(new[]
        {
            new ReportsDailyRow
            {
                Date = "2026-06-19",
                Cost = 12.345m,
                TotalTokens = 1234,
                InputTokens = 100,
                OutputTokens = 200,
                CacheCreationTokens = 300,
                CacheReadTokens = 400,
                AgentsText = "claude-code",
                ModelsText = "claude-sonnet-4"
            }
        });

        csv.Should().Be(
            "Date,Cost,Total Tokens,Input Tokens,Output Tokens,Cache Creation Tokens,Cache Read Tokens,Agents,Models\r\n" +
            "2026-06-19,12.345,1234,100,200,300,400,claude-code,claude-sonnet-4\r\n");
    }

    [Fact]
    public void Format_EscapesCommaQuoteAndNewlineFields()
    {
        var formatter = new ReportCsvFormatter();

        var csv = formatter.Format(new[]
        {
            new ReportsDailyRow
            {
                Date = "2026-06-19",
                Cost = 1m,
                AgentsText = "claude-code, zcode",
                ModelsText = "sonnet \"latest\"\nopus"
            }
        });

        csv.Should().Contain("\"claude-code, zcode\",\"sonnet \"\"latest\"\"\nopus\"");
    }
}
