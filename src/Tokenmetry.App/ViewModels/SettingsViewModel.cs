using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.Update;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Sync;
using Tokenmetry.Infrastructure.Sync;
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

/// <summary>
/// 外观画板选项（Value 对应 AppSettings.ThemePalette / ThemePaletteCatalog.Id），机制同
/// <see cref="ThemeOption"/>。默认画板名经 Translations 取串；其余是画板专名——各语种同值
/// 不翻译（同语言名自名不翻译的惯例）。
/// </summary>
public sealed class PaletteOption : ObservableObject
{
    public PaletteOption(string value) => Value = value;

    public string Value { get; }

    public string Label => Value switch
    {
        ThemePaletteCatalog.DefaultPaletteId => Translations.Instance.ThemePaletteDefault,
        "nord" => "Nord",
        "dracula" => "Dracula",
        "solarized" => "Solarized",
        "gruvbox" => "Gruvbox",
        _ => Value,
    };

    /// <summary>语言切换后由 SettingsViewModel 统一触发（默认画板名随语言重译）。</summary>
    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>界面语言选项（Value 对应 AppSettings.Language 的取值，统一小写：system / zh / zh-hant /
/// en / ja / ko / pt-br / ru），机制同 <see cref="ThemeOption"/>。
/// 语言名按 stupid-dict 惯例经 Translations 显示——自名不翻译，任何界面语言下都显示各语种原名。</summary>
public sealed class LanguageOption : ObservableObject
{
    public LanguageOption(string value) => Value = value;

    public string Value { get; }

    public string Label => Value switch
    {
        "zh" => Translations.Instance.LanguageZh,
        "zh-hant" => Translations.Instance.LanguageZhHant,
        "en" => Translations.Instance.LanguageEn,
        "ja" => Translations.Instance.LanguageJa,
        "ko" => Translations.Instance.LanguageKo,
        "pt-br" => Translations.Instance.LanguagePtBr,
        "ru" => Translations.Instance.LanguageRu,
        "es" => Translations.Instance.LanguageEs,
        "de" => Translations.Instance.LanguageDe,
        "fr" => Translations.Instance.LanguageFr,
        _ => Translations.Instance.LanguageSystem,
    };

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// 设置页 VM（S-004 起收缩为应用偏好：主题 / 界面语言 / 刷新间隔 / 关窗行为 / 开机自启动 /
/// 本地路径）。
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

    /// <summary>外观画板选项（顺序即下拉展示顺序，来自 ThemePaletteCatalog.All，默认画板恒首位）。</summary>
    public static IReadOnlyList<PaletteOption> PaletteOptions { get; } =
        ThemePaletteCatalog.All.Select(p => new PaletteOption(p.Id)).ToArray();

    /// <summary>界面语言选项（顺序即下拉展示顺序：跟随系统 / 简体中文 / 繁體中文 / English /
    /// 日本語 / 한국어 / Português (Brasil) / Русский / Español / Deutsch / Français；D-025
    /// 起第二梯队 es/de/fr 一并首发）。</summary>
    public static IReadOnlyList<LanguageOption> LanguageOptions { get; } = new[]
    {
        new LanguageOption("system"),
        new LanguageOption("zh"),
        new LanguageOption("zh-hant"),
        new LanguageOption("en"),
        new LanguageOption("ja"),
        new LanguageOption("ko"),
        new LanguageOption("pt-br"),
        new LanguageOption("ru"),
        new LanguageOption("es"),
        new LanguageOption("de"),
        new LanguageOption("fr"),
    };

    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;
    private readonly IThemeService _themeService;
    private readonly IAutoStartManager _autoStartManager;
    private readonly UpdateService _updateService;

    /// <summary>「打开数据目录」的系统出口（生产 = <see cref="ShellLauncher.OpenFolder"/>；测试注入记录器）。</summary>
    private readonly Action<string> _openDataFolder;

    /// <summary>「打开日志文件夹」的系统出口（I-045；注入缝同上——测试断言目标路径；生产同一出口）。</summary>
    private readonly Action<string> _openLogFolder;

    /// <summary>自动保存串行化 gate：文本框逐键触发的高频保存不并发写同一 settings.json（原子写语义不被破坏）。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    [ObservableProperty]
    private string _refreshMinutesText = string.Empty;

    /// <summary>关窗行为开关（S-005）：true = 关窗最小化到托盘（默认）；false = 关窗即退出。消费者 = MainWindow.OnClosing。</summary>
    [ObservableProperty]
    private bool _minimizeToTrayOnClose;

    /// <summary>开机自启动开关（S-006，默认关）：OS 注册 = <see cref="IAutoStartManager"/>（Win Run 键 / macOS LaunchAgent / Linux autostart），
    /// 注册内容带 --autostart 参数（启动静默常驻托盘）。</summary>
    [ObservableProperty]
    private bool _autoStartOnBoot;

    /// <summary>采集文件日志开关（I-045，默认开）：消费面 = AppServices 装配的 IngestionFileLogger 单例。
    /// 切换即落盘；运行中的采集轮不受影响，重启后按落盘值装配（与刷新间隔同语义）。</summary>
    [ObservableProperty]
    private bool _fileLoggingEnabled;

