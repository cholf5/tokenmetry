using System.Text;

namespace TokenPulse.Infrastructure.JsonSource;

/// <summary>
/// 严格 RFC3339 时间戳解析（蓝本 crates/ccusage-core/src/date_utils.rs parse_ts_timestamp 的逐条移植，
/// I-010 Gemini / I-014 Amp / I-015 Droid 共用；<see cref="Jsonl.JsonlTimestamp"/> 的宽松
/// DateTimeOffset.TryParse 语义不满足蓝本口径——蓝本对长度与字段位置做严格检查，缺时区、非 3 位毫秒、
/// 空格分隔等输入一律拒绝（调用方回退到文件 mtime 等 fallback），故单独移植）：
/// - 仅接受四种长度：20（无毫秒）、25（无毫秒 + 5 字符偏移）、24（.sss + Z）、29（.sss + 偏移），
///   第 19 位必须为 'Z'/'+'/'-'，24/29 形态第 19 位必须为 '.' 且毫秒恰为 3 位数字；
/// - 时区偏移允许 ±23:59（超出 .NET DateTimeOffset 的 ±14h 限制，故手工做毫秒算术而非 TimeSpan 构造）；
/// - 返回值为 UTC 时刻（入参文本中的偏移已换算）；入参不做去首尾空白（蓝本要求看到原始文本）。
/// 已知边界：蓝本 jiff 支持 year 0 而 DateTimeOffset 下限为 year 1，year 0000 输入返回 false
/// （真实日志不出现，登记为理论边界）。
/// </summary>
public static class StrictRfc3339Timestamp
{
    private const long MillisPerMinute = 60_000;
    private const long MillisPerHour = 3_600_000;
    private static readonly DateTimeOffset UnixEpoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>UNIX 纪元（文件 mtime 不可得时各蓝本 loader 的兜底时间戳）。</summary>
    public static DateTimeOffset Epoch => UnixEpoch;

    public static bool TryParse(string value, out DateTimeOffset timestamp)
    {
        timestamp = UnixEpoch;
        var bytes = Encoding.UTF8.GetBytes(value);

        long millis;
        int timezoneStart;
        switch (bytes.Length)
        {
            case 20 or 25 when bytes[19] is (byte)'Z' or (byte)'+' or (byte)'-':
                millis = 0;
                timezoneStart = 19;
                break;
            case 24 or 29 when bytes[19] == (byte)'.':
                var parsedMillis = ParseDigits(bytes, 20, 3);
                if (parsedMillis < 0)
                {
                    return false;
                }

                millis = parsedMillis;
                timezoneStart = 23;
                break;
            default:
                return false;
        }

        if (bytes[4] != (byte)'-' || bytes[7] != (byte)'-' || bytes[10] != (byte)'T'
            || bytes[13] != (byte)':' || bytes[16] != (byte)':')
        {
            return false;
        }

        var year = ParseDigits(bytes, 0, 4);
        var month = ParseDigits(bytes, 5, 2);
        var day = ParseDigits(bytes, 8, 2);
        var hour = ParseDigits(bytes, 11, 2);
        var minute = ParseDigits(bytes, 14, 2);
        var second = ParseDigits(bytes, 17, 2);
        if (year < 0 || month < 0 || day < 0 || hour < 0 || minute < 0 || second < 0)
        {
            return false;
        }

        if (hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        var offsetMinutes = ParseTimezoneOffset(bytes, timezoneStart);
        if (offsetMinutes is null)
        {
            return false;
        }

        // 蓝本 IsoDate::from_ymd 校验真实日历日；DateTime 构造同口径（year 0 被拒绝，见类型注释）。
        DateTime date;
        try
        {
            date = new DateTime((int)year, (int)month, (int)day, 0, 0, 0, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var epochMillis = (date.Ticks - UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond
                          + hour * MillisPerHour
                          + minute * MillisPerMinute
                          + second * 1_000
                          + millis
                          - offsetMinutes.Value * MillisPerMinute;

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(epochMillis);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>时区段解析（蓝本 parse_timezone_offset）：'Z' 为 0；否则 6 字节 ±HH:MM，
    /// 小时 ≤ 23、分钟 ≤ 59；返回"东偏为正"的分钟数（蓝本 '+' 取正、'-' 取负同口径）。</summary>
    private static long? ParseTimezoneOffset(byte[] bytes, int start)
    {
        if (bytes[start] == (byte)'Z')
        {
            return 0;
        }

        if (bytes.Length - start != 6
            || bytes[start] is not ((byte)'+' or (byte)'-')
            || bytes[start + 3] != (byte)':')
        {
            return null;
        }

        var hours = ParseDigits(bytes, start + 1, 2);
        var minutes = ParseDigits(bytes, start + 4, 2);
        if (hours < 0 || minutes < 0 || hours > 23 || minutes > 59)
        {
            return null;
        }

        var offset = hours * 60 + minutes;
        return bytes[start] == (byte)'+' ? offset : -offset;
    }

    /// <summary>定长 ASCII 数字段；含非数字字符返回 -1（蓝本 parse_digits 返回 None）。</summary>
    private static long ParseDigits(byte[] bytes, int start, int length)
    {
        long value = 0;
        for (var index = start; index < start + length; index++)
        {
            var byteValue = bytes[index];
            if (byteValue is < (byte)'0' or > (byte)'9')
            {
                return -1;
            }

            value = value * 10 + (byteValue - (byte)'0');
        }

        return value;
    }
}
