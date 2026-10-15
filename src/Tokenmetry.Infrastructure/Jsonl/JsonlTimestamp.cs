using System.Globalization;

namespace Tokenmetry.Infrastructure.Jsonl;

/// <summary>
/// 统一记录时间戳解析（各 agent 行解析器共用）：RFC3339，未知时区按 UTC 处理。
/// <see cref="Tokenmetry.Core.Models.UsageRecord.Timestamp"/> 为 DateTimeOffset，供 facts 与按日聚合直接消费。
/// </summary>
public static class JsonlTimestamp
{
    public static bool TryParse(string timestamp, out DateTimeOffset value)
    {
        return DateTimeOffset.TryParse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out value);
    }
}
