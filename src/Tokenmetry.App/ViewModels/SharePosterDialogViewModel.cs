using System;
using System.Collections.Generic;
using System.Linq;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 生成对话框的展示模型（P5）：持有五个选项，任一变化即重建预览快照。
/// 快照由注入的工厂产出——对话框不直接查库，避免和总览页各写一套口径。
/// 选项即存：四个选项任一实际变化都回调 <c>onChoiceChanged</c>（真实路径回写 settings.json，
/// 下次打开接着上次的选择）；构造期只赋起值，不触发回调。
/// </summary>
public sealed class SharePosterDialogViewModel : ViewModelBase
{
    private readonly Func<SharePosterRequest, SharePosterData> _snapshot;
    private readonly Func<string, ModelAvatarVisual?>? _modelIcons;
    private readonly Action<SharePosterStyle, SharePosterLayout, SharePosterTheme, SharePosterProjectMask>? _onChoiceChanged;
    private readonly DateOnly _rangeStart;
    private readonly DateOnly _rangeEnd;

    private SharePosterStyle _style;
    private SharePosterLayout _layout;
    private SharePosterTheme _theme;
    private SharePosterProjectMask _mask;

    // ComboBox 的选中项必须是选项列表里的同一批实例（不是枚举值）；枚举属性仍是唯一真相，
    // 两边在对方的 setter 里同步（测试与视图各用各的绑定面）。
    private SharePosterOption _selectedStyle;
    private SharePosterOption _selectedLayout;
    private SharePosterOption _selectedTheme;
    private SharePosterOption _selectedMask;

    public SharePosterDialogViewModel(
        SharePosterRequest initial,
        Func<SharePosterRequest, SharePosterData> snapshot,
        Func<string, ModelAvatarVisual?>? modelIconResolver = null,
        Action<SharePosterStyle, SharePosterLayout, SharePosterTheme, SharePosterProjectMask>? onChoiceChanged = null)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _modelIcons = modelIconResolver;
        _onChoiceChanged = onChoiceChanged;
        _rangeStart = initial.RangeStart;
        _rangeEnd = initial.RangeEnd;
        _style = initial.Style;
        _layout = initial.Layout;
        _theme = initial.Theme;
        _mask = initial.ProjectMask;
        // 起值版式不在当前样式能力表内就收口（真实路径：单日范围缺省信息卡 + 设置默认横版），
        // 不让对话框一进来就展示一个"选项与版面不一致"的预览
        _layout = CoerceLayout(_style, _layout);

        // 对话框自带一批选项实例（带独立 IsSelected，分段控件的 RadioButton 直接双向绑定它）
        StyleOptions = SharePosterOptions.CreateStyles();
        LayoutOptions = SharePosterOptions.CreateLayouts();
        ThemeOptions = SharePosterOptions.CreateThemes();
        MaskOptions = SharePosterOptions.CreateMasks();
        _selectedStyle = StyleOptions.First(o => o.Value == SharePosterOptions.KeyOf(_style));
        _selectedLayout = LayoutOptions.First(o => o.Value == SharePosterOptions.KeyOf(_layout));
        _selectedTheme = ThemeOptions.First(o => o.Value == SharePosterOptions.KeyOf(_theme));
        _selectedMask = MaskOptions.First(o => o.Value == SharePosterOptions.KeyOf(_mask));
        WireSelection(StyleOptions, o => Style = SharePosterOptions.ParseStyle(o.Value));
        WireSelection(LayoutOptions, o => Layout = SharePosterOptions.ParseLayout(o.Value));
        WireSelection(ThemeOptions, o => Theme = SharePosterOptions.ParseTheme(o.Value));
        WireSelection(MaskOptions, o => ProjectMask = SharePosterOptions.ParseMask(o.Value));
        SyncSelection();

        Preview = Build();
    }

    /// <summary>当前样式是否提供多种版式（决定版式分段组启用与否；能力表见 <see cref="SharePosterOptions.SupportedLayouts"/>）。</summary>
    public bool CanChooseLayout => SharePosterOptions.SupportedLayouts(Style).Count > 1;

    /// <summary>样式锁版式时的说明行文案；支持多种版式的样式为 null（无说明行）。</summary>
    public string? LayoutNoticeText => SharePosterOptions.LayoutNoticeOf(Style);

    /// <summary>是否显示版式说明行（告诉用户为什么，而不是静默禁用）。</summary>
    public bool ShowLayoutNotice => LayoutNoticeText is not null;

    /// <summary>版式收口：不在当前样式能力表内 → 首个受支持版式。</summary>
    private static SharePosterLayout CoerceLayout(SharePosterStyle style, SharePosterLayout layout)
    {
        var supported = SharePosterOptions.SupportedLayouts(style);
        return supported.Contains(layout) ? layout : supported[0];
    }

    public IReadOnlyList<SharePosterOption> StyleOptions { get; }

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

    /// <summary>枚举 → 分段控件选中态（选项被程序化改动时同步，如信息卡样式强制竖版）。</summary>
    private void SyncSelection()
    {
        foreach (var option in StyleOptions)
        {
            option.IsSelected = option.Value == SharePosterOptions.KeyOf(_style);
        }

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

    public SharePosterOption SelectedStyle
    {
        get => _selectedStyle;
        set
        {
            if (value is not null)
            {
                Style = SharePosterOptions.ParseStyle(value.Value);
            }
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

    /// <summary>
    /// 海报样式（信息卡 / 总览……）：与时间范围解耦的纯选项。切换时按新样式的能力表收口版式——
    /// 合法组合的收口在样式侧做，版式选项保持无状态。
    /// </summary>
    public SharePosterStyle Style
    {
        get => _style;
        set
        {
            if (SetProperty(ref _style, value))
            {
                Layout = CoerceLayout(value, _layout);

                _selectedStyle = StyleOptions.First(o => o.Value == SharePosterOptions.KeyOf(value));
                OnPropertyChanged(nameof(SelectedStyle));
                OnPropertyChanged(nameof(CanChooseLayout));
                OnPropertyChanged(nameof(LayoutNoticeText));
                OnPropertyChanged(nameof(ShowLayoutNotice));
                SyncSelection();
                Refresh();
                NotifyChoiceChanged();
            }
        }
    }

    public SharePosterLayout Layout
    {
        get => _layout;
        set
        {
            var next = CoerceLayout(_style, value);
            if (SetProperty(ref _layout, next))
            {
                _selectedLayout = LayoutOptions.First(o => o.Value == SharePosterOptions.KeyOf(next));
                OnPropertyChanged(nameof(SelectedLayout));
                SyncSelection();
                Refresh();
                NotifyChoiceChanged();
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
                NotifyChoiceChanged();
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
                NotifyChoiceChanged();
            }
        }
    }

    /// <summary>当前选项对应的请求（导出时用它生成文件名）。</summary>
    public SharePosterRequest CurrentRequest =>
        new(_layout, _theme, _mask, _rangeStart, _rangeEnd, _style);

    private SharePosterViewModel Build() => new(_snapshot(CurrentRequest), _modelIcons);

    /// <summary>四个选项任一实际变化后通知持有方（真实路径回写 settings.json——上次使用即下次默认）。
    /// 每组各报一次（样式切换连带版式收口时报两条）是可接受的幂等冗余；收口不改值时不报。</summary>
    private void NotifyChoiceChanged() =>
        _onChoiceChanged?.Invoke(_style, _layout, _theme, _mask);

    private void Refresh()
    {
        Preview = Build();
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(CurrentRequest));
    }
}
