using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TokenPulse.App.Controls;
using TokenPulse.App.Localization;
using TokenPulse.App.Services;
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;

namespace TokenPulse.App.ViewModels;

/// <summary>右栏快速筛选的单个 Agent 勾选项（D-008：选项来自 AgentCatalogService 的真实 Agent 清单，
/// 查询值 = Agent 键，驱动会话明细页的页级 Agent 过滤——关系定案见 <see cref="SessionsPanelViewModel"/>）。</summary>
public partial class AgentQuickFilterViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isChecked = true;

    /// <summary>Agent 键（查询值，与 I-029 过滤语义一致）。</summary>
    public string AgentId { get; }

    public string Label { get; }

    public AgentQuickFilterViewModel(string agentId, string? label = null, bool isChecked = true)
    {
        AgentId = agentId;
        Label = label ?? agentId;
        IsChecked = isChecked;
    }
}

/// <summary>右栏「最近会话」列表的单行展示模型（原型 .session 行：icon-chip + Agent + 标题 + token/时间脚注）。</summary>
public sealed class RecentSessionRowViewModel
{
    public RecentSessionRowViewModel(SessionListRow row)
    {
        SessionId = row.SessionId;
        Agent = row.Agent;
        // facts 无标题字段（I-002/I-029）：标题位诚实用会话标识 SessionId，不伪造任务标题
        TitleText = row.SessionId;
        IconGlyph = AgentVisualMap.Glyph(row.Agent).ToString();
        ChipVariant = AgentVisualMap.ChipVariant(row.Agent);
        TokensText = $"{TrendChart.FormatCompactTokens(row.TotalTokens)} tokens";
        TimeText = SessionsViewModel.FormatRelativeTime(row.LastSeenAt);
    }

    public string SessionId { get; }

    public string Agent { get; }

    public string TitleText { get; }

    public string IconGlyph { get; }

    /// <summary>软色底变体名（颜色经 SharedStyles chip-* 伪类 DynamicResource 呈现，VM 不持画刷）。</summary>
    public string ChipVariant { get; }

    public string TokensText { get; }

    public string TimeText { get; }
}

/// <summary>
/// 右侧最近会话栏 VM（D-008 接真实数据）。
/// <b>与全局筛选 / 会话页的关系定案（任务卡要求记录）</b>：采用"页级快捷筛选"方案——
/// <list type="bullet">
/// <item>全局筛选（顶部栏时间范围 + 单选 Agent）= 跨页上下文：右栏「最近会话」列表<b>只</b>随全局
///       筛选重查（<see cref="FilterStateService"/> 订阅，最近活动排序 Top6，对齐原型 slice(0,6)）；</item>
/// <item>Agent 复选组 / 模型 / 项目下拉 = <b>会话页级</b>快捷筛选：只驱动会话明细页查询
///       （与全局筛选取交集，映射规则见 <see cref="SessionsViewModel"/>），不影响最近会话列表
///       （对齐原型：.filterbox 不改变 #recentSessions 的渲染）；</item>
/// <item>「应用筛选」「清除」是复选组/下拉的提交点（提交前改动暂存、不触发重查），两者都会
///       触发会话页重查（经 <see cref="QuickFiltersCommitted"/> 事件；页面未打开时仅 toast 反馈）；</item>
/// <item>不扩展 FilterStateService 支持多 Agent 集（I-029 UsageQueryFilter.Agent 是单值，
///       不在本卡变更已审定的查询契约，见 D-008 Dev Log 权衡）。</item>
/// </list>
/// Agent 复选组选项来自 <see cref="AgentCatalogService"/>（与顶部栏全局下拉同一份缓存 = 单一真相源），
/// 模型 / 项目选项同源（无界维度查询 + 服务层缓存）。最近会话用序号防护（全局筛选快速切换时
/// 旧响应不覆盖新选择）；实例与外壳同生命周期，常驻订阅 FilterStateService 无需退订。
/// </summary>
public partial class SessionsPanelViewModel : ViewModelBase
{
    /// <summary>最近会话 TopN（对齐原型 renderSessions 的 slice(0,6)）。</summary>
    public const int RecentTopN = 6;

    private readonly IToastService _toastService;
    private readonly IUsageQueryService _query;
    private readonly AgentCatalogService _catalog;
    private readonly FilterStateService _filters;

