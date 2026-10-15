using System.Text;

namespace Tokenmetry.Infrastructure.Antigravity;

/// <summary>
/// Antigravity protobuf blob 的最小手写字段解码器（I-021，逐条移植蓝本
/// ../ccusage/rust/adapters/antigravity/src/parser.rs 的 decode_fields / read_varint /
/// take_bytes / take_length_delimited / field_varint / field_bytes / field_bytes_all /
/// field_text；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）。
/// Spike 结论（卡片 Dev Log）：蓝本即手写解码、只读所需字段号，无需 protobuf 库——
/// 本类按蓝本 wire type 0/1/2/5 与溢出语义逐字移植，C# 侧以 byte[] 切片近似蓝图本生命周期借用。
/// 字段选取语义（蓝本同式）：varint / text 取<b>最后一个</b>重复（.rev().find_map）、
/// bytes 消息取<b>第一个</b>（find_map）、repeated bytes 全量保序（field_bytes_all）、
/// 文本须 UTF-8 可解码且去首尾空白后非空。
/// </summary>
public static class AntigravityProtoReader
{
    /// <summary>解码失败（蓝本 ProtoResult 错误串同拒绝面）。</summary>
    public sealed class ProtoDecodeException : Exception
    {
        public ProtoDecodeException(string message)
            : base(message)
        {
        }
    }

    public enum WireKind : byte
    {
        Varint = 0,
        Fixed64 = 1,
        Bytes = 2,
        Fixed32 = 5,
    }

    public readonly record struct Field(uint Number, WireKind Wire, ulong Varint, byte[] Bytes);

    /// <summary>解码整段 blob 为字段序列（蓝本 decode_fields：读到空为止，结构非法即异常）。</summary>
    public static List<Field> DecodeFields(byte[] blob)
    {
        var fields = new List<Field>();
        var position = 0;
        while (position < blob.Length)
        {
            var tag = ReadVarint(blob, ref position);
            if (tag >> 3 > uint.MaxValue)
            {
                throw new ProtoDecodeException("protobuf field number overflow");
            }

            var number = (uint)(tag >> 3);
            if (number == 0)
            {
                throw new ProtoDecodeException("protobuf field number is zero");
            }

            var wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    fields.Add(new Field(number, WireKind.Varint, ReadVarint(blob, ref position), []));
                    break;
                case 1:
                    fields.Add(new Field(number, WireKind.Fixed64, 0, TakeBytes(blob, ref position, 8)));
                    break;
                case 2:
                    {
                        var length = ReadVarint(blob, ref position);
                        if (length > int.MaxValue)
                        {
                            throw new ProtoDecodeException("protobuf length overflow");
                        }

                        fields.Add(new Field(number, WireKind.Bytes, 0, TakeBytes(blob, ref position, (int)length)));
                        break;
                    }
                case 5:
                    fields.Add(new Field(number, WireKind.Fixed32, 0, TakeBytes(blob, ref position, 4)));
                    break;
                default:
                    throw new ProtoDecodeException("unsupported protobuf wire type");
            }
        }

        return fields;
    }

    /// <summary>varint（蓝本 read_varint：最多 10 字节，第 10 字节 payload &gt; 1 即溢出）。</summary>
    public static ulong ReadVarint(byte[] blob, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift <= 63; shift += 7)
        {
            if (position >= blob.Length)
            {
                throw new ProtoDecodeException("truncated protobuf varint");
            }

            var byteValue = blob[position++];
            var payload = (ulong)(byteValue & 0x7f);
            if (shift == 63 && payload > 1)
            {
                throw new ProtoDecodeException("protobuf varint overflow");
            }

            value |= payload << shift;
            if ((byteValue & 0x80) == 0)
            {
                return value;
            }

            if (shift == 63)
            {
                throw new ProtoDecodeException("protobuf varint overflow");
            }
        }

        throw new ProtoDecodeException("protobuf varint overflow");
    }

    private static byte[] TakeBytes(byte[] blob, ref int position, int length)
    {
        if (blob.Length - position < length)
        {
            throw new ProtoDecodeException("truncated protobuf fixed-width value");
        }

        var value = new byte[length];
        Array.Copy(blob, position, value, 0, length);
        position += length;
        return value;
    }

    /// <summary>varint 字段（蓝本 field_varint）：最后一个重复胜出；类型不符或缺失返回 null。</summary>
    public static ulong? FieldVarint(List<Field> fields, uint number)
    {
        for (var index = fields.Count - 1; index >= 0; index--)
        {
            var field = fields[index];
            if (field.Number == number && field.Wire == WireKind.Varint)
            {
                return field.Varint;
            }
        }

        return null;
    }

    /// <summary>bytes 字段（蓝本 field_bytes）：第一个重复胜出；类型不符或缺失返回 null。</summary>
    public static byte[]? FieldBytes(List<Field> fields, uint number)
    {
        foreach (var field in fields)
        {
            if (field.Number == number && field.Wire == WireKind.Bytes)
            {
                return field.Bytes;
            }
        }

        return null;
    }

    /// <summary>repeated bytes 字段（蓝本 field_bytes_all）：全量保序。</summary>
    public static List<byte[]> FieldBytesAll(List<Field> fields, uint number)
    {
        var values = new List<byte[]>();
        foreach (var field in fields)
        {
            if (field.Number == number && field.Wire == WireKind.Bytes)
            {
                values.Add(field.Bytes);
            }
        }

        return values;
    }

    /// <summary>
    /// 文本字段（蓝本 field_text）：最后一个重复、须 UTF-8 可解码且去首尾空白后非空，否则 null
    /// （非法 UTF-8 静默回 null 与蓝本 from_utf8().ok() 同口径）。
    /// </summary>
    public static string? FieldText(List<Field> fields, uint number)
    {
        byte[]? raw = null;
        for (var index = fields.Count - 1; index >= 0; index--)
        {
            var field = fields[index];
            if (field.Number == number && field.Wire == WireKind.Bytes)
            {
                raw = field.Bytes;
                break;
            }
        }

        if (raw is null)
        {
            return null;
        }

        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(raw);
            var trimmed = text.Trim();
            return trimmed.Length == 0 ? null : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>google.protobuf.Timestamp 形状消息（蓝本 parse_timestamp_message）：秒 &gt; 0 才有效、
    /// 纳秒钳到 999_999_999、毫秒 = 秒 × 1000 + 纳秒 / 1e6（饱和算术）。</summary>
    public static long? ParseTimestampMessage(byte[] blob)
    {
        var fields = DecodeFields(blob);
        var secondsValue = FieldVarint(fields, 1);
        if (secondsValue is not { } seconds || seconds > long.MaxValue || seconds <= 0)
        {
            return null;
        }

        var nanos = Math.Min(FieldVarint(fields, 2) ?? 0, 999_999_999);
        return SaturatingAdd(SaturatingMultiply(unchecked((long)seconds), 1_000), (long)(nanos / 1_000_000));
    }

    public static long SaturatingAdd(long left, long right)
    {
        var result = left + right;
        if (left > 0 && right > 0 && result < 0)
        {
            return long.MaxValue;
        }

        if (left < 0 && right < 0 && result >= 0)
        {
            return long.MinValue;
        }

        return result;
    }

    public static long SaturatingMultiply(long left, long right)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        try
        {
            return checked(left * right);
        }
        catch (OverflowException)
        {
            return left > 0 ^ right > 0 ? long.MinValue : long.MaxValue;
        }
    }
}
