using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 比例列附加属性（D-015）：把一组占比权重（星号值，如构成条三段的输入/输出/缓存百分比）翻译成
/// 挂载 Grid 的 ColumnDefinitions。XAML 对 <c>ColumnDefinitions</c> 直接绑定不受支持（XAML 编译器
/// 把它当内容-adder 属性，Binding/ReflectionBinding 均拒收），沿用 AgentChip 的附加属性先例：
/// VM 只给数据（VM 不解析视图结构），本属性把数据翻成列定义。权重 ≤0 → 0 宽列（段消失，轨道裸露）。
/// </summary>
public static class ProportionalColumns
{
    /// <summary>星号权重列表（null = 不动；每次变更整体重建列）。</summary>
    public static readonly AttachedProperty<IReadOnlyList<double>?> SharesProperty =
        AvaloniaProperty.RegisterAttached<Grid, IReadOnlyList<double>?>("Shares", typeof(ProportionalColumns));

    static ProportionalColumns()
    {
        SharesProperty.Changed.AddClassHandler<Grid>(OnSharesChanged);
    }

    public static IReadOnlyList<double>? GetShares(Grid grid) => grid.GetValue(SharesProperty);

    public static void SetShares(Grid grid, IReadOnlyList<double>? value) => grid.SetValue(SharesProperty, value);

    private static void OnSharesChanged(Grid grid, AvaloniaPropertyChangedEventArgs e)
    {
        var shares = e.NewValue as IReadOnlyList<double>;
        grid.ColumnDefinitions.Clear();
        if (shares is null)
        {
            return;
        }

        foreach (var share in shares)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0, share), GridUnitType.Star)));
        }
    }
}
