using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.Update;
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
/// 设置页 VM（S-004 起收缩为应用偏好：主题 / 界面语言 / 刷新间隔 / ccusage 命令 / 异常通知 / 关窗行为 /
/// 开机自启动 / 本地路径）。
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
    private readonly IAutoStartManager _autoStartManager;
    private readonly UpdateService _updateService;

    /// <summary>自动保存串行化 gate：文本框逐键触发的高频保存不并发写同一 settings.json（原子写语义不被破坏）。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    [ObservableProperty]
    private string _ccusageCommand = string.Empty;

    [ObservableProperty]
    private string _refreshMinutesText = string.Empty;

    [ObservableProperty]
    private bool _anomalyNotificationsEnabled;

    /// <summary>关窗行为开关（S-005）：true = 关窗最小化到托盘（默认）；false = 关窗即退出。消费者 = MainWindow.OnClosing。</summary>
    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    /// <summary>开机自启动开关（S-006，默认关）：OS 注册 = <see cref="IAutoStartManager"/>（Win Run 键 / macOS LaunchAgent / Linux autostart），
    /// 注册内容带 --autostart 参数（启动静默常驻托盘）。</summary>
    [ObservableProperty]
    private bool _autoStartOnBoot;

    /// <summary>刷新间隔的行内校验提示（非法输入时非空并显示；合法/初始为 null 隐藏）。</summary>
    [ObservableProperty]
    private string? _refreshMinutesError;

    /// <summary>更新检查状态行（I-039）：null 隐藏；内容随检查/下载结果整体切换（文案 key 见 Translations）。</summary>
    [ObservableProperty]
    private string? _updateStatusText;

    /// <summary>发现新版本时为 true：显示「下载更新包」与「打开 Release 页」两个按钮。</summary>
    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    public SettingsViewModel(
        AppSettings settings,
        AppPaths paths,
        IAppSettingsStore store,
        IThemeService themeService,
        IAutoStartManager autoStartManager,
        UpdateService? updateService = null)
    {
        _settings = settings;
        _store = store;
        _themeService = themeService;
        ArgumentNullException.ThrowIfNull(autoStartManager);
        _autoStartManager = autoStartManager;
        CacheDbPath = paths.CacheDbPath;
        SettingsFilePath = paths.SettingsFilePath;
        _ccusageCommand = settings.CcusageCommand;
        _refreshMinutesText = settings.RefreshMinutes.ToString();
        _anomalyNotificationsEnabled = settings.AnomalyNotificationsEnabled;
        _minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        _autoStartOnBoot = settings.AutoStartOnBoot;
        _selectedTheme = ResolveThemeOption(settings.Theme);
        _selectedLanguage = ResolveLanguageOption(settings.Language);
        // 更新检查服务为 DI 单例（与托盘菜单共享状态）；测试直接构造时缺省自建（不发网络——
        // 检查只在命令触发时发生）。构造即同步一次已知状态：托盘里查过「发现新版本」，再打开
        // 设置页能直接看到结论而不是空白。
        _updateService = updateService ?? new UpdateService(new UpdateChecker(), settings, store);
        RefreshUpdateStatusFromState();

        // 语言切换重译订阅：本页 VM 为 DI 单例（与外壳同生命周期），直订阅无需退订。
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    public string CacheDbPath { get; }
    public string SettingsFilePath { get; }

    /// <summary>「关于」区展示的当前版本，与更新检查同源（csproj &lt;Version&gt; → 程序集版本）。</summary>
    public string UpdateAppVersion => UpdateChecker.CurrentVersion.TrimStart('v');

    /// <summary>检查更新：结果整体切换状态行（发现新版本时同时亮出下载/打开 Release 页按钮）。
    /// 与托盘「检查更新」走同一 UpdateService 单例——任一处查完，另一处的标题/状态行同步反映。</summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateStatusText = Translations.Instance.UpdateChecking;
        try
        {
            await _updateService.CheckNowAsync();
        }
        catch (Exception ex)
        {
            // CheckAsync 正常不抛（网络失败转 Failed 结果）；这里兜真正的意外（磁盘/序列化等）。
            UpdateStatusText = string.Format(Translations.Instance.UpdateCheckFailed, ex.Message);
            return;
        }
        RefreshUpdateStatusFromState();
    }

    /// <summary>下载新版本的平台更新包到「下载」文件夹，完成后在文件管理器中显示（best-effort）。
    /// 进度经 <see cref="Progress{T}"/> 回传（生产在 UI 线程构造，回调自动回 UI 线程；无同步上下文
    /// 的测试环境则内联，无绑定无碍）。不做自更新替换——见 UpdateDownloadService 注释。</summary>
    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        UpdateStatusText = Translations.Instance.UpdateDownloading;
        var progress = new Progress<double>(p =>
            UpdateStatusText = string.Format(Translations.Instance.UpdateDownloadingPercent, (int)p));
        try
        {
            var path = await _updateService.DownloadLatestAsync(progress);
            _updateService.RevealDownloaded(path);
            UpdateStatusText = string.Format(Translations.Instance.UpdateDownloaded, path);
        }
        catch (Exception ex)
        {
            UpdateStatusText = string.Format(Translations.Instance.UpdateDownloadFailed, ex.Message);
        }
    }

    /// <summary>打开这次检查发现的 Release 页（浏览器；失败静默诊断——按钮之外托盘菜单同一出口）。</summary>
    [RelayCommand]
    private void OpenReleasePage() => _updateService.OpenReleasePage();

    /// <summary>把 UpdateService 当前状态翻译成状态行文案（检查/下载命令完成后与构造时各调一次）。</summary>
    private void RefreshUpdateStatusFromState()
    {
        IsUpdateAvailable = _updateService.State == UpdateState.Available;
        UpdateStatusText = _updateService.State switch
        {
            UpdateState.Available => string.Format(
                Translations.Instance.UpdateAvailableFormat, _updateService.LastResult?.LatestVersion),
            UpdateState.UpToDate => string.Format(
                Translations.Instance.UpdateUpToDate, UpdateChecker.CurrentVersion),
            UpdateState.Failed => string.Format(
                Translations.Instance.UpdateCheckFailed, _updateService.LastResult?.Error),
            _ => null,
        };
    }

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

    /// <summary>开机自启动开关（S-006）：先 OS 注册后落盘——OS 失败回滚开关显示（字段直写，不重入本回调）
    /// 且不落盘（settings 与 OS 状态保持一致）；成功即存（同关窗行为开关的自动保存语义）。启动侧对账
    /// （App 启动序列，仅 enabled 时）另负责应用更新换路径后的注册刷新。</summary>
    partial void OnAutoStartOnBootChanged(bool value)
    {
        try
        {
            _autoStartManager.SetEnabled(value);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 开机自启动{(value ? "注册" : "注销")}失败：{ex.Message}");
            _autoStartOnBoot = !value;
            OnPropertyChanged(nameof(AutoStartOnBoot));
            return;
        }

        _settings.AutoStartOnBoot = value;
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
