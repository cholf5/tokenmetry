using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

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
/// 统计口径区单行（S-002 定案）：本版全部为说明项——逐项评估后没有可真实生效的用户开关
/// （要么是管道/计价的始终生效行为，要么尚未实现），落 AppSettings 即假开关（卡内红线），
/// 故以「徽标 + 说明」样式呈现：<see cref="Badge"/> ∈ {始终生效, 未提供}。
/// </summary>
public sealed record CaliberItem(string Text, string Badge, string Note);

/// <summary>
/// 数据源表行 VM（S-002）：数据全部来自采集扫描注册表的快照（真实可得，见卡 Dev Log 来源表），
/// 无行级可变状态——每次进入页面 / 重扫完成后整表重建。状态类布尔驱动 XAML Classes 条件绑定
/// （status-ok / status-partial / status-nodata / status-pending，颜色复用既有强调色令牌）。
/// </summary>
public sealed record DataSourceRowViewModel(
    string Agent,
    string StatusText,
    bool IsOk,
    bool IsPartial,
    bool IsNoData,
    bool IsError,
    bool IsPending,
    string LastScanText,
    string RecordCountText)
{
    public static DataSourceRowViewModel NotScannedYet(string agent) =>
        new(agent, Translations.Instance.StatusNotScanned, IsOk: false, IsPartial: false, IsNoData: false, IsError: false, IsPending: true, "—", "—");
}

/// <summary>
/// 模型聚合规则单行（AliasRules）：原始模型名 → 规范名，展示侧附规范名的内置厂商图标
/// （聚合后统计里出现的是规范名，图标跟着规范名走）。不可变小对象，规则增删整表重建。
/// </summary>
public sealed class AliasRuleRowViewModel
{
    public AliasRuleRowViewModel(string alias, string target)
    {
        Alias = alias;
        Target = target;
        Icon = ModelVisualMap.Resolve(target);
    }

    /// <summary>原始模型名（settings.json 字典键原文，删除时按它精确移除）。</summary>
    public string Alias { get; }

    /// <summary>规范名（统计显示与聚合键）。</summary>
    public string Target { get; }

    /// <summary>规范名的内置厂商图标（恒非 null）。</summary>
    public ModelVisual Icon { get; }

    /// <summary>厂商变体名（ModelChip.Vendor 附加属性消费）。</summary>
    public string Vendor => Icon.Vendor;

    /// <summary>芯片字形。</summary>
    public char Glyph => Icon.Glyph;
}

