using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// D-011 项目排行表单行（原型 #projectRows 六列：项目 / 会话数 / 总 Token / 估算成本 / 占比 / 平均每会话）。
/// 不可变小对象，由 <see cref="ProjectsViewModel"/> 整体替换集合触发绑定刷新。
/// 名称 = 归一化展示名（I-029 歧义守卫下可能就是原始路径——页面如实展示，ToolTip 恒给原始路径）；
/// 平均每会话 = TotalTokens / SessionCount（除零保护 → "—"，不伪造 0）。
/// token 列与均值列用紧凑格式（FormatCompactTokens，同 Agent/模型页与 KPI 卡口径）——token 量级
/// 无上界，定宽七/六列布局下千分位全数字会被裁切成错值，宁紧凑不裁切（精确值可下钻会话页）。
/// </summary>
public sealed class ProjectRankRowViewModel
{
    public ProjectRankRowViewModel(DimensionUsageRow row, bool canOpen)
    {
        Name = row.DisplayName ?? row.Key;
        RawPath = row.Key;
        CanOpen = canOpen;
        SessionCountText = row.SessionCount.ToString("N0", CultureInfo.InvariantCulture);
        TotalTokensText = TrendChart.FormatCompactTokens(row.TotalTokens);
        CostText = OverviewViewModel.FormatCost(row.Cost);
        ShareText = OverviewViewModel.FormatShare(row.TokenShare);
        AverageText = row.SessionCount > 0
            ? TrendChart.FormatCompactTokens((double)Math.Round((decimal)row.TotalTokens / row.SessionCount, MidpointRounding.AwayFromZero))
            : "—";
    }

    /// <summary>展示名（归一化项目名；歧义守卫下为原始路径——诚实不猜合并）。</summary>
    public string Name { get; }

    /// <summary>项目原始路径（I-029 稳定键；跳转会话页的筛选值）。</summary>
    public string RawPath { get; }

    /// <summary>
    /// 本行是否可交给系统打开（右键菜单门禁）：键必须是当前平台的绝对路径。
    /// 编码目录名 / 占位串为 false；目录是否存在在点击时再确认（加载期不做 I/O）。
    /// </summary>
    public bool CanOpen { get; }

    public string SessionCountText { get; }

    public string TotalTokensText { get; }

    /// <summary>估算成本四态文案（未知成本不折 0：未知 / $x.xx+ / $x.xx / —）。</summary>
    public string CostText { get; }

    /// <summary>占比文案（0..1 → "36.8%"；无基数 → "—"）。</summary>
    public string ShareText { get; }

    /// <summary>平均每会话 token（= TotalTokens / SessionCount 取整；0 会话（如纯 CLI 源项目）→ "—"）。</summary>
    public string AverageText { get; }
}

/// <summary>
/// D-011 项目统计页 VM（原型 #page-projects）：项目六列表（I-029 项目维度聚合，Key = 原始路径、
/// DisplayName = 归一化名，歧义守卫见 <see cref="DimensionUsageRow.DisplayName"/>）。
/// 数据随全局筛选联动重查（GlobalFilterMapper 同 Overview/Agents 纪律）；展示维度全部行
/// （服务已按 token 降序，此处防御性重排，不依赖服务排序契约）。
/// <b>行跳转（右栏撤除定案：全局 Project 筛选途径）</b>：由视图的左键点击 / Enter 调用
/// <see cref="OpenSessionsFor"/>——不再用 SelectedItem 变化触发（右键菜单会顺带改选中项，
/// 选择即跳会把「想看菜单」变成「已被带去会话页」）；点击经构造注入回调把 (原始路径, 展示名)
/// 写入全局 Project 筛选后导航会话页（顶栏可见、「全部项目」可复位）。
/// <b>行右键</b>：菜单恒有反馈——<see cref="ProjectRankRowViewModel.CanOpen"/> 的行给
/// 「文件管理器 / 终端 / 本机探测到的编辑器与 Git 客户端」（<see cref="ExternalApps"/>，
/// 只列真的装了的应用），否则给一条置灰说明（编码目录名 / 占位串不猜路径）；
/// 点击时再确认目录存在并尽力启动，失败经 toast 如实反馈。生命周期 / 线程 / 序号防护纪律同
/// <see cref="OverviewViewModel"/>。
/// </summary>
public partial class ProjectsViewModel : ViewModelBase, IDisposable, IDataRefreshAware
{
    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _query;

    /// <summary>行点击跳转会话页的外壳回调（原始路径 + 展示名）；null（测试/预览）时 no-op。</summary>
    private readonly Action<string, string?>? _openSessionsForProject;

    private readonly IToastService? _toastService;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _openFolder;
    private readonly Func<string, bool> _openTerminal;
    private readonly Func<string, bool> _isOpenablePath;

    /// <summary>本机探测到的编辑器 / Git 客户端（菜单按 Editor → GitClient 分组；测试注入固定列表）。</summary>
    public IReadOnlyList<ExternalApp> ExternalApps { get; }

    /// <summary>外部应用图标（macOS 从已安装 .app 提取；测试注入假提供者）。</summary>
    public IAppIconProvider IconProvider { get; }

    /// <summary>加载序号：重置型加载自增；跨 await 序号不一致的响应/异常直接丢弃（同 OverviewViewModel）。</summary>
    private int _loadSequence;

