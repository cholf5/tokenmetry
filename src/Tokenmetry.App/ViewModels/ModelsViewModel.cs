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
/// D-010 模型明细表单行（原型 #modelRows 七列：模型 / 关联 Agent / 输入 / 输出 / 缓存读取 / 总 Token / 成本）。
/// 不可变小对象，由 <see cref="ModelsViewModel"/> 整体替换集合触发绑定刷新。
/// <b>未知成本口径（全应用样板页）</b>：成本列四态——(x,false) "$x.xx"；(x,true) "$x.xx+"（已知成本下限）；
/// (null,true) 全未知 → 文案「未知」+ chip 视觉区分（<see cref="IsUnknownCost"/>）；(null,false) 无成本数据 → "—"。
/// 缺价行的 Token 计入合计、成本绝不折 0 混入（TC-001，I-024/I-025 未知成本纪律）。
/// <b>关联 Agent 列定案（D-010 决策点 1）</b>：契约演进给 <see cref="DimensionUsageRow"/> 增补
/// DominantAgent/AgentCount（仅模型维度，同批 session_facts 按 Agent 计数派生）——
/// 唯一 Agent → 显示该 Agent；多 Agent → 「主用 Agent 等 N 个」（主用 + 聚合语义同时可见）；
/// 无会话数据（纯 CLI 源）→ "—"（不猜）。
/// </summary>
public sealed class ModelDetailRowViewModel
{
    public ModelDetailRowViewModel(DimensionUsageRow row)
    {
        Name = row.Key;
        AssociatedAgentText = row.AgentCount switch
        {
            1 => row.DominantAgent ?? "—",
            > 1 => string.Format(
                Translations.Instance.AssociatedAgentsMultiFormat,
                row.DominantAgent,
                row.AgentCount.Value.ToString("N0", CultureInfo.InvariantCulture)),
            _ => "—",
        };
        IsMultiAgent = row.AgentCount is > 1;
        InputTokensText = TrendChart.FormatCompactTokens(row.InputTokens);
        OutputTokensText = TrendChart.FormatCompactTokens(row.OutputTokens);
        CacheReadTokensText = TrendChart.FormatCompactTokens(row.CacheReadTokens);
        TotalTokensText = TrendChart.FormatCompactTokens(row.TotalTokens);
        CostText = OverviewViewModel.FormatCost(row.Cost);
        IsUnknownCost = row.Cost.KnownCostUsd is null && row.Cost.HasUnknownCost;
    }

    public string Name { get; }

    /// <summary>关联 Agent 文案（唯一 → Agent 名；多 Agent → "主用 等 N 个"；无会话数据 → "—"）。</summary>
    public string AssociatedAgentText { get; }

    /// <summary>多 Agent 共用（视图可加聚合提示；单 Agent / 无数据为 false）。</summary>
    public bool IsMultiAgent { get; }

    public string InputTokensText { get; }

    public string OutputTokensText { get; }

    /// <summary>缓存读取（CacheReadTokens；缓存写入不计入本列，口径同原型「缓存读取」列）。</summary>
    public string CacheReadTokensText { get; }

    public string TotalTokensText { get; }

    /// <summary>成本四态文案（未知绝不折 0）。</summary>
    public string CostText { get; }

    /// <summary>全未知成本（缺价模型）→ 成本列以 chip 呈现「未知」；已知部分（含 $x.xx+ 混合态）走普通文本。</summary>
    public bool IsUnknownCost { get; }
}

/// <summary>
/// D-010 模型明细合计行（口径在页面可见，TC-001）：
/// <list type="bullet">
/// <item>各 Token 列合计 = 全部模型行之和（<b>含缺价模型</b>——缺价只影响成本，不影响用量）；</item>
/// <item>成本合计 = 已知成本之和（未知部分不计入）：全部已知 → "$Y"；存在未知 → "$Y+"（已知成本下限）；</item>
/// <item>未知模型计数与口径脚注（"N 个模型成本未知：其成本未计入合计，Token 已计入总量"），
/// 对齐原型 #page-models meta「价格缺失时成本应标记为未知，而非 0」的产品立场。</item>
/// </list>
/// </summary>
public sealed class ModelsTotalViewModel
{
    public ModelsTotalViewModel(IReadOnlyList<DimensionUsageRow> rows)
    {
        ModelCountText = string.Format(
            Translations.Instance.TotalsModelCountFormat,
            rows.Count.ToString("N0", CultureInfo.InvariantCulture));
        // Token 合计用紧凑格式（行同口径，见 ModelDetailRowViewModel 的列宽裁定）
        InputTokensText = TrendChart.FormatCompactTokens(rows.Sum(row => row.InputTokens));
        OutputTokensText = TrendChart.FormatCompactTokens(rows.Sum(row => row.OutputTokens));
        CacheReadTokensText = TrendChart.FormatCompactTokens(rows.Sum(row => row.CacheReadTokens));
        TotalTokensText = TrendChart.FormatCompactTokens(rows.Sum(row => row.TotalTokens));
        Cost = rows.Aggregate(AggregatedCost.Empty, (acc, row) => acc.Add(row.Cost));
        CostText = OverviewViewModel.FormatCost(Cost);
        UnknownCount = rows.Count(row => row.Cost.HasUnknownCost);
        UnknownNoteText = string.Format(
            Translations.Instance.TotalsUnknownNoteFormat,
            UnknownCount.ToString("N0", CultureInfo.InvariantCulture));
    }