    /// <summary>刷新间隔的行内校验提示（非法输入时非空并显示；合法/初始为 null 隐藏）。</summary>
    [ObservableProperty]
    private string? _refreshMinutesError;

    /// <summary>更新检查状态行（I-039）：null 隐藏；内容随检查/下载结果整体切换（文案 key 见 Translations）。</summary>
    [ObservableProperty]
    private string? _updateStatusText;

    /// <summary>发现新版本时为 true：显示「下载更新包」与「打开 Release 页」两个按钮。</summary>
    [ObservableProperty]
    private bool _isUpdateAvailable;

    /// <summary>更新包已下载并校验完成、等重启生效：显示「重启并更新」按钮。</summary>
    [ObservableProperty]
    private bool _isUpdateReadyToRestart;

    /// <summary>下载/校验进行中：亮出进度条（百分比由 <see cref="UpdateDownloadPercent"/> 驱动）。</summary>
    [ObservableProperty]
    private bool _isUpdateProgressVisible;

    [ObservableProperty]
    private double _updateDownloadPercent;

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    [ObservableProperty]
    private PaletteOption _selectedPalette;

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    // ── 分享海报默认值（P5b/P7）：只决定生成对话框打开时的起值，海报本身继承顶部栏筛选 ──
    [ObservableProperty]
    private SharePosterOption _sharePosterLayoutOption;

    [ObservableProperty]
    private SharePosterOption _sharePosterThemeOption;

    [ObservableProperty]
    private SharePosterOption _sharePosterMaskOption;

    public SettingsViewModel(
        AppSettings settings,
        AppPaths paths,
        IAppSettingsStore store,
        IThemeService themeService,
        IAutoStartManager autoStartManager,
        UpdateService? updateService = null,
        Action<string>? openDataFolderOverride = null,
        Action<string>? openLogFolderOverride = null,
        SyncStateService? syncStateService = null)
    {
        _settings = settings;
        _store = store;
        _themeService = themeService;
        ArgumentNullException.ThrowIfNull(autoStartManager);
        _autoStartManager = autoStartManager;
        AppDataDirectory = paths.AppDataDirectory;
        CacheDbPath = paths.CacheDbPath;
        SettingsFilePath = paths.SettingsFilePath;
        LogDirectory = paths.LogDirectory;
        _openDataFolder = openDataFolderOverride ?? ShellLauncher.OpenFolder;
        _openLogFolder = openLogFolderOverride ?? _openDataFolder;
        _refreshMinutesText = settings.RefreshMinutes.ToString();
        _minimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        _autoStartOnBoot = settings.AutoStartOnBoot;
        _fileLoggingEnabled = settings.FileLoggingEnabled;
        _selectedPalette = ResolvePaletteOption(settings.ThemePalette);
        // 模式显示按画板归口（单档画板强制唯一档显示，不回写存储偏好）；字段直赋不走
        // OnSelectedThemeChanged 的「应用 + 落盘」，与既有构造纪律一致。
        _selectedTheme = ResolveThemeOption(CoerceMode(ThemePaletteCatalog.Resolve(_selectedPalette.Value), settings.Theme));
        _selectedLanguage = ResolveLanguageOption(settings.Language);
        _sharePosterLayoutOption = SharePosterOptions.ForLayout(ParseStoredLayout(settings));
        _sharePosterThemeOption = SharePosterOptions.ForTheme(ParseStoredTheme(settings));
        _sharePosterMaskOption = SharePosterOptions.ForMask(ParseStoredMask(settings));
        // 更新检查服务为 DI 单例（与托盘菜单共享状态）；测试直接构造时缺省自建（不发网络——
        // 检查只在命令触发时发生）。构造即同步一次已知状态：托盘里查过「发现新版本」，再打开
        // 设置页能直接看到结论而不是空白。
        _updateService = updateService ?? new UpdateService(new UpdateChecker(), settings, store);
        RefreshUpdateStatusFromState();

        // 跳入口同步（首个公开发布前补）：本页 VM 是 DI 单例、不实现 INavigationAware——重进导航
        // 不会重建也不会重读状态。而升级的默认路径正是「启动 30 秒后静默周检 / 托盘菜单发现新版本
        // → 老用户进设置页点下载」，旧实现下那个按钮一直 IsVisible=false。订阅后托盘与后台周检
        // 的结果会自己跟过来（Changed 可能在后台线程触发，统一 marshal 回 UI 线程）。
        // 不退订：VM 与进程同生命周期（同 Translations 订阅惯例）。
        _updateService.Changed += OnUpdateServiceChanged;

        // 语言切换重译订阅：本页 VM 为 DI 单例（与外壳同生命周期），直订阅无需退订。
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;

        // 跨设备同步（relay 设计 §4.6）：服务为 DI 单例，VM 直订阅 Changed（可能后台线程触发，
        // 统一 marshal 回 UI）。构造即同步一次已知状态（后续以 RefreshSyncStateAsync 增量跟上），
        // 否则错过启动期 InitializeAsync 的 Changed 后设置页一直显示空同步态。
        _syncState = syncStateService;
        if (_syncState is not null)
        {
            _syncState.Changed += OnSyncStateChanged;
            SyncEndpointInput = settings.SyncEndpoint;
            IsSyncEnabled = _syncState.IsEnabled;
            _ = RefreshSyncStateAsync();
        }
    }