    /// <summary>最近会话加载序号（全局筛选快速切换防护）。</summary>
    private int _recentSequence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentSessions))]
    private ObservableCollection<RecentSessionRowViewModel> _recentSessions = new();

    [ObservableProperty]
    private string? _recentError;

    /// <summary>Agent 复选组（真实 Agent 清单；未加载完成前为空——诚实不冒充占位清单）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentFilterCount))]
    private ObservableCollection<AgentQuickFilterViewModel> _agentFilters = new();

    /// <summary>模型筛选选项（首个为"全部模型"哨兵；加载前仅哨兵——诚实）。</summary>
    [ObservableProperty]
    private IReadOnlyList<ModelFilterOption> _modelFilterOptions = [ModelFilterOption.All];

    /// <summary>项目筛选选项（首个为"全部项目"哨兵；查询值 = 原始路径）。</summary>
    [ObservableProperty]
    private IReadOnlyList<ProjectFilterOption> _projectFilterOptions = [ProjectFilterOption.All];

    [ObservableProperty]
    private ModelFilterOption _modelFilter = ModelFilterOption.All;

    [ObservableProperty]
    private ProjectFilterOption _projectFilter = ProjectFilterOption.All;

    /// <summary>最近会话行点击（toast 占位下钻，同会话页定案）；选中态立即复位以便再次点击。</summary>
    [ObservableProperty]
    private RecentSessionRowViewModel? _selectedRecentSession;

    /// <summary>快速筛选提交（应用/清除）事件；会话明细页 VM 订阅以触发重查。</summary>
    public event EventHandler? QuickFiltersCommitted;

    public bool HasRecentSessions => RecentSessions.Count > 0;

    /// <summary>复选组当前项数（0 = 选项清单未加载；会话页据此走"不过滤"兜底）。</summary>
    public int AgentFilterCount => AgentFilters.Count;

    /// <summary>当前勾选的 Agent 键（选项序；会话页在提交点读取 = 提交值）。</summary>
    public IReadOnlyList<string> CheckedAgentIds =>
        AgentFilters.Where(a => a.IsChecked).Select(a => a.AgentId).ToList();

    /// <summary>选中的模型筛选（null = 全部模型）。</summary>
    public string? SelectedModelName => ModelFilter.Model;

    /// <summary>选中的项目筛选（null = 全部项目）。</summary>
    public string? SelectedProjectPath => ProjectFilter.Path;

    public SessionsPanelViewModel(
        IToastService toastService,
        IUsageQueryService query,
        AgentCatalogService catalog,
        FilterStateService filters)
    {
        _toastService = toastService;
        _query = query;
        _catalog = catalog;
        _filters = filters;

        // 最近会话随全局筛选重查（页级快速筛选不影响本列表，见类型说明定案）
        filters.PropertyChanged += OnFilterPropertyChanged;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // 选项清单（AgentCatalogService 缓存，页面/右栏共用，不随导航重拉）
        try
        {
            var agents = await _catalog.GetAgentOptionsAsync();
            AgentFilters = new ObservableCollection<AgentQuickFilterViewModel>(
                agents.Where(a => a.Id != AgentOption.AllId)
                    .Select(a => new AgentQuickFilterViewModel(a.Id, a.Label)));

            var models = await _catalog.GetModelOptionsAsync();
            ModelFilterOptions = models;

            var projects = await _catalog.GetProjectOptionsAsync();
            ProjectFilterOptions = projects;
        }
        catch
        {
            // 选项加载失败：诚实保持"仅哨兵/空"，不伪造占位清单；下次启动重试（服务层缓存未写入）
        }

        await ReloadRecentSessionsAsync();
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (FilterStateService.AffectsQueries(e.PropertyName))
        {
            _ = ReloadRecentSessionsAsync();
        }
    }

    /// <summary>右栏最近会话：全局筛选映射 + 最近活动降序 Top6（序号防护，见类型说明）。</summary>
    public async Task ReloadRecentSessionsAsync()
    {
        var sequence = ++_recentSequence;
        try
        {
            var result = await _query.GetSessionsAsync(new SessionListQuery
            {
                Filter = GlobalFilterMapper.Build(_filters),
                SortBy = SessionSortField.LastSeenAt,
                SortDescending = true,
                Page = 1,
                PageSize = RecentTopN,
            });
            if (sequence != _recentSequence)
            {
                return;
            }

            RecentSessions = new ObservableCollection<RecentSessionRowViewModel>(
                result.Rows.Select(r => new RecentSessionRowViewModel(r)));
            RecentError = null;
        }
        catch (Exception ex)
        {
            if (sequence != _recentSequence)
            {
                return;
            }

            RecentSessions = new ObservableCollection<RecentSessionRowViewModel>();
            RecentError = string.Format(Translations.Instance.RecentErrorFormat, ex.Message);
        }
    }

    [RelayCommand]
    private void ApplyQuickFilters()
    {
        // 原型 applyFilters 同款反馈（Agent 数 · 模型 · 项目）；重查由订阅本事件的会话页执行
        _toastService.Show(string.Format(
            Translations.Instance.ToastFiltersAppliedFormat,
            AgentFilters.Count(a => a.IsChecked),
            ModelFilter.Label,
            ProjectFilter.Label));
        QuickFiltersCommitted?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearQuickFilters()
    {
        foreach (var agent in AgentFilters)
        {
            agent.IsChecked = true;
        }

        ModelFilter = ModelFilterOptions[0];
        ProjectFilter = ProjectFilterOptions[0];
        _toastService.Show(Translations.Instance.ToastFiltersCleared);
        QuickFiltersCommitted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// D-011 项目统计页行点击的外部落点（D-009/D-011 决策点 2 定案）：以「程序化立即应用」语义
    /// 把目标项目写入会话页级项目筛选——与手动改下拉不同，不走暂存（跳转即生效），但同样经
    /// <see cref="QuickFiltersCommitted"/> 提交、同样在右栏下拉可见、「清除」可复位（无隐藏过滤态）。
    /// 选项清单缺该路径时如实追加（选项是启动期全集缓存，后台刷新后的新项目可能不在其中；
    /// 静默丢弃会让跳转筛选失效）；不触碰 Agent 复选组与模型下拉（跳转只表达项目维度意图，
    /// 其余维度维持用户既有筛选，与全局筛选取交集的语义不变）。
    /// </summary>
    public void ApplyExternalProjectFilter(string projectPath, string? label = null)
    {
        var option = ProjectFilterOptions.FirstOrDefault(o =>
            string.Equals(o.Path, projectPath, StringComparison.Ordinal));
        if (option is null)
        {
            option = new ProjectFilterOption(projectPath, label ?? projectPath);
            ProjectFilterOptions = [.. ProjectFilterOptions, option];
        }

        ProjectFilter = option;
        QuickFiltersCommitted?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedRecentSessionChanged(RecentSessionRowViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        // 下钻定案（D-008）：与明细表行点击一致——toast 显示会话标识 + 规划中提示
        _toastService.Show(string.Format(Translations.Instance.ToastSessionPlannedFormat, value.SessionId));
        _selectedRecentSession = null;
        OnPropertyChanged(nameof(SelectedRecentSession));
    }
}
