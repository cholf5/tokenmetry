using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 生成对话框的展示模型（P5 + 数据范围筛选）：持有四个海报选项（样式/版式/主题/雾化，任一变化
/// 同步重排预览），并订阅与主窗口共享的 <see cref="FilterStateService"/>——对话框里改筛选 = 改全局
/// （同一实例即双向同步，关窗后保留），防抖后经注入的 reload 委托按当前筛选重查一轮
/// <see cref="SharePosterDataBundle"/>，数据到达前旧预览保留显示（stale-while-revalidate）。
/// 范围不是选项：来自当前 Bundle（开窗首轮由外壳按当时筛选解析，之后随每轮重查更新）。
/// 选项即存：四个选项任一实际变化都回调 <c>onChoiceChanged</c>（真实路径回写 settings.json，
/// 下次打开接着上次的选择）；构造期只赋起值，不触发回调。
/// </summary>
public sealed class SharePosterDialogViewModel : ViewModelBase
{
    /// <summary>筛选变更到重查的防抖窗口：ComboBox 连续切换/自定义日期连点只查最后一轮。</summary>
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(300);

    private readonly FilterStateService? _filters;
    private readonly TimeRangePickerViewModel? _timePicker;
    private readonly Func<CancellationToken, Task<SharePosterDataBundle>>? _reload;
    private readonly Action<Func<Task>> _reloadScheduler;
    private readonly Func<string, ModelAvatarVisual?>? _modelIcons;
    private readonly Action<SharePosterStyle, SharePosterLayout, SharePosterTheme, SharePosterProjectMask>? _onChoiceChanged;

    /// <summary>当前数据轮（范围 + 快照输入的唯一真相）；重查成功后整轮替换。</summary>
    private SharePosterDataBundle _data;

    private SharePosterStyle _style;
    private SharePosterLayout _layout;
    private SharePosterTheme _theme;
    private SharePosterProjectMask _mask;

    // —— 筛选重查编排：防抖 + 按代取消 + 过代丢弃 ——

    private DispatcherTimer? _debounceTimer;
    private Func<Task>? _pendingReload;
    private CancellationTokenSource? _reloadCts;
    private int _reloadGeneration;
    private bool _detached;

    private bool _isUpdating;
    private string? _reloadError;

    // ComboBox 的选中项必须是选项列表里的同一批实例（不是枚举值）；枚举属性仍是唯一真相，
    // 两边在对方的 setter 里同步（测试与视图各用各的绑定面）。
    private SharePosterOption _selectedStyle;
    private SharePosterOption _selectedLayout;
    private SharePosterOption _selectedTheme;
    private SharePosterOption _selectedMask;