    public string CacheDbPath { get; }
    public string SettingsFilePath { get; }

    /// <summary>采集日志目录（I-045；「打开日志文件夹」目标；与数据目录同根的 logs/ 子目录）。</summary>
    public string LogDirectory { get; }

    /// <summary>两个文件共同的所在目录（「本地路径」区打开按钮的目标；缓存库与设置文件同目录）。</summary>
    public string AppDataDirectory { get; }

    /// <summary>「关于」区展示的当前版本，与更新检查同源（csproj &lt;Version&gt; → 程序集版本 → 检查器基线）。
    /// 走服务而不是静态属性：注入了基线的测试/嵌入场景下两处显示必须一致。</summary>
    public string UpdateAppVersion => _updateService.CurrentVersion.TrimStart('v');

    /// <summary>「关于」区版权行（I-041）：csproj &lt;Copyright&gt; → 程序集 AssemblyCopyright（Directory.Build.props 单一出处，不二次硬编码）。</summary>
    public string AboutCopyright { get; } =
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    /// <summary>「关于」区仓库主页与 LICENSE 直链（I-041）：与更新检查同源（UpdateAssets.Repository 单一出处）。</summary>
    public string AboutRepositoryUrl { get; } = UpdateAssets.RepositoryUrl;
    public string AboutLicenseUrl { get; } = UpdateAssets.LicenseUrl;

    /// <summary>Windows 已安装副本：「关于」区提示换成「下载后自动安装并重启」，而不是「解压覆盖」。
    /// 视图据此二选一绑哪条 hint（<c>IsVisible</c> + 取反）。</summary>
    public bool IsInstalledCopy => _updateService.CanSilentUpgrade;

    /// <summary>更新服务（DI 单例）：外壳的更新横幅与托盘菜单共享同一实例。</summary>
    internal UpdateService UpdateService => _updateService;

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

    /// <summary>下载新版本并准备好重启：下载 → SHA-256 校验 → 解包到目标同目录暂存区，然后等用户点
    /// 「重启并更新」（<see cref="RestartUpdateCommand"/>）。进度经 <see cref="Progress{T}"/> 回传
    /// （生产在 UI 线程构造，回调自动回 UI 线程；无同步上下文的测试环境则内联）。</summary>
    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        IsUpdateProgressVisible = true;
        UpdateDownloadPercent = 0;
        UpdateStatusText = Translations.Instance.UpdateDownloading;
        var progress = new Progress<double>(p =>
        {
            UpdateDownloadPercent = p;
            UpdateStatusText = string.Format(Translations.Instance.UpdateDownloadingPercent, (int)p);
        });
        try
        {
            await _updateService.DownloadAsync(progress);
            RefreshUpdateStatusFromState();
        }
        catch (UpdateStageUnavailableException ex)
        {
            // 目录不可写：包已下载并已打开所在位置，状态行如实说清楚（别让用户以为更新没下成）。
            RefreshUpdateStatusFromState();
            UpdateStatusText = string.Format(Translations.Instance.UpdateDownloaded, ex.ArchivePath);
        }
        catch (Exception ex)
        {
            RefreshUpdateStatusFromState();
            UpdateStatusText = string.Format(Translations.Instance.UpdateDownloadFailed, ex.Message);
        }
        finally
        {
            IsUpdateProgressVisible = false;
        }
    }

    /// <summary>重启并应用已下载的更新：替换文件（安装器 / 原子替换）后本进程退出，新版本自动拉起。</summary>
    [RelayCommand]
    private void RestartUpdate()
    {
        try
        {
            UpdateStatusText = Translations.Instance.UpdateInstalling;
            _updateService.RestartAndApply();
        }
        catch (Exception ex)
        {
            UpdateStatusText = string.Format(Translations.Instance.UpdateDownloadFailed, ex.Message);
            RefreshUpdateStatusFromState();
        }
    }

    /// <summary>打开这次检查发现的 Release 页（浏览器；失败静默诊断——按钮之外托盘菜单同一出口）。</summary>
    [RelayCommand]
    private void OpenReleasePage() => _updateService.OpenReleasePage();

    /// <summary>
    /// 「本地路径」区的打开按钮：在文件管理器中打开数据目录。这个面板本来就只为「去文件系统里看一眼」
    /// 而存在（备份数据库 / 排障 / 亲自核对库里没有会话内容），显示路径是手段，打开目录才是终点——
    /// 少这一步，用户只能照着屏幕抄路径。best-effort：失败只打诊断（ShellLauncher 内 catch），不弹框。
    /// 不提供「更改数据目录」：库路径固定，换目录会牵出迁移 / 权限 / 双实例一致性问题。
    /// </summary>
    [RelayCommand]
    private void OpenDataFolder() => _openDataFolder(AppDataDirectory);

