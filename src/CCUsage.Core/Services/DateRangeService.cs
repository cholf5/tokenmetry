namespace CCUsage.Core.Services;

public class DateRangeService
{
    public virtual string Today() => DateTime.Now.ToString("yyyy-MM-dd");

    public string GetMonthPrefix(string date) => date[..7];

    public IEnumerable<string> GetLastNDays(int days)
    {
        for (var i = 0; i < days; i++)
        {
            yield return DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd");
        }
    }
}