    /// <summary>合计行的成本聚合（已知部分之和 + 未知标记；测试对账用）。</summary>
    public AggregatedCost Cost { get; }

    public string ModelCountText { get; }

    public string InputTokensText { get; }

    public string OutputTokensText { get; }

    public string CacheReadTokensText { get; }

    public string TotalTokensText { get; }

    public string CostText { get; }

    /// <summary>成本未知的模型数（含部分未知的混合行——其未知部分同样未计入合计）。</summary>
    public int UnknownCount { get; }

    public bool HasUnknownNote => UnknownCount > 0;

    public string UnknownNoteText { get; }
}

/// <summary>
/// D-010 模型统计页 VM（原型 #page-models）：全模型明细表（I-029 模型维度聚合，含缺价行）+
/// 合计行（已估/未估口径可见）。本页是全应用「未知成本口径」的样板页——缺价模型成本呈「未知」
/// 而非 0，token 计入、成本不计入（TC-001）。数据随全局筛选联动重查（GlobalFilterMapper 同
/// Overview/Agents 纪律）；明细表展示维度全部行（服务已按 token 降序，此处防御性重排）。
/// 生命周期 / 线程 / 序号防护纪律同 <see cref="OverviewViewModel"/>：每次导航新建实例、
/// 外壳 Dispose 退订、FilterStateService 通知已在 UI 线程。加载失败保留上一次成功数据 +
/// 错误面板（同会话页 P2-2 纪律）；本页无行点击下钻（卡内未要求，明细下钻经会话页搜索/筛选完成）。
/// </summary>
public partial class ModelsViewModel : ViewModelBase, IDisposable, IDataRefreshAware
{
    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _query;

    /// <summary>加载序号：重置型加载自增；跨 await 序号不一致的响应/异常直接丢弃（同 OverviewViewModel）。</summary>
    private int _loadSequence;

    public PageDescriptor Page { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows), nameof(ShowEmptyState))]
    private IReadOnlyList<ModelDetailRowViewModel> _rows = [];

    /// <summary>合计行（无行数据 / 首查未完成时为 null → 视图隐藏；失败保留上一次成功合计）。</summary>
    [ObservableProperty]
    private ModelsTotalViewModel? _totals;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError), nameof(ShowEmptyState))]
    private string? _loadError;

    public bool HasRows => Rows.Count > 0;

    public bool HasLoadError => LoadError is not null;

    /// <summary>空态可见性（失败时让位错误面板，同会话页 P2-2 纪律）。</summary>
    public bool ShowEmptyState => !HasRows && !HasLoadError;

    public bool ShowTotals => Totals is not null;

    public ModelsViewModel(PageDescriptor page, FilterStateService filters, IUsageQueryService query)
    {
        Page = page;
        _filters = filters;
        _query = query;
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

    /// <summary>按当前全局筛选重查模型维度（序号防护见类型说明）。</summary>
    public async Task LoadAsync()
    {
        var sequence = ++_loadSequence;
        var filter = GlobalFilterMapper.Build(_filters);

        try
        {
            var models = await _query.GetModelBreakdownAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            var ordered = models
                .OrderByDescending(row => row.TotalTokens)
                .ThenBy(row => row.Key, StringComparer.Ordinal)
                .ToList();
            Rows = ordered.Select(row => new ModelDetailRowViewModel(row)).ToList();
            Totals = ordered.Count > 0 ? new ModelsTotalViewModel(ordered) : null;
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

    public void Dispose() => _filters.PropertyChanged -= OnFilterPropertyChanged;
}
