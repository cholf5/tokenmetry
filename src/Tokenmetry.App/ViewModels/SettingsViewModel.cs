using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 外观主题选项（Value 对应 AppSettings.Theme 的取值）。稳定实例 + 实时 Label（经 Translations 取串，
/// 语言切换由 SettingsViewModel 统一 RefreshLabel）——规避 Avalonia ComboBox 重建 ItemsSource 丢选中、
/// 选中项内容被快照的坑（同 stupid-dict 的 OptionItem 方案）。相等性只看 Value（SelectedItem 绑定比较稳定）。
/// </summary>
public sealed class ThemeOption : ObservableObject
{
    public ThemeOption(string value) => Value = value;

    public string Value { get; }

    public string Label => Value switch
    {
        "light" => Translations.Instance.ThemeLight,
        "dark" => Translations.Instance.ThemeDark,
        _ => Translations.Instance.ThemeSystem,
    };

    /// <summary>语言切换后由 SettingsViewModel 统一触发（Label 为计算属性，需显式通知刷新）。</summary>
    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>界面语言选项（Value 对应 AppSettings.Language 的取值：system / zh / en），机制同 <see cref="ThemeOption"/>。
/// 语言名按 stupid-dict 惯例经 Translations 显示（英文界面显示 "简体中文"/"English"，中文界面同理）。</summary>
public sealed class LanguageOption : ObservableObject
{
    public LanguageOption(string value) => Value = value;

    public string Value { get; }

    public string Label => Value switch
    {
        "zh" => Translations.Instance.LanguageZh,
        "en" => Translations.Instance.LanguageEn,
        _ => Translations.Instance.LanguageSystem,
    };

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// 设置页 VM（S-004 起收缩为应用偏好：主题 / 界面语言 / 刷新间隔 / ccusage 命令 / 异常通知 / 本地路径）。
/// 数据源、统计口径、模型目录与别名映射在数据与计价页（<see cref="DataPricingViewModel"/>）。
/// 全部设置项改完即生效并落盘（无保存按钮——对齐 stupid-dict；刷新间隔非法输入用行内提示兜住不落盘）。
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption("light"),
        new ThemeOption("dark"),
        new ThemeOption("system"),
    };

    /// <summary>界面语言选项（顺序即下拉展示顺序：跟随系统 / 简体中文 / English）。</summary>
    public static IReadOnlyList<LanguageOption> LanguageOptions { get; } = new[]
    {
        new LanguageOption("system"),
        new LanguageOption("zh"),
        new LanguageOption("en"),
    };

    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;
    private readonly IThemeService _themeService;

    /// <summary>自动保存串行化 gate：文本框逐键触发的高频保存不并发写同一 settings.json（原子写语义不被破坏）。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    [ObservableProperty]
    private string _ccusageCommand = string.Empty;

    [ObservableProperty]
    private string _refreshMinutesText = string.Empty;

    [ObservableProperty]
    private bool _anomalyNotificationsEnabled;

    /// <summary>关窗行为（S-005）：true = 关窗最小化到托盘（默认）；false = 关窗即退出。消费者 = MainWindow.OnClosing。</summary>
    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    /// <summary>刷新间隔的行内校验提示（非法输入时非空并显示；合法/初始为 null 隐藏）。</summary>
    [ObservableProperty]
    private string? _refreshMinutesError;

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    public SettingsViewModel(
        AppSettings settings,
        AppPaths paths,
        IAppSettingsStore store,
        IThemeService themeService)
    {
        _settings = settings;
        _store = store;
        _themeService = themeService;
        CacheDbPath = paths.CacheDbPath;
        SettingsFilePath = paths.SettingsFilePath;
        _ccusageCommand = settings.CcusageCommand;
        _refreshMinutesText = settings.RefreshMinutes.ToString();
        _anomalyNotificationsEnabled = settings.AnomalyNotificationsEnabled;
        _minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        _selectedTheme = ResolveThemeOption(settings.Theme);
        _selectedLanguage = ResolveLanguageOption(settings.Language);

        // 语言切换重译订阅：本页 VM 为 DI 单例（与外壳同生命周期），直订阅无需退订。
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    public string CacheDbPath { get; }
    public string SettingsFilePath { get; }

    /// <summary>页面描述符（标题与副标题，与导航项同源）。</summary>
    public PageDescriptor Page { get; } = ShellPages.For(PageId.Settings);

    /// <summary>主题切换：立即应用预览并落盘（全自动保存——预览即生效，无需两段式）。</summary>
    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (value is null)
        {
            return;
        }

        _themeService.Apply(value.Value);
        _settings.Theme = value.Value;
        PersistSettings();
    }

