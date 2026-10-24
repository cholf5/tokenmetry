using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Controls;

/// <summary>
/// #RRGGBB 色串 → <see cref="IBrush"/>（图标选择器色盘格子的渲染转换）：按规范化的 hex 缓存
/// 画刷实例。色值是用户可选的"内容"（头像颜色），不是主题令牌——不随明暗翻转，VM 不持画刷
/// 纪律同 <see cref="ModelChip"/> 的就地着色。脏 hex 返回 null（格子空置，不伪造颜色）。
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static HexToBrushConverter Instance { get; } = new();

    private static readonly ConcurrentDictionary<string, IBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string hex && ModelAvatarVisual.TryNormalizeColor(hex, out var normalized)
            ? Cache.GetOrAdd(normalized, static h => new SolidColorBrush(Color.Parse(h)))
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("色盘格子是单向数据（调色板常量），不支持回转。");
}