    /// <summary>
    /// 「打开日志文件夹」（I-045）：用户报 issue 时的错误报告出口——日志里有可贴的 diagnós face。
    /// 目标是日志目录（从未写过的关闭态可能尚不存在，文件管理器的行为可接受；ShellLauncher 内 catch 静默）。
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder() => _openLogFolder(LogDirectory);

    /// <summary>更新服务状态变化（任意入口触发）→ 重算关于区状态行。可能来自后台线程，故走 <see cref="UiPost"/>。</summary>
    private void OnUpdateServiceChanged() => UiPost(RefreshUpdateStatusFromState);

    /// <summary>UI 线程 marshal 入口：生产走 Avalonia Dispatcher；测试注入内联执行（无 dispatcher 的宿主）。</summary>
    internal Action<Action> UiPost { get; set; } = PostToUiThread;

    private static void PostToUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>把 UpdateService 当前状态翻译成状态行文案（检查/下载命令完成后与构造时各调一次）。</summary>
    private void RefreshUpdateStatusFromState()
    {
        IsUpdateAvailable = _updateService.State == UpdateState.Available;
        IsUpdateReadyToRestart = _updateService.State == UpdateState.ReadyToRestart;
        UpdateStatusText = _updateService.State switch
        {
            UpdateState.Available => string.Format(
                Translations.Instance.UpdateAvailableFormat, _updateService.LastResult?.LatestVersion),
            UpdateState.UpToDate => string.Format(
                Translations.Instance.UpdateUpToDate, _updateService.CurrentVersion),
            UpdateState.Failed => string.Format(
                Translations.Instance.UpdateCheckFailed, _updateService.LastResult?.Error),
            UpdateState.Downloading => Translations.Instance.UpdateDownloading,
            UpdateState.ReadyToRestart => Translations.Instance.BannerReadyTitle,
            UpdateState.Restarting => Translations.Instance.UpdateInstalling,
            _ => null,
        };
    }

    // ── 分享海报（设置页三行 + 生成对话框的默认值）──

    /// <summary>版式选项（竖版 / 横版）。</summary>
    public IReadOnlyList<SharePosterOption> SharePosterLayoutOptions => SharePosterOptions.Layouts;

    /// <summary>主题选项（跟随应用 / 夜间 / 日间）。</summary>
    public IReadOnlyList<SharePosterOption> SharePosterThemeOptions => SharePosterOptions.Themes;

    /// <summary>项目名雾化档（显示 / 局部 / 全部）。</summary>
    public IReadOnlyList<SharePosterOption> SharePosterMaskOptions => SharePosterOptions.Masks;

    /// <summary>生成对话框的默认版式（脏值回落竖版）。</summary>
    public SharePosterLayout DefaultSharePosterLayout => ParseStoredLayout(_settings);

    /// <summary>生成对话框的默认主题（脏值回落跟随应用）。</summary>
    public SharePosterTheme DefaultSharePosterTheme => ParseStoredTheme(_settings);

    /// <summary>生成对话框的默认雾化档（脏值回落显示）。</summary>
    public SharePosterProjectMask DefaultSharePosterProjectMask => ParseStoredMask(_settings);

    /// <summary>设置页改版式即存（下次打开生成对话框生效）。</summary>
    partial void OnSharePosterLayoutOptionChanged(SharePosterOption value)
    {
        if (value is null)
        {
            return;
        }

        _settings.SharePosterLayout = value.Value;
        OnPropertyChanged(nameof(DefaultSharePosterLayout));
        PersistSettings();
    }

    /// <summary>设置页改默认主题即存（只影响此后打开的海报，不回溯已生成的海报）。</summary>
    partial void OnSharePosterThemeOptionChanged(SharePosterOption value)
    {
        if (value is null)
        {
            return;
        }

        _settings.SharePosterTheme = value.Value;
        OnPropertyChanged(nameof(DefaultSharePosterTheme));
        PersistSettings();
    }

    /// <summary>设置页改默认雾化档即存。</summary>
    partial void OnSharePosterMaskOptionChanged(SharePosterOption value)
    {
        if (value is null)
        {
            return;
        }

        _settings.SharePosterProjectMask = value.Value;
        OnPropertyChanged(nameof(DefaultSharePosterProjectMask));
        PersistSettings();
    }

    private static SharePosterLayout ParseStoredLayout(AppSettings settings) =>
        SharePosterOptions.ParseLayout(settings.SharePosterLayout);

    private static SharePosterTheme ParseStoredTheme(AppSettings settings) =>
        SharePosterOptions.ParseTheme(settings.SharePosterTheme);

    private static SharePosterProjectMask ParseStoredMask(AppSettings settings) =>
        SharePosterOptions.ParseMask(settings.SharePosterProjectMask);

    /// <summary>
    /// 生成对话框的默认样式：上次在对话框里选过的样式（选项即存，见 <see cref="ApplySharePosterLastUsed"/>）；
    /// null = 从未选过（新用户/旧 settings.json），打开时跟随范围（单日 → 信息卡、多日 → 总览）。
    /// 样式不进设置页（没有编辑入口），本属性只读。
    /// </summary>
    public SharePosterStyle? DefaultSharePosterStyle =>
        SharePosterOptions.ParseStyleOrNull(_settings.SharePosterStyle);

