using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 头像方式枚举 ↔ RadioButton 选中态的双向转换（图标选择器的模式段选）：
/// Convert 比对绑定值与 ConverterParameter 枚举值；ConvertBack 在选中时回写参数值、
/// 取消选中时 DoNothing（组内其它 RadioButton 的取消不回写脏值）。
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static EnumEqualsConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && value.Equals(parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : Avalonia.Data.BindingOperations.DoNothing;
}
