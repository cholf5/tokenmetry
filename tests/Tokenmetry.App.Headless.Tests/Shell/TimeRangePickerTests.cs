using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// 顶栏时间范围选择器控件（D-020）的 headless 契约：Flyout 弹层真实打开、档位清单完整
/// （内置 8 + 自定义哨兵，预设装载后计数联动）、选中常规档即收层、选中「自定义」档保持
/// 弹层开（起止日期选择器就在弹层内，收掉便无从编辑——收口语义见 TimeRangePicker 注释）。
/// </summary>
public class TimeRangePickerTests
{
    private sealed class FakeStore : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (Window Window, TimeRangePicker Picker, TimeRangePickerViewModel Vm, FilterStateService Filters) CreateHost()
    {
        var filters = new FilterStateService();
        var presets = new TimeRangePresetService(filters, new AppSettings(), new FakeStore());
        presets.Load();
        var vm = new TimeRangePickerViewModel(filters, presets);
        var picker = new TimeRangePicker { DataContext = vm };
        var window = new Window { Content = picker };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (window, picker, vm, filters);
    }

    private static Flyout OpenFlyout(TimeRangePicker picker)
    {
        var button = picker.GetVisualDescendants().OfType<DropDownButton>().Single();
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return flyout;
    }

    [AvaloniaFact]
    public void Flyout_lists_full_option_set_and_selection_flows_to_filter_state()
    {
        var (window, picker, vm, filters) = CreateHost();

        var flyout = OpenFlyout(picker);
        var listBox = ((StackPanel)flyout.Content!).GetVisualDescendants().OfType<ListBox>().Single();

        // 预设服务装载后清单 = 内置 8 + 自定义哨兵（本用例未加预设）
        Assert.Equal(9, listBox.ItemCount);
        Assert.Same(TimeRangeOption.Default, listBox.SelectedItem);

        // 点选档位（经 ListBox 选中 → TwoWay 绑定回 FilterStateService）
        var item = Assert.IsType<ListBoxItem>(listBox.GetRealizedContainers().First(c =>
            c is ListBoxItem lbi && ((TimeRangeOption)lbi.DataContext!).Id == "ThisMonth"));
        item.IsSelected = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("ThisMonth", filters.TimeRange.Id);
        Assert.Equal("ThisMonth", vm.FilterState.TimeRange.Id);

        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Flyout_closes_on_regular_selection_but_stays_open_for_custom()
    {
        var (window, picker, _, filters) = CreateHost();

        var flyout = OpenFlyout(picker);
        var listBox = ((StackPanel)flyout.Content!).GetVisualDescendants().OfType<ListBox>().Single();

        // 常规档：选中即收层（选项绑定同步传播，见 TimeRangePicker.OnRangeSelectionChanged）
        var monthItem = Assert.IsType<ListBoxItem>(listBox.GetRealizedContainers().First(c =>
            c is ListBoxItem lbi && ((TimeRangeOption)lbi.DataContext!).Id == "Last30Days"));
        monthItem.IsSelected = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(flyout.IsOpen);

        // 自定义档：弹层保持开——起止日期选择器在弹层内，收掉便无从编辑
        var flyout2 = OpenFlyout(picker);
        var listBox2 = ((StackPanel)flyout2.Content!).GetVisualDescendants().OfType<ListBox>().Single();
        var customItem = Assert.IsType<ListBoxItem>(listBox2.GetRealizedContainers().First(c =>
            c is ListBoxItem lbi && ((TimeRangeOption)lbi.DataContext!).Id == "Custom"));
        customItem.IsSelected = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(flyout2.IsOpen);
        Assert.True(filters.IsCustomRange);
        // 自定义日期区（两个 DatePicker）已在弹层内容树中可见
        Assert.Equal(2, ((StackPanel)flyout2.Content!).GetVisualDescendants().OfType<DatePicker>().Count());

        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
