using System;
using System.Collections.Generic;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 生成对话框的展示模型（P5）：持有四个选项，任一变化即重建预览快照。
/// 快照由注入的工厂产出——对话框不直接查库，避免和总览页各写一套口径。
/// </summary>
public sealed class SharePosterDialogViewModel : ViewModelBase
{
    private readonly Func<SharePosterRequest, SharePosterData> _snapshot;
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
        Func<SharePosterRequest, SharePosterData> snapshot)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
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

        _selectedLayout = SharePosterOptions.ForLayout(_layout);
        _selectedTheme = SharePosterOptions.ForTheme(_theme);
        _selectedMask = SharePosterOptions.ForMask(_mask);

        Preview = Build();
    }

    /// <summary>
    /// 单日窗口只能出竖版：信息窗本质是一张 438px 竖卡片，横置要重排信息流，
    /// 已超出"不偏离框架"的界线（设计文档 §3.1）。
    /// </summary>
    public bool CanChooseLandscape => _rangeStart != _rangeEnd;

    /// <summary>单日窗口的说明行（版式被锁竖版时显示，告诉用户为什么而不是静默禁用）。</summary>
    public bool ShowSingleDayNotice => !CanChooseLandscape;

    public IReadOnlyList<SharePosterOption> LayoutOptions => SharePosterOptions.Layouts;

    public IReadOnlyList<SharePosterOption> ThemeOptions => SharePosterOptions.Themes;

    public IReadOnlyList<SharePosterOption> MaskOptions => SharePosterOptions.Masks;

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
                _selectedLayout = SharePosterOptions.ForLayout(next);
                OnPropertyChanged(nameof(SelectedLayout));
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
                _selectedTheme = SharePosterOptions.ForTheme(value);
                OnPropertyChanged(nameof(SelectedTheme));
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
                _selectedMask = SharePosterOptions.ForMask(value);
                OnPropertyChanged(nameof(SelectedMask));
                Refresh();
            }
        }
    }

    /// <summary>当前选项对应的请求（导出时用它生成文件名）。</summary>
    public SharePosterRequest CurrentRequest => new(_layout, _theme, _mask, _rangeStart, _rangeEnd);

    private SharePosterViewModel Build() => new(_snapshot(CurrentRequest));

    private void Refresh()
    {
        Preview = Build();
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(CurrentRequest));
    }
}
