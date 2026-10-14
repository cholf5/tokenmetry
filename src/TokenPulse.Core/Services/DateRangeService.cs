using System.Globalization;

namespace TokenPulse.Core.Services;

public class DateRangeService
{
    // 日期串是 facts / CLI 输出之间的对账键，必须固定 invariant（格里历 + 0 补零），
    // 否则非格里历文化（如佛历）下与 mapper 的 invariant 日期失配。
    public virtual string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string GetMonthPrefix(string date) => date[..7];

    public IEnumerable<string> GetLastNDays(int days)
    {
        var today = DateTime.ParseExact(Today(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (var i = 0; i < days; i++)
        {
            yield return today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }
}
