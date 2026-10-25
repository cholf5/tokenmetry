using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// 顶栏时间范围选择器 VM（D-020）的 headless 验证：预设服务未注入时管理区诚实隐藏（选择面照常）、
/// 添加表单的状态机（种类→周期/天数、校验错误就地显示、成功收起表单）、PresetRows 随选项清单同步。
/// AddPreset 经 TimeRangePresetService（内部 UpdateTimeRangeOptions 走 Dispatcher），须 [AvaloniaFact]。
/// </summary>
public class TimeRangePickerViewModelTests
{
    private sealed class FakeStore : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public void Without_preset_service_manage_section_hidden_but_selection_works()
    {
        var filters = new FilterStateService();
        var vm = new TimeRangePickerViewModel(filters, presets: null);

        Assert.False(vm.IsManageAvailable);
        Assert.Same(filters, vm.FilterState);
        Assert.Same(TimeRangeOption.Default, filters.TimeRange); // 选择绑定面完好
    }

    [AvaloniaFact]
    public void Kind_options_default_to_month_current_period()
    {
        var vm = new TimeRangePickerViewModel(new FilterStateService(), null);

        Assert.Equal(4, vm.KindOptions.Count); // 滚动/单日/周/月 封闭词表
        Assert.Equal(RelativeRangeKind.Month, vm.SelectedKind!.Kind);
        Assert.Equal(0, vm.SelectedOffset!.Offset);
        Assert.True(vm.IsRollingKind is false);
    }

    [AvaloniaFact]
    public void Add_preset_via_form_persists_and_clears_error()
    {
        var filters = new FilterStateService();
        var settings = new AppSettings();
        var presets = new TimeRangePresetService(filters, settings, new FakeStore());
        presets.Load();
        var vm = new TimeRangePickerViewModel(filters, presets);

        vm.SelectedKind = vm.KindOptions.Single(k => k.Kind == RelativeRangeKind.Week);
        vm.SelectedOffset = vm.OffsetOptions.Single(o => o.Offset == -1);
        vm.AddPresetCommand.Execute(null);

        Assert.Null(vm.PresetError);
        Assert.False(vm.IsAddFormOpen);
        var preset = Assert.Single(settings.CustomTimePresets);
        Assert.Equal("Week", preset.Kind);
        Assert.Equal(-1, preset.Offset);
        Assert.Single(vm.PresetRows);
    }

    [AvaloniaFact]
    public void Add_rolling_preset_parses_days_text_and_surfaces_validation_error()
    {
        var filters = new FilterStateService();
        var settings = new AppSettings();
        var presets = new TimeRangePresetService(filters, settings, new FakeStore());
        presets.Load();
        var vm = new TimeRangePickerViewModel(filters, presets);
        vm.SelectedKind = vm.KindOptions.Single(k => k.Kind == RelativeRangeKind.RollingDays);
        vm.IsAddFormOpen = true; // 用户已展开添加表单

        vm.RollingDaysText = "1"; // 越界 → 错误就地显示、不落库
        vm.AddPresetCommand.Execute(null);
        Assert.NotNull(vm.PresetError);
        Assert.True(vm.IsAddFormOpen); // 失败不收起表单，就地改参重试
        Assert.Empty(settings.CustomTimePresets);

        vm.RollingDaysText = "14";
        vm.AddPresetCommand.Execute(null);
        Assert.Null(vm.PresetError);
        Assert.Equal(14, Assert.Single(settings.CustomTimePresets).Days);
    }

    [AvaloniaFact]
    public void Delete_preset_command_removes_row_and_selection_falls_back()
    {
        var filters = new FilterStateService();
        var settings = new AppSettings();
        var presets = new TimeRangePresetService(filters, settings, new FakeStore());
        presets.Load();
        var vm = new TimeRangePickerViewModel(filters, presets);

        vm.SelectedKind = vm.KindOptions.Single(k => k.Kind == RelativeRangeKind.Week);
        vm.AddPresetCommand.Execute(null);
        filters.TimeRange = filters.TimeRangeOptions.Single(o => o.IsPreset);

        vm.DeletePresetCommand.Execute(vm.PresetRows.Single().Id);

        Assert.Empty(vm.PresetRows);
        Assert.Same(TimeRangeOption.Default, filters.TimeRange);
    }
}
