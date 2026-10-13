using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CCUsage.App.Services;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

namespace CCUsage.App.ViewModels;

/// <summary>外观主题选项（Value 对应 AppSettings.Theme 的取值）。</summary>
public sealed record ThemeOption(string Value, string Label);

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
        new(agent, "● 尚未扫描", IsOk: false, IsPartial: false, IsNoData: false, IsError: false, IsPending: true, "—", "—");
}

public partial class SettingsViewModel : ViewModelBase, INavigationAware
{
    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption("light", "日间"),
        new ThemeOption("dark", "夜间"),
        new ThemeOption("system", "跟随系统"),
    };

    /// <summary>
    /// 统计口径区定案（S-002，逐项结论记录于卡 Dev Log）：原型 5 个复选项全部评估为说明项——
    /// 去重 / Token 四向拆分 / 未知成本纪律 / 仅本机处理为采集与计价管道的<b>始终生效</b>行为
    /// （无用户开关可接）；跨 Agent 合并<b>未提供</b>（未实现）。不做假开关。
    /// </summary>
    public static IReadOnlyList<CaliberItem> CaliberItems { get; } = new[]
    {
        new CaliberItem("按 Agent + Session ID 去重", "始终生效",
            "消息级去重与会话事实按 agent + session_id 幂等 upsert，采集管道内建行为"),
        new CaliberItem("区分输入、输出、缓存读取与缓存写入", "始终生效",
            "统一记录与 facts 存储固定四向拆分 Token"),
        new CaliberItem("未知模型价格显示为「未知」", "始终生效",
            "缺价模型成本显式标记未知、绝不折算为 0（未知成本纪律）"),
        new CaliberItem("将不同 Agent 的疑似同一任务自动合并（谨慎启用）", "未提供",
            "跨 Agent 任务合并尚未实现；原型开关不落地，避免无效果的假开关"),
        new CaliberItem("所有原始记录默认仅在本机处理", "始终生效",
            "适配器直读本机日志与账本；联网仅拉取模型价格表，原始记录不出本机"),
    };

    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;
    private readonly IThemeService _themeService;
    private readonly IIngestionScanReader _scanReader;
    private readonly BackgroundRefreshService _refreshService;
    private readonly IToastService _toastService;

    [ObservableProperty]
    private string _ccusageCommand = string.Empty;

    [ObservableProperty]
    private string _refreshMinutesText = string.Empty;

    [ObservableProperty]
    private bool _anomalyNotificationsEnabled;

    [ObservableProperty]
    private bool _showDebugInfo;

    [ObservableProperty]
    private string _status = "ready";

    [ObservableProperty]
    private bool _isSaving;

    /// <summary>重新扫描进行中（TC-001：按钮进入扫描态防重复触发；互斥在 BackgroundRefreshService）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonText))]
    private bool _isScanning;

    /// <summary>重扫按钮文案（如实语义：全局重扫，不支持 per-source）。</summary>
    public string ScanButtonText => IsScanning ? "扫描中…" : "重新扫描（全部源）";

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    /// <summary>数据源面板 meta 文案：扫描中提示 / 静态口径说明 / 扫描失败提示（保持到下次成功扫描）。</summary>
    [ObservableProperty]
    private string _sourceMetaText = SourceMetaIdle;

    public const string SourceMetaIdle = "状态与记录数来自上次采集扫描 · 记录数为增量累计值";
    public const string SourceMetaScanning = "扫描中…（按源逐个执行，完成后统一刷新）";
    public const string SourceMetaScanFailed = "上次扫描未完成——下表为最近一次成功快照，可能非本轮数据";

    /// <summary>数据源表行（"已识别数据源"来自采集层注册表，不手写清单）。</summary>
    public ObservableCollection<DataSourceRowViewModel> SourceRows { get; } = new();

    public SettingsViewModel(
        AppSettings settings,
        AppPaths paths,
        IAppSettingsStore store,
        IThemeService themeService,
        IIngestionScanReader scanReader,
        BackgroundRefreshService refreshService,
        IToastService toastService)
    {
        _settings = settings;
        _store = store;
        _themeService = themeService;
        _scanReader = scanReader;
        _refreshService = refreshService;
        _toastService = toastService;
        CacheDbPath = paths.CacheDbPath;
        SettingsFilePath = paths.SettingsFilePath;
        _ccusageCommand = settings.CcusageCommand;
        _refreshMinutesText = settings.RefreshMinutes.ToString();
        _anomalyNotificationsEnabled = settings.AnomalyNotificationsEnabled;
        _showDebugInfo = settings.ShowDebugInfo;
        _selectedTheme = ResolveThemeOption(settings.Theme);
        RebuildSourceRows();
    }

    public string CacheDbPath { get; }
    public string SettingsFilePath { get; }

    /// <summary>页面描述符（原型 #page-settings：标题与副标题，与导航项同源）。</summary>
    public PageDescriptor Page { get; } = ShellPages.For(PageId.Settings);

    /// <summary>口径区行（静态定案数据，实例属性便于绑定）。</summary>
    public IReadOnlyList<CaliberItem> CaliberRows => CaliberItems;

    /// <summary>切换选项时立即预览效果；持久化仍由保存设置触发。</summary>
    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (value is not null)
        {
            _themeService.Apply(value.Value);
        }
    }

    /// <summary>
    /// 单例页重进刷新（INavigationAware）：外壳每次导航进入设置页后重建数据源表，
    /// 使后台定时刷新 / 上一页期间的扫描结果在重进时可见（生命周期定案见卡 Dev Log）。
    /// </summary>
    public void OnNavigatedTo()
    {
        RebuildSourceRows();
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
        SourceMetaText = SourceMetaScanning;
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
                SourceMetaText = SourceMetaIdle;
                _toastService.Show("扫描完成");
            }
            else
            {
                Status = "scan error";
                SourceMetaText = SourceMetaScanFailed;
                _toastService.Show("扫描失败：数据表保持最近一次成功快照");
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (!TryValidateRefreshMinutes(out var refreshMinutes, out var error))
        {
            Status = error;
            return;
        }

        IsSaving = true;
        Status = "saving";

        try
        {
            _settings.CcusageCommand = string.IsNullOrWhiteSpace(CcusageCommand)
                ? "ccusage"
                : CcusageCommand.Trim();
            _settings.RefreshMinutes = refreshMinutes;
            _settings.AnomalyNotificationsEnabled = AnomalyNotificationsEnabled;
            _settings.ShowDebugInfo = ShowDebugInfo;
            _settings.Theme = SelectedTheme?.Value ?? "system";

            await _store.SaveAsync(_settings);
            CcusageCommand = _settings.CcusageCommand;
            RefreshMinutesText = _settings.RefreshMinutes.ToString();
            Status = "settings saved";
            _toastService.Show("设置已保存");
        }
        catch (Exception ex)
        {
            Status = $"error: {ex.Message}";
            _toastService.Show($"保存失败：{ex.Message}");
        }
        finally
        {
            IsSaving = false;
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
        JsonlScanStatus.Ok => "● 已连接",
        JsonlScanStatus.Partial => "● 部分数据",
        JsonlScanStatus.NoData => "● 未检出",
        JsonlScanStatus.Error => "● 扫描失败（本轮跳过）",
        _ => "● 尚未扫描",
    };

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

    private bool TryValidateRefreshMinutes(out int refreshMinutes, out string error)
    {
        refreshMinutes = 0;
        error = string.Empty;

        if (!int.TryParse(RefreshMinutesText, out var parsed))
        {
            error = "refresh interval must be a number";
            return false;
        }

        if (parsed < 1 || parsed > 1440)
        {
            error = "refresh interval must be between 1 and 1440 minutes";
            return false;
        }

        refreshMinutes = parsed;
        return true;
    }
}