    /// <summary>生成对话框的默认页边距（上次拖的值，选项即存；越界值收口，设置页无编辑入口）。</summary>
    public double DefaultSharePosterMargin =>
        Math.Clamp(_settings.SharePosterMargin, SharePosterRequest.MinMargin, SharePosterRequest.MaxMargin);

    /// <summary>
    /// 生成对话框的选项即存（对话框里改样式/版式/主题/雾化/页边距都回写默认档——上次使用即下次默认，
    /// 设置页的三组 ComboBox 与之同步）。版式/主题/雾化走既有 setter：顺带发通知、幂等时短路；
    /// 样式/页边距设置页无编辑入口，单独写字段。最后统一落盘一次。
    /// </summary>
    public void ApplySharePosterLastUsed(
        SharePosterStyle style,
        SharePosterLayout layout,
        SharePosterTheme theme,
        SharePosterProjectMask mask,
        double margin)
    {
        _settings.SharePosterStyle = SharePosterOptions.KeyOf(style);
        _settings.SharePosterMargin = Math.Clamp(margin, SharePosterRequest.MinMargin, SharePosterRequest.MaxMargin);
        SharePosterLayoutOption = SharePosterOptions.ForLayout(layout);
        SharePosterThemeOption = SharePosterOptions.ForTheme(theme);
        SharePosterMaskOption = SharePosterOptions.ForMask(mask);
        PersistSettings();
    }

    /// <summary>页面描述符（标题与副标题，与导航项同源）。</summary>
    public PageDescriptor Page { get; } = ShellPages.For(PageId.Settings);

    /// <summary>当前画板信息（选项与目录按 Id 对应）。</summary>
    private ThemePaletteInfo SelectedPaletteInfo => ThemePaletteCatalog.Resolve(SelectedPalette?.Value);

    /// <summary>模式档位列表：单档画板（Nord/Dracula 只有夜间）只给唯一档，多档画板齐全。</summary>
    public IReadOnlyList<ThemeOption> EffectiveThemeOptions =>
        SelectedPaletteInfo.Modes.Count == 1 ? [ThemeOptions[1]] : ThemeOptions;

    /// <summary>模式选择是否可用（单档画板置灰——模式随画板固定，无选择意义）。</summary>
    public bool IsModeSelectionEnabled => SelectedPaletteInfo.Modes.Count > 1;

    /// <summary>画板切换：换字典应用并落盘；模式显示归口到新画板的档位但不回写存储的模式偏好
    /// （切回多档画板时用户原日间/夜间选择仍在）。字段直赋 + 手动通知，绕开
    /// OnSelectedThemeChanged 的「应用 + 落盘」，避免把归口值写进 settings.json。</summary>
    partial void OnSelectedPaletteChanged(PaletteOption value)
    {
        if (value is null)
        {
            return;
        }

        var palette = ThemePaletteCatalog.Resolve(value.Value);
        _settings.ThemePalette = palette.Id;

        var coerced = CoerceMode(palette, _settings.Theme);
        var modeOption = EffectiveThemeOptions.FirstOrDefault(o => o.Value == coerced) ?? EffectiveThemeOptions[0];
        if (!ReferenceEquals(_selectedTheme, modeOption))
        {
            _selectedTheme = modeOption;
            OnPropertyChanged(nameof(SelectedTheme));
        }
        OnPropertyChanged(nameof(EffectiveThemeOptions));
        OnPropertyChanged(nameof(IsModeSelectionEnabled));

        _themeService.Apply(palette.Id, _settings.Theme);
        PersistSettings();
    }

    /// <summary>模式切换：立即应用并落盘（全自动保存——预览即生效，无需两段式）。</summary>
    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (value is null)
        {
            return;
        }

        _themeService.Apply(_settings.ThemePalette, value.Value);
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

