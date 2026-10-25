using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;

namespace Tokenmetry.App.ViewModels;

/// <summary>添加预设表单的种类下拉项（Label 经 Translations 实时取串，语言切换整体重建）。</summary>
public sealed record TimePresetKindOption(RelativeRangeKind Kind, string Label);

/// <summary>添加预设表单的周期偏移下拉项：当前周期(0) / 上一周期(-1)。</summary>
public sealed record TimePresetOffsetOption(int Offset, string Label);

/// <summary>
/// 顶栏时间范围选择器（D-020）VM：FilterStateService 的选择绑定面 + 弹层开关 + 「我的预设」
/// 管理表单（种类/周期/天数 + 校验错误就地显示）。预设增删经 <see cref="TimeRangePresetService"/>
/// 单点落盘；预设服务未注入（测试隔离构造）时管理区诚实隐藏（IsManageAvailable=false），
/// 选择弹层照常可用。语言切换重建表单下拉标签并重发 PresetRows（行 Label 由选项自身 RefreshLabel）。
/// 生命周期 = 外壳 VM 单例同生命周期，Translations/FilterState 订阅无需退订。
/// </summary>
public partial class TimeRangePickerViewModel : ViewModelBase
{
    private readonly TimeRangePresetService? _presets;

    [ObservableProperty]
    private bool _isFlyoutOpen;

    [ObservableProperty]
    private TimePresetKindOption? _selectedKind;

    [ObservableProperty]
    private TimePresetOffsetOption? _selectedOffset;

    [ObservableProperty]
    private string _rollingDaysText = "14";

    [ObservableProperty]
    private string? _presetError;

    [ObservableProperty]
    private bool _isAddFormOpen;

    public TimeRangePickerViewModel(FilterStateService filters, TimeRangePresetService? presets)
    {
        FilterState = filters ?? throw new ArgumentNullException(nameof(filters));
        _presets = presets;

        RebuildFormOptions();
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
        // 预设清单随选项清单整体替换（装载/增删）而变，行集合同步重发；行 Label 重译由选项自身负责。
        filters.PropertyChanged += OnFilterPropertyChanged;
    }

    /// <summary>全局筛选状态（档位选择 / 自定义日期的绑定面）。</summary>
    public FilterStateService FilterState { get; }

    /// <summary>预设管理可用性：预设服务未注入时诚实隐藏管理区（选择弹层不受影响）。</summary>
    public bool IsManageAvailable => _presets is not null;

    /// <summary>种类下拉项（固定四种词表，Label 随语言重建）。</summary>
    public IReadOnlyList<TimePresetKindOption> KindOptions { get; private set; } = [];

    /// <summary>周期偏移下拉项（当前周期 / 上一周期；滚动天数种类不显示本下拉）。</summary>
    public IReadOnlyList<TimePresetOffsetOption> OffsetOptions { get; private set; } = [];

    /// <summary>管理区的预设行（FilterState.TimeRangeOptions 的预设子集，含行内删除）。</summary>
    public IReadOnlyList<TimeRangeOption> PresetRows =>
        FilterState.TimeRangeOptions.Where(o => o.IsPreset).ToList();

    /// <summary>当前种类是否为滚动天数（决定表单显示周期下拉还是天数输入）。</summary>
    public bool IsRollingKind => SelectedKind?.Kind == RelativeRangeKind.RollingDays;

    [RelayCommand]
    private void ToggleAddForm()
    {
        IsAddFormOpen = !IsAddFormOpen;
        if (!IsAddFormOpen)
        {
            PresetError = null;
        }
    }

    /// <summary>添加预设：解析天数（滚动种类）→ 服务校验落盘；错误就地显示，成功收起表单。</summary>
    [RelayCommand]
    private void AddPreset()
    {
        if (_presets is null || SelectedKind is null)
        {
            return;
        }

        var kind = SelectedKind.Kind;
        int days;
        if (kind == RelativeRangeKind.RollingDays)
        {
            if (!int.TryParse(RollingDaysText, out days))
            {
                PresetError = Translations.Instance.TimePresetErrorDays;
                return;
            }
        }
        else
        {
            days = RelativeRangeSpec.MinRollingDays; // 非滚动种类不消费天数（IsValid 忽略），占位合法值
        }

        var error = _presets.TryAdd(kind, SelectedOffset?.Offset ?? 0, days);
        if (error is not null)
        {
            PresetError = error;
            return;
        }

        PresetError = null;
        IsAddFormOpen = false;
        SelectedOffset = OffsetOptions.FirstOrDefault(o => o.Offset == 0);
        RollingDaysText = "14";
    }

    /// <summary>删除预设（按稳定 Id；服务侧幂等）。</summary>
    [RelayCommand]
    private void DeletePreset(string presetId) => _presets?.Remove(presetId);

    partial void OnSelectedKindChanged(TimePresetKindOption? value)
    {
        OnPropertyChanged(nameof(IsRollingKind));
        PresetError = null;
    }

    private void RebuildFormOptions()
    {
        KindOptions =
        [
            new(RelativeRangeKind.RollingDays, Translations.Instance.TimePresetKindRolling),
            new(RelativeRangeKind.SingleDay, Translations.Instance.TimePresetKindSingleDay),
            new(RelativeRangeKind.Week, Translations.Instance.TimePresetKindWeek),
            new(RelativeRangeKind.Month, Translations.Instance.TimePresetKindMonth),
        ];
        OffsetOptions =
        [
            new(0, Translations.Instance.TimePresetOffsetCurrent),
            new(RelativeRangeSpec.MinOffset, Translations.Instance.TimePresetOffsetPrevious),
        ];
        // 语言切换重建时按既有选中语义找回（无选中则默认"月·当前周期"，最常见的日历诉求）
        SelectedKind = KindOptions.FirstOrDefault(k => k.Kind == (SelectedKind?.Kind ?? RelativeRangeKind.Month));
        SelectedOffset = OffsetOptions.FirstOrDefault(o => o.Offset == (SelectedOffset?.Offset ?? 0));
    }

    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage))
        {
            RebuildFormOptions();
            OnPropertyChanged(nameof(PresetRows));
        }
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterStateService.TimeRangeOptions))
        {
            OnPropertyChanged(nameof(PresetRows));
        }
    }
}
