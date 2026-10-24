using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Tokenmetry.App.Controls;

/// <summary>
/// data:image/* base64 data URI → <see cref="IImage"/>（上传头像的渲染转换）：解码结果按 URI 串
/// 全局缓存——同一头像同时出现在目录行 / 统计页 / 弹层预览多个槽位，64×64 PNG 只解码一次。
/// 解析失败（脏 data URI）缓存 null，槽位诚实空置，不伪造字母 / 色块兜底。
/// </summary>
public sealed class DataUriImageConverter : IValueConverter
{
    public static DataUriImageConverter Instance { get; } = new();

    private static readonly ConcurrentDictionary<string, IImage?> Cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string uri && uri.StartsWith("data:image/", StringComparison.Ordinal)
            ? Cache.GetOrAdd(uri, Decode)
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("头像 data URI 是单向数据（保存于 settings.json），不支持回转。");

    private static IImage? Decode(string data)
    {
        try
        {
            var base64 = data[(data.IndexOf(',') + 1)..];
            using var stream = new MemoryStream(System.Convert.FromBase64String(base64));
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            // 非法 base64 / 不支持格式：诚实空置（上传侧已只产 64×64 PNG，此路径仅防手改脏值）。
            return null;
        }
    }
}