    /// <summary>关窗行为开关：勾选即存（消费者 = MainWindow.OnClosing 的关窗分派；下次关窗即按新值生效）。</summary>
    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        _settings.MinimizeToTrayOnClose = value;
        PersistSettings();
    }

    /// <summary>采集文件日志开关（I-045）：勾选即存（logger 读取设置；关闭后不再新增写入，重启后按落盘值装配）。</summary>
    partial void OnFileLoggingEnabledChanged(bool value)
    {
        _settings.FileLoggingEnabled = value;
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

        foreach (var option in PaletteOptions)
        {
            option.RefreshLabel();
        }

        foreach (var option in LanguageOptions)
        {
            option.RefreshLabel();
        }

        SharePosterOptions.RefreshLabels();
    }

    private static ThemeOption ResolveThemeOption(string? preference)
    {
        var normalized = (preference ?? "system").Trim().ToLowerInvariant();
        return ThemeOptions.FirstOrDefault(o => o.Value == normalized) ?? ThemeOptions[2];
    }

    /// <summary>画板偏好 → 选项（未知/空值如实回落默认画板，同 Theme/Language 的未知值处理）。</summary>
    private static PaletteOption ResolvePaletteOption(string? preference)
    {
        var normalized = (preference ?? ThemePaletteCatalog.DefaultPaletteId).Trim().ToLowerInvariant();
        return PaletteOptions.FirstOrDefault(o => o.Value == normalized) ?? PaletteOptions[0];
    }

    /// <summary>模式偏好按画板归口：单档画板强制唯一档；多档画板未知/空值回落 system。</summary>
    private static string CoerceMode(ThemePaletteInfo palette, string? preference)
    {
        if (palette.Modes.Count == 1)
        {
            return palette.Modes[0];
        }

        var normalized = (preference ?? "system").Trim().ToLowerInvariant();
        return normalized is "light" or "dark" or "system" ? normalized : "system";
    }

    /// <summary>语言偏好 → 选项（未知/空值如实回落「跟随系统」，同 Theme 的未知值处理）。</summary>
    private static LanguageOption ResolveLanguageOption(string? preference)
    {
        var normalized = (preference ?? "system").Trim().ToLowerInvariant();
        return LanguageOptions.FirstOrDefault(o => o.Value == normalized) ?? LanguageOptions[0];
    }

    // ── 跨设备同步区块（relay 设计 §4.6 / Task 12）──

    private readonly SyncStateService? _syncState;

    /// <summary>确认中的待移除设备行（内联确认面板，headless 可测——仓库无既有确认弹窗服务）。</summary>
    private SyncDeviceRowVM? _pendingRemoveRow;

    /// <summary>RefreshSyncStateAsync 回写投影时抑制 OnIsSyncEnabledChanged 的开关副作用。</summary>
    private bool _suppressSyncToggle;

    /// <summary>同步状态服务是否就位（headless 部分测试不带服务构造 → 区块整体隐藏）。</summary>
    public bool HasSyncState => _syncState is not null;

    /// <summary>同步开关（投影 = SyncStateService.IsEnabled；用户拨动 = 启用/关闭动作的入口）。</summary>
    [ObservableProperty]
    private bool _isSyncEnabled;

    [ObservableProperty]
    private string _syncEndpointInput = string.Empty;

    /// <summary>账号 token 输入（自建通道粘贴；明文 v1 落盘为设计已知取舍——同 settings.json 纪律）。</summary>
    [ObservableProperty]
    private string _syncTokenInput = string.Empty;

    /// <summary>通道选择：true = 官方实例（GitHub 登录），false = 自建中继（地址 + token）。</summary>
    [ObservableProperty]
    private bool _isOfficialMode;

    [ObservableProperty]
    private string _deviceNameInput = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SyncDeviceRowVM> _deviceRows = new();

    [ObservableProperty]
    private string _syncStatusText = string.Empty;

    [ObservableProperty]
    private string _lastSyncText = string.Empty;

    [ObservableProperty]
    private bool _isRemoveConfirmVisible;

    partial void OnIsSyncEnabledChanged(bool value)
    {
        // 投影回写（RefreshSyncStateAsync）不触发动作；用户拨动开关 = 启用/关闭动作。
        if (_suppressSyncToggle || _syncState is null)
        {
            return;
        }

        if (value)
        {
            _ = EnableSyncAsync();
        }
        else
        {
            _ = DisableSyncAsync();
        }
    }

    /// <summary>从 SyncStateService 装载区块状态（构造、Changed 回调、测试直调的统一入口）。</summary>
    internal async Task RefreshSyncStateAsync()
    {
        var syncState = _syncState;
        if (syncState is null)
        {
            return;
        }

        _suppressSyncToggle = true;
        try
        {
            IsSyncEnabled = syncState.IsEnabled;
            if (!string.IsNullOrEmpty(syncState.LocalDeviceName))
            {
                DeviceNameInput = syncState.LocalDeviceName;
            }

            SyncStatusText = syncState.IsSyncing
                ? Translations.Instance.SyncStatusSyncing
                : syncState.LastError is not null
                    ? Translations.Instance.SyncStatusError
                    : Translations.Instance.SyncStatusIdle;
            LastSyncText = syncState.LastRunAtUtc == default
                ? string.Empty
                : syncState.LastRunAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            var rows = new ObservableCollection<SyncDeviceRowVM>();
            if (syncState.IsEnabled)
            {
                var thisDevice = Translations.Instance.ThisDevice;
                foreach (var device in await syncState.ListDevicesAsync())
                {
                    var name = string.IsNullOrWhiteSpace(device.Name) ? device.DeviceId : device.Name;
                    var lastSeen = device.LastSeenAt == default
                        ? string.Empty
                        : device.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    rows.Add(new SyncDeviceRowVM(device.DeviceId, name, lastSeen, device.DeviceId == syncState.LocalDeviceId, thisDevice));
                }
            }

            DeviceRows = rows;

            // 排行榜子区装载（leaderboard 设计 §4.4）：启用时 GET 偏好回填（异常吞掉置错误文案，
            // 与区块现状一致）；关闭时清空输入——偏好唯一事实源在中继（不落 settings.json），
            // 无后端即无意义，子区随「已启用」分支整体隐藏。
            if (syncState.IsEnabled)
            {
                try
                {
                    ApplyLeaderboardPreference(await syncState.GetLeaderboardPreferenceAsync());
                }
                catch (Exception ex)
                {
                    SetSyncError(ex);
                }
            }
            else
            {
                _suppressLeaderboardToggle = true;
                try
                {
                    LeaderboardOptIn = false;
                }
                finally
                {
                    _suppressLeaderboardToggle = false;
                }

                LeaderboardDisplayNameInput = string.Empty;
            }
        }
        finally
        {
            _suppressSyncToggle = false;
        }
    }

    private void OnSyncStateChanged() =>
        Dispatcher.UIThread.Post(() => _ = RefreshSyncStateAsync());

    [RelayCommand]
    private async Task EnableSyncAsync(CancellationToken ct = default)
    {
        if (_syncState is null)
        {
            return;
        }

        try
        {
            if (IsOfficialMode)
            {
                await EnableViaGitHubAsync(ct);
                return;
            }

            await _syncState.EnableAsync(SyncEndpointInput.Trim(), SyncTokenInput.Trim(), ct);
        }
        catch (Exception ex)
        {
            SetSyncError(ex);
        }
    }

    /// <summary>官方通道：GitHub loopback 登录 → 中继换 token → 启用（端点 = 编译期官方常量）。</summary>
    private async Task EnableViaGitHubAsync(CancellationToken ct)
    {
        var endpoint = TokenmetryOfficial.OfficialRelayUrl;
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(TokenmetryOfficial.GitHubClientId) || _syncState is null)
        {
            SyncStatusText = Translations.Instance.SyncStatusError;
            LastSyncText = string.Empty;
            return;
        }

        var backend = new RelaySyncBackend(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, endpoint, "anonymous", _syncState.LocalDeviceId);
        var token = await GitHubSyncLogin.AcquireTokenAsync(backend, ct);
        if (string.IsNullOrEmpty(token))
        {
            return; // 用户放弃 / 超时 / 配置未就绪：状态保持未启用
        }

        await _syncState.EnableAsync(endpoint, token, ct);
    }

    [RelayCommand]
    private async Task DisableSyncAsync(CancellationToken ct = default)
    {
        if (_syncState is null)
        {
            return;
        }

        try
        {
            await _syncState.DisableAsync(ct);
        }
        catch (Exception ex)
        {
            SetSyncError(ex);
        }
    }

    /// <summary>官方模式登录按钮与开关启用共用同一流程（token 由交换流程带回后一并启用）。</summary>
    [RelayCommand]
    private Task LoginGitHubAsync(CancellationToken ct) => EnableViaGitHubAsync(ct);

    [RelayCommand]
    private async Task RenameDeviceAsync(CancellationToken ct = default)
    {
        if (_syncState is null || string.IsNullOrWhiteSpace(DeviceNameInput))
        {
            return;
        }

        try
        {
            await _syncState.RenameDeviceAsync(DeviceNameInput.Trim(), ct);
        }
        catch (Exception ex)
        {
            SetSyncError(ex);
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync(CancellationToken ct = default)
    {
        if (_syncState is null)
        {
            return;
        }

        try
        {
            await _syncState.RunNowAsync(ct);
        }
        catch (Exception ex)
        {
            SetSyncError(ex);
        }
    }

    [RelayCommand]
    private void RemoveDevice(SyncDeviceRowVM? row)
    {
        if (row is null || row.IsLocal)
        {
            // 本机不移除（解绑自身 = 转未登录态，由关闭同步承担——不做隐藏式的自我删除入口）。
            return;
        }

        _pendingRemoveRow = row;
        IsRemoveConfirmVisible = true;
    }

    [RelayCommand]
    private async Task ConfirmRemoveAsync(CancellationToken ct = default)
    {
        var row = _pendingRemoveRow;
        IsRemoveConfirmVisible = false;
        _pendingRemoveRow = null;
        if (row is null || _syncState is null)
        {
            return;
        }

        try
        {
            await _syncState.RemoveDeviceAsync(row.DeviceId, ct);
        }
        catch (Exception ex)
        {
            SetSyncError(ex);
        }
    }

    [RelayCommand]
    private void CancelRemove()
    {
        _pendingRemoveRow = null;
        IsRemoveConfirmVisible = false;
    }

    // ── 排行榜子区（leaderboard 设计 §4.4 / Task 5：同步区块内的 opt-in 开关 + 展示名 + 网页链接）──

    /// <summary>官网榜单页（「在网页查看」的目标；站点仓库 Task 6 的落地页同一路径）。</summary>
    internal const string LeaderboardUrl = "https://tokenmetry.app/leaderboard";

    /// <summary>RefreshSyncStateAsync / 保存回填投影时抑制 OnLeaderboardOptInChanged 的开关副作用
    /// （防误恢复，同 _suppressSyncToggle 的投影守卫）。</summary>
    private bool _suppressLeaderboardToggle;

    /// <summary>排行榜 opt-in（投影 = 服务端偏好）。拨开仅解锁名字输入与保存按钮（PUT 在保存）；
    /// 拨关 = 立即 PUT optIn=false 并清展示（服务端「关闭即删」语义的入口）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveLeaderboardCommand))]
    private bool _leaderboardOptIn;

    /// <summary>展示名输入（预填 GET 回来的生效名；保存时 trim，空 = null 用默认名）。</summary>
    [ObservableProperty]
    private string _leaderboardDisplayNameInput = string.Empty;

    /// <summary>偏好提交进行中（保存/拨关出站期间的防抖位；消费者 = SaveLeaderboardCommand.CanExecute）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveLeaderboardCommand))]
    private bool _leaderboardBusy;

    partial void OnLeaderboardOptInChanged(bool value)
    {
        // 投影回写（RefreshSyncStateAsync / 保存回填）不触发动作；用户拨动才走。
        if (_suppressLeaderboardToggle || _syncState is null)
        {
            return;
        }

        // 拨开 = 仅解锁名字输入与保存按钮（真正 PUT 在保存）；拨关 = 立即退出榜单。
        if (!value)
        {
            _ = SubmitLeaderboardPreferenceAsync(
                new LeaderboardPreferenceRequest(OptIn: false, DisplayName: null));
        }
    }

    /// <summary>保存排行榜偏好：PUT {OptIn=当前开关, DisplayName=输入 trim 或 null}；成功后用
    /// 响应生效名回写输入框并把状态行复位到就绪（失败经 SetSyncError 与区块同款）。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveLeaderboard))]
    private async Task SaveLeaderboardAsync(CancellationToken ct = default)
    {
        await SubmitLeaderboardPreferenceAsync(
            new LeaderboardPreferenceRequest(
                LeaderboardOptIn,
                DisplayName: string.IsNullOrWhiteSpace(LeaderboardDisplayNameInput)
                    ? null
                    : LeaderboardDisplayNameInput.Trim()),
            ct);
    }

    private bool CanSaveLeaderboard() => LeaderboardOptIn && !LeaderboardBusy && _syncState is not null;

    /// <summary>「在网页查看」：系统浏览器打开官网榜单页（best-effort——链接失败不打断设置页，
    /// 打最小诊断；Process.Start + UseShellExecute 模式同 GitHubSyncLogin.OpenBrowser）。</summary>
    [RelayCommand]
    private void OpenLeaderboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo(LeaderboardUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 打开排行榜网页失败（{LeaderboardUrl}）：{ex.Message}");
        }
    }

    /// <summary>偏好出站共用体：PUT 后按服务端回显投影（生效名回写输入框）并复位状态行。</summary>
    private async Task SubmitLeaderboardPreferenceAsync(LeaderboardPreferenceRequest request, CancellationToken ct = default)
    {
        var syncState = _syncState;
        if (syncState is null)
        {
            return;
        }

        LeaderboardBusy = true;
        try
        {
            ApplyLeaderboardPreference(await syncState.SetLeaderboardPreferenceAsync(request, ct));
            SyncStatusText = Translations.Instance.SyncStatusIdle;
        }
        catch (Exception ex)
        {
            SetSyncError(ex);
        }
        finally
        {
            LeaderboardBusy = false;
        }
    }

    /// <summary>服务端偏好 → 本地投影（守卫抑制开关副作用）。opt-in 时生效名回写输入框；opt-out 时
    /// 清空输入：服务端对 opt-out 的响应一律携带生效名回落（settings 自定义名已删 → 账号默认名，
    /// 永非 null），那是「不参与榜单时对外显示什么」的服务端口径，不是要回填输入框的值——
    /// 清展示是客户端语义（设计 §4.4「拨关 = 立即退出榜单并清展示」；测试 fake 已镜像服务端回落）。</summary>
    private void ApplyLeaderboardPreference(LeaderboardPreferenceResponse response)
    {
        _suppressLeaderboardToggle = true;
        try
        {
            LeaderboardOptIn = response.OptIn;
            LeaderboardDisplayNameInput = response.OptIn ? response.DisplayName ?? string.Empty : string.Empty;
        }
        finally
        {
            _suppressLeaderboardToggle = false;
        }
    }

    private void SetSyncError(Exception ex) =>
        SyncStatusText = $"{Translations.Instance.SyncStatusError}: {ex.Message}";
}

/// <summary>设备清单行（设置页同步区块；IsLocal 行以「本机」标记且无移除按钮）。</summary>
public sealed class SyncDeviceRowVM
{
    public SyncDeviceRowVM(string deviceId, string name, string lastSeenText, bool isLocal, string thisDeviceLabel)
    {
        DeviceId = deviceId;
        Name = name;
        LastSeenText = lastSeenText;
        IsLocal = isLocal;
        // 本机行名字缀「本机」标记（本机名可与远端重名）；远端行原样显示登记名。
        DisplayName = isLocal ? $"{name} · {thisDeviceLabel}" : name;
    }

    public string DeviceId { get; }

    public string Name { get; }

    public string DisplayName { get; }

    public string LastSeenText { get; }

    public bool IsLocal { get; }
}
