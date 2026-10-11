using System.Text.Json;

namespace CCUsage.Infrastructure.SqliteSource;

/// <summary>
/// SQLite 行内 JSON 载荷的 lenient 字段读取（蓝本 adapters/common/src/jsonl.rs lenient 助手的
/// JsonElement 等价移植，I-009 OpenCode 首建、I-017 Kilo 复用；语义）：
/// - <see cref="GetU64"/>（lenient_u64）：仅非负整数生效；u64 溢出钳到 long.MaxValue；其余 0；
/// - <see cref="GetI64"/>（lenient_i64）：仅整数生效（浮点 / 字符串 / 缺失 → null）；
/// - <see cref="GetF64"/>（lenient_f64）：任意 JSON 数值生效（字符串 / 缺失 → null）；
/// - <see cref="GetObject"/>（lenient_object）：对象才返回（其余 / 缺失 → null）；
/// - <see cref="GetNonEmptyString"/>（non_empty_string）：字符串去首尾空白后非空才生效。
/// </summary>
internal static class LenientJson
{
    public static long GetU64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        if (value.TryGetUInt64(out var unsigned))
        {
            return unsigned > long.MaxValue ? long.MaxValue : (long)unsigned;
        }

        return 0;
    }

    public static long? GetI64(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt64(out var parsed)
            ? parsed
            : null;
    }

    public static double? GetF64(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    public static JsonElement? GetObject(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    public static string? GetNonEmptyString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var trimmed = value.GetString()?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