    /// <summary>语言切换：立即全局应用（含本页与外壳重译）并即时落盘——对齐 stupid-dict
    /// 的语言切换语义（改完即保存），避免重启后回到旧语言。</summary>
    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null)
        {
            return;
        }

        Translations.ApplyPreference(value.Value);
        _settings.Language = value.Value;
        PersistSettings();
    }

    /// <summary>刷新间隔输入：合法（1–1440）即存（中间合法值无害——刷新间隔在下次启动后台
    /// 刷新服务时生效），非法行内提示且不落盘（settings.json 保留上一个合法值）。</summary>
    partial void OnRefreshMinutesTextChanged(string value)
    {
        if (!int.TryParse(value, out var parsed))
        {
            RefreshMinutesError = Translations.Instance.RefreshIntervalMustBeNumber;
            return;
        }

        if (parsed is < 1 or > 1440)
        {
            RefreshMinutesError = Translations.Instance.RefreshIntervalOutOfRange;
            return;
        }

        RefreshMinutesError = null;
        _settings.RefreshMinutes = parsed;
        PersistSettings();
    }

    /// <summary>ccusage 命令输入：内容变化即存；空值按既有语义落默认命令 "ccusage"
    /// （原始文本保留在输入框，规范化只落盘；命令本身下次启动才被消费）。</summary>
    partial void OnCcusageCommandChanged(string value)
    {
        _settings.CcusageCommand = string.IsNullOrWhiteSpace(value) ? "ccusage" : value.Trim();
        PersistSettings();
    }

    /// <summary>异常通知开关：勾选即存（有真实行为出口——ISystemNotification → in-app toast）。</summary>
    partial void OnAnomalyNotificationsEnabledChanged(bool value)
    {
        _settings.AnomalyNotificationsEnabled = value;
        PersistSettings();
    }

    /// <summary>关窗行为开关：勾选即存（消费者 = MainWindow.OnClosing 的关窗分派；下次关窗即按新值生效）。</summary>
    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        _settings.MinimizeToTrayOnClose = value;
        PersistSettings();
    }

    /// <summary>
    /// 全自动保存（所有设置项改完即落盘，无保存按钮——对齐 stupid-dict 的设置语义）。
    /// 单飞行 gate 串行化：文本框逐键触发的高频保存不并发写同一 settings.json；
    /// best-effort——磁盘失败不阻断 UI 已生效的更改，打最小诊断（同外壳 Agent 选项失败
    /// 的处理），后续任一更改会继续尝试。
    /// </summary>
    private async Task PersistSettingsAsync()
    {
        try
        {
            await _saveGate.WaitAsync();
            try
            {
                await _store.SaveAsync(_settings);
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 设置自动保存失败（UI 已生效，待下次更改重试）：{ex.Message}");
        }
    }

    private void PersistSettings() => _ = PersistSettingsAsync();

    /// <summary>
    /// 语言切换重译（应用级单例，进程内存活）：以 PropertyChanged(CurrentLanguage) 作为
    /// 「每次切换一次」的信号（同 stupid-dict 约定）——刷新下拉选项 Label。
    /// XAML 直绑 Translations 的串由 Translations 的全量 PropertyChanged 自行刷新；
    /// 页头 Page.Title 由 PageDescriptor 自行发通知（叶子层）。
    /// </summary>
    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Translations.CurrentLanguage))
        {
            return;
        }

        foreach (var option in ThemeOptions)
        {
            option.RefreshLabel();
        }

        foreach (var option in LanguageOptions)
        {
            option.RefreshLabel();
        }
    }

    private static ThemeOption ResolveThemeOption(string? preference)
    {
        var normalized = (preference ?? "system").Trim().ToLowerInvariant();
        return ThemeOptions.FirstOrDefault(o => o.Value == normalized) ?? ThemeOptions[2];
    }

    /// <summary>语言偏好 → 选项（未知/空值如实回落「跟随系统」，同 Theme 的未知值处理）。</summary>
    private static LanguageOption ResolveLanguageOption(string? preference)
    {
        var normalized = (preference ?? "system").Trim().ToLowerInvariant();
        return LanguageOptions.FirstOrDefault(o => o.Value == normalized) ?? LanguageOptions[0];
    }
}
