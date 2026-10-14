namespace TokenPulse.Core.Tests.Services;

using TokenPulse.Core.Services;

public class DateRangeServiceTests
{
    private readonly DateRangeService _service = new();

    [Fact]
    public void Today_returns_local_date_iso_format()
    {
        var today = _service.Today();
        today.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
    }

    [Fact]
    public void GetMonthPrefix_returns_yyyy_mm()
    {
        var prefix = _service.GetMonthPrefix("2026-01-09");
        prefix.Should().Be("2026-01");
    }

    [Fact]
    public void GetLastNDays_includes_today()
    {
        var dates = _service.GetLastNDays(7).ToList();
        dates.Count.Should().Be(7);
        dates.First().Should().Be(_service.Today());
    }
}