    public PageDescriptor Page { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows), nameof(ShowEmptyState))]
    private IReadOnlyList<ProjectRankRowViewModel> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError), nameof(ShowEmptyState))]
    private string? _loadError;

    public bool HasRows => Rows.Count > 0;

    public bool HasLoadError => LoadError is not null;

    /// <summary>空态可见性（失败时让位错误面板，同会话页 P2-2 纪律）。</summary>
    public bool ShowEmptyState => !HasRows && !HasLoadError;

    public ProjectsViewModel(
        PageDescriptor page,
        FilterStateService filters,
        IUsageQueryService query,
        Action<string, string?>? openSessionsForProject = null,
        IToastService? toastService = null,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? openFolder = null,
        Func<string, bool>? openTerminal = null,
        Func<string, bool>? isOpenablePath = null,
        IReadOnlyList<ExternalApp>? externalApps = null,
        IAppIconProvider? iconProvider = null)
    {
        Page = page;
        _filters = filters;
        _query = query;
        _openSessionsForProject = openSessionsForProject;
        _toastService = toastService;
        _directoryExists = directoryExists ?? (path => Directory.Exists(path));
        _openFolder = openFolder ?? ShellLauncher.TryOpenFolder;
        _openTerminal = openTerminal ?? TerminalLauncher.TryOpen;
        _isOpenablePath = isOpenablePath ?? ProjectPathRules.IsLocalAbsolutePath;
        ExternalApps = externalApps ?? ExternalAppCatalog.Detect();
        IconProvider = iconProvider ?? AppIconProvider.Shared;
        filters.PropertyChanged += OnFilterPropertyChanged;
        _ = LoadAsync();
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (FilterStateService.AffectsQueries(e.PropertyName))
        {
            _ = LoadAsync();
        }
    }

    /// <summary>后台采集刷新轮次完成后重查本页（IDataRefreshAware）：复用 LoadAsync，序号防护保证与筛选重查并发时旧响应丢弃。</summary>
    public Task OnDataRefreshedAsync() => LoadAsync();

    /// <summary>按当前全局筛选重查项目维度（序号防护见类型说明）。</summary>
    public async Task LoadAsync()
    {
        var sequence = ++_loadSequence;
        var filter = GlobalFilterMapper.Build(_filters);

        try
        {
            var projects = await _query.GetProjectBreakdownAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            Rows = projects
                .OrderByDescending(row => row.TotalTokens)
                .ThenBy(row => row.Key, StringComparer.Ordinal)
                .Select(row => new ProjectRankRowViewModel(row, _isOpenablePath(row.Key)))
                .ToList();
            LoadError = null;
        }
        catch (Exception ex)
        {
            if (sequence != _loadSequence)
            {
                return; // 旧请求的失败不覆盖新请求的状态
            }

            LoadError = string.Format(Translations.Instance.LoadErrorFormat, ex.Message);
        }
    }

    /// <summary>
    /// 行跳转（视图左键 Tapped / Enter 调用）：写入全局 Project 筛选并导航会话页。
    /// 不再经 SelectedItem 变化触发——右键菜单会改选中项，选择即跳会误触发导航。
    /// </summary>
    public void OpenSessionsFor(ProjectRankRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _openSessionsForProject?.Invoke(row.RawPath, row.Name);
    }

    /// <summary>右键菜单「在文件管理器中打开」：绝对路径行才执行，点击时确认目录存在。</summary>
    [RelayCommand]
    private Task OpenProjectFolderAsync(ProjectRankRowViewModel? row) =>
        OpenProjectPathAsync(row, _openFolder, Translations.Instance.ToastProjectOpenFailed);

    /// <summary>右键菜单「在终端中打开」：同上，终端启动失败单独提示。</summary>
    [RelayCommand]
    private Task OpenProjectTerminalAsync(ProjectRankRowViewModel? row) =>
        OpenProjectPathAsync(row, _openTerminal, Translations.Instance.ToastTerminalUnavailable);

    /// <summary>右键菜单「在 {app} 中打开」：目录存在才启动，失败 toast（带应用名）。</summary>
    public async Task OpenInAppAsync(ExternalApp app, ProjectRankRowViewModel? row)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (row is not { CanOpen: true })
        {
            return;
        }

        if (!await DirectoryExistsAsync(row.RawPath))
        {
            _toastService?.Show(Translations.Instance.ToastProjectPathMissing);
            return;
        }

        if (!app.TryOpen(row.RawPath))
        {
            _toastService?.Show(string.Format(
                Translations.Instance.ToastExternalAppOpenFailedFormat, app.DisplayName));
        }
    }

    private async Task OpenProjectPathAsync(
        ProjectRankRowViewModel? row,
        Func<string, bool> open,
        string failureMessage)
    {
        if (row is not { CanOpen: true })
        {
            return;
        }

        if (!await DirectoryExistsAsync(row.RawPath))
        {
            _toastService?.Show(Translations.Instance.ToastProjectPathMissing);
            return;
        }

        if (!open(row.RawPath))
        {
            _toastService?.Show(failureMessage);
        }
    }

    // 存在性检查放线程池：网络盘/失效挂载的 stat 可能阻塞，不能卡 UI 线程。
    private Task<bool> DirectoryExistsAsync(string path) => Task.Run(() => _directoryExists(path));

    public void Dispose() => _filters.PropertyChanged -= OnFilterPropertyChanged;
}
