using System;
using System.Collections.Generic;
using System.Linq;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 生成对话框的展示模型（P5）：持有四个选项，任一变化即重建预览快照。
/// 快照由注入的工厂产出——对话框不直接查库，避免和总览页各写一套口径。
/// </summary>
public sealed class SharePosterDialogViewModel : ViewModelBase
{
    private readonly Func<SharePosterRequest, SharePosterData> _snapshot;
    private readonly Func<string, ModelAvatarVisual?>? _modelIcons;
    private readonly DateOnly _rangeStart;
    private readonly DateOnly _rangeEnd;

    private SharePosterLayout _layout;
    private SharePosterTheme _theme;
    private SharePosterProjectMask _mask;

    // ComboBox 的选中项必须是选项列表里的同一批实例（不是枚举值）；枚举属性仍是唯一真相，
    // 两边在对方的 setter 里同步（测试与视图各用各的绑定面）。
    private SharePosterOption _selectedLayout;
    private SharePosterOption _selectedTheme;
    private SharePosterOption _selectedMask;

    public SharePosterDialogViewModel(
        SharePosterRequest initial,
        Func<SharePosterRequest, SharePosterData> snapshot,
        Func<string, ModelAvatarVisual?>? modelIconResolver = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _modelIcons = modelIconResolver;
        _rangeStart = initial.RangeStart;
        _rangeEnd = initial.RangeEnd;
        _layout = initial.Layout;
        _theme = initial.Theme;
        _mask = initial.ProjectMask;
        // 单日窗口只能竖版：起值就收口，不让对话框一进来就展示一个"选项与版面不一致"的预览
        if (!CanChooseLandscape)
        {
            _layout = SharePosterLayout.Portrait;
        }

        // 对话框自带一批选项实例（带独立 IsSelected，分段控件的 RadioButton 直接双向绑定它）
        LayoutOptions = SharePosterOptions.CreateLayouts();
        ThemeOptions = SharePosterOptions.CreateThemes();
        MaskOptions = SharePosterOptions.CreateMasks();
        _selectedLayout = LayoutOptions.First(o => o.Value == SharePosterOptions.KeyOf(_layout));
        _selectedTheme = ThemeOptions.First(o => o.Value == SharePosterOptions.KeyOf(_theme));
        _selectedMask = MaskOptions.First(o => o.Value == SharePosterOptions.KeyOf(_mask));
        WireSelection(LayoutOptions, o => Layout = SharePosterOptions.ParseLayout(o.Value));
        WireSelection(ThemeOptions, o => Theme = SharePosterOptions.ParseTheme(o.Value));
        WireSelection(MaskOptions, o => ProjectMask = SharePosterOptions.ParseMask(o.Value));
        SyncSelection();

        Preview = Build();
    }

    /// <summary>
    /// 单日窗口只能出竖版：信息窗本质是一张 438px 竖卡片，横置要重排信息流，
    /// 已超出"不偏离框架"的界线（设计文档 §3.1）。
    /// </summary>
    public bool CanChooseLandscape => _rangeStart != _rangeEnd;

    /// <summary>单日窗口的说明行（版式被锁竖版时显示，告诉用户为什么而不是静默禁用）。</summary>
    public bool ShowSingleDayNotice => !CanChooseLandscape;

    public IReadOnlyList<SharePosterOption> LayoutOptions { get; }

    public IReadOnlyList<SharePosterOption> ThemeOptions { get; }

    public IReadOnlyList<SharePosterOption> MaskOptions { get; }

    /// <summary>分段控件：某一项被选中（RadioButton 勾上）时回写对应枚举。</summary>
    private static void WireSelection(IReadOnlyList<SharePosterOption> options, Action<SharePosterOption> onSelected)
    {
        foreach (var option in options)
        {
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SharePosterOption.IsSelected) && option.IsSelected)
                {
                    onSelected(option);
                }
            };
        }
    }

    /// <summary>枚举 → 分段控件选中态（选项被程序化改动时同步，如单日强制竖版）。</summary>
    private void SyncSelection()
    {
        foreach (var option in LayoutOptions)
        {
            option.IsSelected = option.Value == SharePosterOptions.KeyOf(_layout);
        }

        foreach (var option in ThemeOptions)
        {
            option.IsSelected = option.Value == SharePosterOptions.KeyOf(_theme);
        }

        foreach (var option in MaskOptions)
        {
            option.IsSelected = option.Value == SharePosterOptions.KeyOf(_mask);
        }
    }

    public SharePosterOption SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (value is not null)
            {
                Layout = SharePosterOptions.ParseLayout(value.Value);
            }
        }
    }

    public SharePosterOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is not null)
            {
                Theme = SharePosterOptions.ParseTheme(value.Value);
            }
        }
    }

    public SharePosterOption SelectedMask
    {
        get => _selectedMask;
        set
        {
            if (value is not null)
            {
                ProjectMask = SharePosterOptions.ParseMask(value.Value);
            }
        }
    }

    public SharePosterViewModel Preview { get; private set; }

    public SharePosterLayout Layout
    {
        get => _layout;
        set
        {
            var next = !CanChooseLandscape ? SharePosterLayout.Portrait : value;
            if (SetProperty(ref _layout, next))
            {
                _selectedLayout = LayoutOptions.First(o => o.Value == SharePosterOptions.KeyOf(next));
                OnPropertyChanged(nameof(SelectedLayout));
                SyncSelection();
                Refresh();
            }
        }
    }

    public SharePosterTheme Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                _selectedTheme = ThemeOptions.First(o => o.Value == SharePosterOptions.KeyOf(value));
                OnPropertyChanged(nameof(SelectedTheme));
                SyncSelection();
                Refresh();
            }
        }
    }

    public SharePosterProjectMask ProjectMask
    {
        get => _mask;
        set
        {
            if (SetProperty(ref _mask, value))
            {
                _selectedMask = MaskOptions.First(o => o.Value == SharePosterOptions.KeyOf(value));
                OnPropertyChanged(nameof(SelectedMask));
                SyncSelection();
                Refresh();
            }
        }
    }

    /// <summary>当前选项对应的请求（导出时用它生成文件名）。</summary>
    public SharePosterRequest CurrentRequest => new(_layout, _theme, _mask, _rangeStart, _rangeEnd);

    private SharePosterViewModel Build() => new(_snapshot(CurrentRequest), _modelIcons);

    private void Refresh()
    {
        Preview = Build();
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(CurrentRequest));
    }
}
