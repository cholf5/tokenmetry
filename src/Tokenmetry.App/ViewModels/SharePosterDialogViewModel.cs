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

        Preview = Build();
    }

    /// <summary>
    /// 单日窗口只能出竖版：信息窗本质是一张 438px 竖卡片，横置要重排信息流，
    /// 已超出"不偏离框架"的界线（设计文档 §3.1）。
    /// </summary>
    public bool CanChooseLandscape => _rangeStart != _rangeEnd;

    public IReadOnlyList<SharePosterLayout> LayoutOptions { get; } =
        [SharePosterLayout.Portrait, SharePosterLayout.Landscape];

    public IReadOnlyList<SharePosterTheme> ThemeOptions { get; } =
        [SharePosterTheme.FollowApp, SharePosterTheme.Dark, SharePosterTheme.Light];

    public IReadOnlyList<SharePosterProjectMask> MaskOptions { get; } =
        [SharePosterProjectMask.Off, SharePosterProjectMask.Partial, SharePosterProjectMask.Full];

    public SharePosterViewModel Preview { get; private set; }

    public SharePosterLayout Layout
    {
        get => _layout;
        set
        {
            var next = !CanChooseLandscape ? SharePosterLayout.Portrait : value;
            if (SetProperty(ref _layout, next))
            {
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
