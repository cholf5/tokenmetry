using Avalonia.Controls;
using Avalonia.Interactivity;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;

namespace Tokenmetry.App.Controls;

/// <summary>
/// 顶栏时间范围选择器（D-020）视图：档名按钮 + Flyout 弹层。交互契约——选中常规档即收弹层
/// （选项绑定在 SelectionChanged 同步传播，见 OnRangeSelectionChanged）；选中「自定义」档
/// 保持弹层开（起止日期选择器就在弹层内，收掉便无从编辑）。其余选择/管理逻辑全在
/// <see cref="TimeRangePickerViewModel"/>，代码后置只做弹层生命周期。
/// </summary>
public partial class TimeRangePicker : UserControl
{
    public TimeRangePicker()
    {
        InitializeComponent();
    }

    private void OnRangeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is TimeRangeOption { IsCustom: false })
        {
            RangeButton.Flyout?.Hide();
        }
    }
}
