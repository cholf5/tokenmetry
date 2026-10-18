using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
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
    public ProjectRankRowViewModel(DimensionUsageRow row)
    {
        Name = row.DisplayName ?? row.Key;
        RawPath = row.Key;
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
/// <b>行点击跳转（右栏撤除定案：全局 Project 筛选途径）</b>：项目维度自 2026-10-18 起有全局宿主
/// （FilterStateService.Project，顶栏四维之一）——经构造注入回调由外壳把 (原始路径, 展示名)
/// 写入全局 Project 筛选（OpenSessionsForProject，落地后顶栏可见、「全部项目」可复位）后导航会话页。
/// 生命周期 / 线程 / 序号防护纪律同 <see cref="OverviewViewModel"/>。
/// </summary>
public partial class ProjectsViewModel : ViewModelBase, IDisposable, IDataRefreshAware
{
    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _query;

    /// <summary>行点击跳转会话页的外壳回调（原始路径 + 展示名）；null（测试/预览）时 no-op。</summary>
    private readonly Action<string, string?>? _openSessionsForProject;

    /// <summary>加载序号：重置型加载自增；跨 await 序号不一致的响应/异常直接丢弃（同 OverviewViewModel）。</summary>
    private int _loadSequence;

    public PageDescriptor Page { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows), nameof(ShowEmptyState))]
    private IReadOnlyList<ProjectRankRowViewModel> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError), nameof(ShowEmptyState))]
    private string? _loadError;

    /// <summary>排行表行点击（ListBox SelectedItem）：跳会话页带项目筛选，选中态立即复位。</summary>
    [ObservableProperty]
    private ProjectRankRowViewModel? _selectedProject;

    public bool HasRows => Rows.Count > 0;

    public bool HasLoadError => LoadError is not null;

    /// <summary>空态可见性（失败时让位错误面板，同会话页 P2-2 纪律）。</summary>
    public bool ShowEmptyState => !HasRows && !HasLoadError;

    public ProjectsViewModel(
        PageDescriptor page,
        FilterStateService filters,
        IUsageQueryService query,
        Action<string, string?>? openSessionsForProject = null)
    {
        Page = page;
        _filters = filters;
        _query = query;
        _openSessionsForProject = openSessionsForProject;
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
                .Select(row => new ProjectRankRowViewModel(row))
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

    partial void OnSelectedProjectChanged(ProjectRankRowViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        _selectedProject = null; // 直接回写字段避免 setter 递归；下次点击同一行仍触发
        OnPropertyChanged(nameof(SelectedProject));
        _openSessionsForProject?.Invoke(value.RawPath, value.Name);
    }

    public void Dispose() => _filters.PropertyChanged -= OnFilterPropertyChanged;
}