    public SharePosterDialogViewModel(
        SharePosterDataBundle initialData,
        SharePosterStyle initialStyle,
        SharePosterLayout initialLayout,
        SharePosterTheme initialTheme,
        SharePosterProjectMask initialMask,
        FilterStateService? filters = null,
        TimeRangePickerViewModel? timePicker = null,
        Func<CancellationToken, Task<SharePosterDataBundle>>? reload = null,
        Func<string, ModelAvatarVisual?>? modelIconResolver = null,
        Action<SharePosterStyle, SharePosterLayout, SharePosterTheme, SharePosterProjectMask>? onChoiceChanged = null,
        Action<Func<Task>>? reloadScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(initialData);
        _data = initialData;
        _style = initialStyle;
        _layout = initialLayout;
        _theme = initialTheme;
        _mask = initialMask;
        // 起值版式不在当前样式能力表内就收口（真实路径：单日范围缺省信息卡 + 设置默认横版），
        // 不让对话框一进来就展示一个"选项与版面不一致"的预览
        _layout = CoerceLayout(_style, _layout);

        _filters = filters;
        _timePicker = timePicker;
        _modelIcons = modelIconResolver;
        _onChoiceChanged = onChoiceChanged;
        if (filters is not null)
        {
            // 只有筛选绑定面、没有重查通路 = 对话框里改筛选静默失效——直接构造失败暴露错误
            _reload = reload ?? throw new ArgumentException(
                "filters 与 reload 必须成对提供（对话框里的筛选改动要能触发重查）", nameof(reload));
            _reloadScheduler = reloadScheduler ?? DebounceSchedule;
            filters.PropertyChanged += OnFilterPropertyChanged;
        }
        else
        {
            _reload = reload;
            _reloadScheduler = reloadScheduler ?? DebounceSchedule;
        }

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

    /// <summary>全局筛选绑定面（与主窗口顶栏同一实例——对话框里改，顶栏与各页同步；反方向同然）。
    /// 未注入（纯选项测试）时为 null，筛选行绑定落空但不抛。</summary>
    public FilterStateService? Filters => _filters;

    /// <summary>时间范围选择器绑定面（复用 D-020 控件；外壳传入与顶栏共享的实例）。</summary>
    public TimeRangePickerViewModel? TimePicker => _timePicker;

    /// <summary>筛选变更重查进行中（旧预览保留显示，完成或失败后复位）。</summary>
    public bool IsUpdating { get => _isUpdating; private set => SetProperty(ref _isUpdating, value); }

    /// <summary>最近一轮重查的就地错误（toast 会被连续失败轰炸；旧预览保留，下次筛选变更即清）。</summary>
    public string? ReloadError
    {
        get => _reloadError;
        private set
        {
            if (SetProperty(ref _reloadError, value))
            {
                OnPropertyChanged(nameof(HasReloadError));
            }
        }
    }

    /// <summary>重查失败说明行的显隐绑定。</summary>
    public bool HasReloadError => _reloadError is not null;

    /// <summary>
    /// 关窗解绑：退订全局筛选、停防抖计时器、取消在途重查。对话框 VM 每开一次新建，而
    /// <see cref="FilterStateService"/> 与应用同生命周期——不解绑会把整棵 VM（连同海报预览）
    /// 沿订阅钉在进程里，还会在关窗后继续触发重查。
    /// </summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        if (_filters is not null)
        {
            _filters.PropertyChanged -= OnFilterPropertyChanged;
        }

        _debounceTimer?.Stop();
        _reloadCts?.Cancel();
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
    /// 海报样式（信息卡 / 总览……）：与时间范围解耦的纯选项——筛选变更带来新数据轮也<b>不</b>改样式
    /// （范围只决定数据窗口）。切换时按新样式的能力表收口版式——合法组合的收口在样式侧做，
    /// 版式选项保持无状态。
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

    /// <summary>当前选项 + 当前数据轮的范围对应的请求（导出时用它生成文件名）。</summary>
    public SharePosterRequest CurrentRequest =>
        new(_layout, _theme, _mask, _data.RangeStart, _data.RangeEnd, _style);

    private SharePosterViewModel Build() => new(_data.Compose(CurrentRequest), _modelIcons);

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

    // ── 数据范围筛选 → 防抖重查（筛选与主窗口同步的写回路径）──

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 空串广播 = 语言切换全属性重译（不改数据口径，对话框又是模态的、语言变不了），不重查；
        // 其余只认会影响查询结果的六个维度（FilterStateService 的统一判据）。
        if (string.IsNullOrEmpty(e.PropertyName) || !FilterStateService.AffectsQueries(e.PropertyName))
        {
            return;
        }

        // 用户再次动手即视为重试：清掉上轮错误（失败说明行随新查询消失）
        ReloadError = null;
        _reloadScheduler(RunReload);
    }

    /// <summary>默认调度器：UI 线程 DispatcherTimer 防抖（FilterStateService 的属性通知保证在 UI
    /// 线程发出，这里不另做 marshal）。测试注入的调度器不经过这里，避免非 UI 线程碰计时器。</summary>
    private void DebounceSchedule(Func<Task> trigger)
    {
        _pendingReload = trigger;
        if (_debounceTimer is null)
        {
            _debounceTimer = new DispatcherTimer(ReloadDebounce, DispatcherPriority.Background, (_, _) =>
            {
                _debounceTimer!.Stop();
                var run = _pendingReload;
                _pendingReload = null;
                if (run is not null)
                {
                    _ = run();
                }
            });
        }

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private Task RunReload() => RunReloadAsync();

    /// <summary>
    /// 重查一轮数据并整轮替换。按代守卫：新一轮开始即作废旧代，旧数据迟到不覆盖新数据；
    /// 失败就地落 <see cref="ReloadError"/> 并保留旧预览（导出仍可用上一轮数据，口径与预览始终一致）。
    /// </summary>
    private async Task RunReloadAsync()
    {
        if (_detached || _reload is null)
        {
            return;
        }

        var generation = ++_reloadGeneration;
        _reloadCts?.Cancel();
        _reloadCts?.Dispose();
        _reloadCts = new CancellationTokenSource();
        IsUpdating = true;
        ReloadError = null;
        try
        {
            var bundle = await _reload(_reloadCts.Token);
            if (_detached || generation != _reloadGeneration)
            {
                return; // 已关窗 / 已有更新一轮：迟到的数据整轮丢弃，不覆盖新筛选的结果
            }

            _data = bundle;
            Refresh();
        }
        catch (OperationCanceledException)
        {
            // 过代取消是正常路径；IsUpdating 由最新一代的 finally 收尾
        }
        catch (Exception ex)
        {
            if (!_detached && generation == _reloadGeneration)
            {
                ReloadError = string.Format(Translations.Instance.SharePosterUpdateFailedFormat, ex.Message);
            }
        }
        finally
        {
            if (!_detached && generation == _reloadGeneration)
            {
                IsUpdating = false;
            }
        }
    }
}