public partial class SettingsViewModel : ViewModelBase, INavigationAware
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

    /// <summary>
    /// 统计口径区定案（S-002，逐项结论记录于卡 Dev Log）：原型 5 个复选项全部评估为说明项——
    /// 去重 / Token 四向拆分 / 未知成本纪律 / 仅本机处理为采集与计价管道的<b>始终生效</b>行为
    /// （无用户开关可接）；跨 Agent 合并<b>未提供</b>（未实现）。不做假开关。
    /// 文案每次访问经 Translations 现取（语言切换后 CaliberRows 重绑即拿到新语言）。
    /// </summary>
    public static IReadOnlyList<CaliberItem> CaliberItems
    {
        get
        {
            var t = Translations.Instance;
            return new[]
            {
                new CaliberItem(t.CaliberDedupText, t.BadgeAlways, t.CaliberDedupNote),
                new CaliberItem(t.CaliberSplitText, t.BadgeAlways, t.CaliberSplitNote),
                new CaliberItem(t.CaliberUnknownPriceText, t.BadgeAlways, t.CaliberUnknownPriceNote),
                new CaliberItem(t.CaliberMergeText, t.BadgeNotProvided, t.CaliberMergeNote),
                new CaliberItem(t.CaliberLocalOnlyText, t.BadgeAlways, t.CaliberLocalOnlyNote),
            };
        }
    }

    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;
    private readonly IThemeService _themeService;
    private readonly IIngestionScanReader _scanReader;
    private readonly BackgroundRefreshService _refreshService;
    private readonly IToastService _toastService;

    /// <summary>
    /// 查询服务（可空：测试/预览无查询环境时为 null，候选清单保持空集——诚实无候选）。
    /// 供模型聚合卡拉取本地数据出现过的原始模型名作候选项。
    /// </summary>
    private readonly IUsageQueryService? _query;

    /// <summary>模型名归一解析器（可空兜底新建空映射）：设置页改规则后热更新，统计页查询即时生效。</summary>
    private readonly ModelAliasResolver _aliasResolver;

    /// <summary>自动保存串行化 gate：文本框逐键触发的高频保存不并发写同一 settings.json（原子写语义不被破坏）。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    [ObservableProperty]
    private string _ccusageCommand = string.Empty;

    [ObservableProperty]
    private string _refreshMinutesText = string.Empty;

    [ObservableProperty]
    private bool _anomalyNotificationsEnabled;

    /// <summary>重扫生命周期内部状态码（scanning / scan finished / scan error，供重扫回归锁对账；
    /// 全部设置项改全自动保存后不再渲染，无 UI 出口）。</summary>
    [ObservableProperty]
    private string _status = "ready";

    /// <summary>刷新间隔的行内校验提示（非法输入时非空并显示；合法/初始为 null 隐藏）。</summary>
    [ObservableProperty]
    private string? _refreshMinutesError;

    /// <summary>重新扫描进行中（TC-001：按钮进入扫描态防重复触发；互斥在 BackgroundRefreshService）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonText))]
    private bool _isScanning;

    /// <summary>重扫按钮文案（如实语义：全局重扫，不支持 per-source）。</summary>
    public string ScanButtonText => IsScanning ? Translations.Instance.ScanRunning : Translations.Instance.ScanIdle;

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    /// <summary>数据源面板 meta 状态（Idle / Scanning / ScanFailed；文案经 Translations 实时取串，语言切换按当前状态重译）。</summary>
    public enum SourceMetaState
    {
        Idle,
        Scanning,
        ScanFailed,
    }

    private SourceMetaState _sourceMetaState = SourceMetaState.Idle;

    /// <summary>数据源面板 meta 文案：扫描中提示 / 静态口径说明 / 扫描失败提示（保持到下次成功扫描）。</summary>
    public string SourceMetaText => _sourceMetaState switch
    {
        SourceMetaState.Scanning => Translations.Instance.SourceMetaScanning,
        SourceMetaState.ScanFailed => Translations.Instance.SourceMetaScanFailed,
        _ => Translations.Instance.SourceMetaIdle,
    };

    private void SetSourceMetaState(SourceMetaState state)
    {
        if (_sourceMetaState == state)
        {
            return;
        }

        _sourceMetaState = state;
        OnPropertyChanged(nameof(SourceMetaText));
    }

    /// <summary>数据源表行（"已识别数据源"来自采集层注册表，不手写清单）。</summary>
    public ObservableCollection<DataSourceRowViewModel> SourceRows { get; } = new();

    /// <summary>模型聚合规则行（AliasRules，原始名 → 规范名；增删整表重建）。</summary>
    public ObservableCollection<AliasRuleRowViewModel> AliasRules { get; } = new();

    /// <summary>无规则空态（规则列表让位提示行）。</summary>
    public bool HasNoAliasRules => AliasRules.Count == 0;

    /// <summary>新增规则的原始模型名（AutoCompleteBox：候选来自本地数据出现过的原始名，也可自由输入）。</summary>
    [ObservableProperty]
    private string _newAliasSource = string.Empty;

    /// <summary>新增规则的规范名（同款候选 + 自由输入；规范名也会进候选供后续规则复用）。</summary>
    [ObservableProperty]
    private string _newAliasTarget = string.Empty;

    /// <summary>新增规则的行内校验提示（非空并显示；合法/初始为 null 隐藏）。</summary>
    [ObservableProperty]
    private string? _aliasError;

    /// <summary>候选模型原始名（GetRawModelNamesAsync，本地数据全集按用量降序；加载失败保留旧值）。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _aliasCandidates = [];

    public SettingsViewModel(
        AppSettings settings,
        AppPaths paths,
        IAppSettingsStore store,
        IThemeService themeService,
        IIngestionScanReader scanReader,
        BackgroundRefreshService refreshService,
        IToastService toastService,
        IUsageQueryService? query = null,
        ModelAliasResolver? aliasResolver = null)
    {
        _settings = settings;
        _store = store;
        _themeService = themeService;
        _scanReader = scanReader;
        _refreshService = refreshService;
        _toastService = toastService;
        _query = query;
        _aliasResolver = aliasResolver ?? new ModelAliasResolver();
        _aliasResolver.UpdateAliases(settings.ModelAliases);
        CacheDbPath = paths.CacheDbPath;
        SettingsFilePath = paths.SettingsFilePath;
        _ccusageCommand = settings.CcusageCommand;
        _refreshMinutesText = settings.RefreshMinutes.ToString();
        _anomalyNotificationsEnabled = settings.AnomalyNotificationsEnabled;
        _selectedTheme = ResolveThemeOption(settings.Theme);
        _selectedLanguage = ResolveLanguageOption(settings.Language);
        RebuildSourceRows();
        RebuildAliasRules();
        _ = LoadAliasCandidatesAsync();

        // 语言切换重译订阅：设置页 VM 为 DI 单例（与外壳同生命周期），直订阅无需退订。
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    public string CacheDbPath { get; }
    public string SettingsFilePath { get; }

    /// <summary>页面描述符（原型 #page-settings：标题与副标题，与导航项同源）。</summary>
    public PageDescriptor Page { get; } = ShellPages.For(PageId.Settings);

    /// <summary>口径区行（静态定案数据，实例属性便于绑定）。</summary>
    public IReadOnlyList<CaliberItem> CaliberRows => CaliberItems;

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
    /// 「每次切换一次」的信号（同 stupid-dict 约定）——刷新下拉选项 Label、重算命令式文案、
    /// 重建数据源表并重绑口径区。XAML 直绑 Translations 的串由 Translations 的全量
    /// PropertyChanged 自行刷新；页头 Page.Title 由 PageDescriptor 自行发通知（叶子层）。
    /// 瞬态 Status（内部状态码）与 toast 保持出现时语言，不回溯重译。
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

        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(SourceMetaText));
        OnPropertyChanged(nameof(CaliberRows));
        RebuildSourceRows();
        RefreshAliasErrorTranslation();
    }

    /// <summary>
    /// 单例页重进刷新（INavigationAware）：外壳每次导航进入设置页后重建数据源表，
    /// 使后台定时刷新 / 上一页期间的扫描结果在重进时可见（生命周期定案见卡 Dev Log）；
    /// 模型候选清单同步重拉（新扫描后数据里可能出现新模型名）。
    /// </summary>
    public void OnNavigatedTo()
    {
        RebuildSourceRows();
        _ = LoadAliasCandidatesAsync();
    }

    /// <summary>
    /// TC-001 重新扫描（全局）：采集管道不支持 per-source 重扫（单飞行串行全源管道，
    /// 定案见卡 Dev Log），按钮语义如实为「重新扫描（全部源）」，经
    /// BackgroundRefreshService.TriggerRefreshAsync 触发——与定时器刷新同一容器实例、同一单飞槽
    /// （防重复触发 + 触发合并，见审查修复轮 P2-2 定案），已有刷新在跑时并入该轮等待其真实结果。
    /// 完成后重建数据源表 + toast；成败经返回值区分（P2-1）——失败时如实报「扫描失败」并在
    /// 数据源面板给出"数据可能非本轮"行级提示，不做假成功。
    /// </summary>
    [RelayCommand]
    public async Task RescanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        SetSourceMetaState(SourceMetaState.Scanning);
        Status = "scanning";
        try
        {
            // TriggerRefreshAsync 不抛异常（内部吞异常转 bool，定时器/首刷路径保持静默语义）；
            // 设置页路径观察返回值区分成败。
            var success = await _refreshService.TriggerRefreshAsync();
            RebuildSourceRows();
            if (success)
            {
                Status = "scan finished";
                SetSourceMetaState(SourceMetaState.Idle);
                _toastService.Show(Translations.Instance.ToastScanDone);
            }
            else
            {
                Status = "scan error";
                SetSourceMetaState(SourceMetaState.ScanFailed);
                _toastService.Show(Translations.Instance.ToastScanFailed);
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>从采集扫描注册表重建数据源表（注册序 = 管道扫描序；未扫描的源如实显示"尚未扫描"）。</summary>
    private void RebuildSourceRows()
    {
        var rows = _scanReader.RegisteredSources
            .Select(agent => _scanReader.GetLastScan(agent) is { } snapshot
                ? new DataSourceRowViewModel(
                    agent,
                    DescribeStatus(snapshot.Status),
                    IsOk: snapshot.Status == JsonlScanStatus.Ok,
                    IsPartial: snapshot.Status == JsonlScanStatus.Partial,
                    IsNoData: snapshot.Status == JsonlScanStatus.NoData,
                    IsError: snapshot.Status == JsonlScanStatus.Error,
                    IsPending: false,
                    FormatLastScan(snapshot.ScannedAt),
                    snapshot.RecordCount.ToString("N0", CultureInfo.InvariantCulture))
                : DataSourceRowViewModel.NotScannedYet(agent))
            .ToList();

        SourceRows.Clear();
        foreach (var row in rows)
        {
            SourceRows.Add(row);
        }
    }

    /// <summary>扫描状态 → 展示文案（三态语义与适配器口径对齐：JsonlScanDiagnostics.Status；
    /// 行的状态着色由各 Is* 布尔经 Classes 条件绑定驱动，见 SharedStyles 的 status-* 类）。</summary>
    internal static string DescribeStatus(JsonlScanStatus status) => status switch
    {
        JsonlScanStatus.Ok => Translations.Instance.StatusOk,
        JsonlScanStatus.Partial => Translations.Instance.StatusPartial,
        JsonlScanStatus.NoData => Translations.Instance.StatusNoData,
        JsonlScanStatus.Error => Translations.Instance.StatusError,
        _ => Translations.Instance.StatusNotScanned,
    };

    // ════════════════════ 模型聚合（别名映射）：规则增删 + 热更新 + 候选清单 ════════════════════

    /// <summary>
    /// 新增映射规则：校验（两侧非空、不得自映射——解析器按原始名精确匹配，字面相同即无效果）
    /// → 写入 <see cref="AppSettings.ModelAliases"/>（同原始名覆盖旧规则，字典语义）→ 重建规则行 +
    /// 落盘 + 热更新解析器（统计页查询即时按新映射聚合）。原始名取输入框原文（trim 后），
    /// 用户可自由键入数据里还没有的名字，为将来出现该名字的来源做准备。
    /// </summary>
    [RelayCommand]
    private void AddAliasRule()
    {
        var source = NewAliasSource.Trim();
        var target = NewAliasTarget.Trim();
        if (source.Length == 0 || target.Length == 0)
        {
            AliasError = Translations.Instance.AliasErrorRequired;
            return;
        }

        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            AliasError = Translations.Instance.AliasErrorSame;
            return;
        }

        AliasError = null;
        _settings.ModelAliases[source] = target;
        ApplyAliasSettings();
        NewAliasSource = string.Empty;
        NewAliasTarget = string.Empty;
    }

    /// <summary>移除映射规则（按行的原始名精确移除）→ 重建 + 落盘 + 热更新。</summary>
    [RelayCommand]
    private void RemoveAliasRule(string? alias)
    {
        if (string.IsNullOrEmpty(alias) || !_settings.ModelAliases.Remove(alias))
        {
            return;
        }

        ApplyAliasSettings();
    }

    /// <summary>规则变更的公共出口：字典 → 规则行重建 + settings.json 落盘 + 解析器热更新。</summary>
    private void ApplyAliasSettings()
    {
        RebuildAliasRules();
        PersistSettings();
        _aliasResolver.UpdateAliases(_settings.ModelAliases);
    }

    /// <summary>从 AppSettings.ModelAliases 重建规则行（插入序 = 字典序，与 settings.json 一致）。</summary>
    private void RebuildAliasRules()
    {
        AliasRules.Clear();
        foreach (var (alias, target) in _settings.ModelAliases)
        {
            AliasRules.Add(new AliasRuleRowViewModel(alias, target));
        }

        OnPropertyChanged(nameof(HasNoAliasRules));
    }

    /// <summary>
    /// 候选模型原始名（本地数据全集、绕过别名归一）：进入设置页 / 构造时拉取。失败静默保留
    /// 旧清单（候选是辅助录入，不值得打断页面；下次进入重试）。internal 供测试对账
    /// （构造路径是 fire-and-forget，测试需要显式等待）。
    /// </summary>
    internal async Task LoadAliasCandidatesAsync()
    {
        if (_query is null)
        {
            return;
        }

        try
        {
            AliasCandidates = await _query.GetRawModelNamesAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 模型候选清单加载失败（保留旧值）：{ex.Message}");
        }
    }

    /// <summary>校验提示是即时文案（出现时语言），语言切换后重算当前输入的校验结果对齐其余行内提示。</summary>
    private void RefreshAliasErrorTranslation()
    {
        if (AliasError is null)
        {
            return;
        }

        var source = NewAliasSource.Trim();
        var target = NewAliasTarget.Trim();
        AliasError = source.Length == 0 || target.Length == 0
            ? Translations.Instance.AliasErrorRequired
            : string.Equals(source, target, StringComparison.Ordinal)
                ? Translations.Instance.AliasErrorSame
                : null;
    }

    /// <summary>扫描完成时刻 → 展示文案：当日显示 HH:mm，跨日显示 MM-dd HH:mm（本机时区）。</summary>
    internal static string FormatLastScan(DateTimeOffset scannedAt)
    {
        var local = scannedAt.LocalDateTime;
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
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
