using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CCUsage.App.Services;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Core.Services;

namespace CCUsage.App.ViewModels;

/// <summary>
/// D-013 成本计算口径说明表行（原型「成本计算口径」三行：项目 / 建议展示 chip / 注意事项）。
/// 静态文案块（无假数据），内容与 docs/reconciliation/2026-10-11-reconciliation.md §1.2 的
/// 计价优先级口径及 <see cref="PricingEngine"/> 实际行为对齐（见 <see cref="CostViewModel.CreateCostBasisRows"/>）。
/// </summary>
public sealed class CostBasisRowViewModel
{
    public CostBasisRowViewModel(string item, string stance, string note)
    {
        Item = item;
        Stance = stance;
        Note = note;
    }

    /// <summary>口径项（原型第一列：实际计费 / 估算成本 / 未知成本）。</summary>
    public string Item { get; }

    /// <summary>建议展示（原型第二列 chip 文案：优先 / 模型价格表 / 不计入总额或单独标注）。</summary>
    public string Stance { get; }

    /// <summary>注意事项（原型第三列；与仓库对账口径文档对齐的表述）。</summary>
    public string Note { get; }
}

/// <summary>
/// D-013 成本分析页 VM（原型 #page-cost）：KPI 四卡（估算总成本 / 输入成本占比 / 输出成本占比 /
/// 待确认价格模型数）+ 成本计算口径说明表（静态三行）。
/// <list type="bullet">
/// <item>估算总成本——<see cref="UsageKpiResult.Cost"/>（<see cref="AggregatedCost"/> 四态，
/// 复用 <see cref="OverviewViewModel.FormatCost"/>：未知绝不折 0，D-010 同一红线）；存在未知贡献时
/// 页面追加「已知成本下限」说明行（<see cref="UnknownCostNoteText"/>）。</item>
/// <item>输入 / 输出占比——<b>决策点定案（方案 b，Dev Log 已记录）</b>：I-028/I-029 契约与
/// <see cref="UsageRecord.CostUsd"/> 均只有混合成本，无按方向（输入/输出）的计价拆分；按方向拆价需
/// 重跑定价口径（波及与 ccusage 的对账）。本页以 <b>Token 构成占比</b>代替并在 KPI 脚注<b>显式标注</b>
/// 「按 Token 构成展示 · 成本按方向拆价口径待扩展」——分母为 Σ TotalTokens（含缓存相关，缓存单列说明），
/// 与原型 input% + output% ≈ 86%、缓存占余量的构成一致；禁止无标签的隐式近似。</item>
/// <item>待确认价格——<see cref="PricingEngine.GetUnpricedModels()"/>（I-003/I-024 缺价权威清单）。
/// <b>全局口径</b>：清单按引擎计价过程累计、不随时间范围 / Agent 筛选变化（KPI 脚注说明，防误读）；
/// 每次加载重读引擎快照。</item>
/// </list>
/// 生命周期 / 线程 / 序号防护纪律同 <see cref="OverviewViewModel"/>；失败保留上一次成功数据 + 错误面板。
/// </summary>
public partial class CostViewModel : ViewModelBase, IDisposable, IDataRefreshAware
{
    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _query;
    private readonly PricingEngine _pricingEngine;

    /// <summary>加载序号：重置型加载自增；跨 await 序号不一致的响应/异常直接丢弃（同 OverviewViewModel）。</summary>
    private int _loadSequence;

    public PageDescriptor Page { get; }

    // ── KPI 四卡（原型 .kpis 顺序：估算总成本 / 输入成本占比 / 输出成本占比 / 待确认价格）──
    // 占比/计数类无环比（卡内未要求），只承载主值；加载完成前 "—"（未加载不冒充 0）。

    /// <summary>估算总成本（<see cref="AggregatedCost"/> 四态文案）。</summary>
    [ObservableProperty]
    private string _totalCostText = "—";

    /// <summary>输入占比（Token 构成口径，显式标注见视图脚注）。</summary>
    [ObservableProperty]
    private string _inputShareText = "—";

    /// <summary>输出占比（Token 构成口径，显式标注见视图脚注）。</summary>
    [ObservableProperty]
    private string _outputShareText = "—";

    /// <summary>待确认价格模型数（如 "3 个"；引擎清单为空为 "0 个"）。</summary>
    [ObservableProperty]
    private string _unpricedCountText = "—";

    /// <summary>缺价模型名清单（tooltip 用；空清单为「无缺价模型」）。</summary>
    [ObservableProperty]
    private string _unpricedModelsTooltip = "无缺价模型";

    /// <summary>未知成本说明行（估算总成本存在未知贡献时可见；D-010 口径的页面级说明）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnknownCostNote))]
    private string? _unknownCostNoteText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? _loadError;

    public bool HasUnknownCostNote => UnknownCostNoteText is not null;

    public bool HasLoadError => LoadError is not null;

    /// <summary>成本计算口径三行（静态文案，页面固定展示；内容见 <see cref="CreateCostBasisRows"/>）。</summary>
    public IReadOnlyList<CostBasisRowViewModel> CostBasisRows { get; } = CreateCostBasisRows();

    public CostViewModel(
        PageDescriptor page,
        FilterStateService filters,
        IUsageQueryService query,
        PricingEngine pricingEngine)
    {
        Page = page;
        _filters = filters;
        _query = query;
        _pricingEngine = pricingEngine ?? throw new ArgumentNullException(nameof(pricingEngine));
        filters.PropertyChanged += OnFilterPropertyChanged;
        _ = LoadAsync();
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(FilterStateService.TimeRange)
            || e.PropertyName == nameof(FilterStateService.Agent))
        {
            _ = LoadAsync();
        }
    }

    /// <summary>后台采集刷新轮次完成后重查本页（IDataRefreshAware）：复用 LoadAsync，序号防护保证与筛选重查并发时旧响应丢弃。</summary>
    public Task OnDataRefreshedAsync() => LoadAsync();

    /// <summary>按当前全局筛选重查 KPI + 日序列，并重读定价引擎缺价清单（序号防护见类型说明）。</summary>
    public async Task LoadAsync()
    {
        var sequence = ++_loadSequence;
        var filter = GlobalFilterMapper.Build(_filters.TimeRange, _filters.Agent);

        try
        {
            var kpi = await _query.GetKpiAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            // 日序列仅取 Token 构成算输入/输出占比（方案 b 显式标注，见类型说明）；成本一律以 KPI 为准。
            var daily = await _query.GetDailySeriesAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            // 引擎缺价清单是全局口径（不随筛选变化）；每次加载重读快照，与页面其它 KPI 同帧可见。
            var unpriced = _pricingEngine.GetUnpricedModels();

            TotalCostText = OverviewViewModel.FormatCost(kpi.Cost);
            UnknownCostNoteText = BuildUnknownCostNote(kpi.Cost);
            var (inputShare, outputShare) = CreateDirectionShares(daily);
            InputShareText = OverviewViewModel.FormatShare(inputShare);
            OutputShareText = OverviewViewModel.FormatShare(outputShare);
            UnpricedCountText = $"{unpriced.Count.ToString("N0", CultureInfo.InvariantCulture)} 个";
            UnpricedModelsTooltip = BuildUnpricedTooltip(unpriced);
            LoadError = null;
        }
        catch (Exception ex)
        {
            if (sequence != _loadSequence)
            {
                return; // 旧请求的失败不覆盖新请求的状态
            }

            LoadError = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 输入 / 输出占比（Token 构成口径，internal 供测试对账）：分子 ΣInputTokens / ΣOutputTokens，
    /// 分母 ΣTotalTokens（含缓存相关——缓存单列说明，与原型 input% + output% ≈ 86% 的构成一致）。
    /// 分母为 0 时双返回 null（无基数，"—"，不伪造 0%）。
    /// </summary>
    internal static (decimal? InputShare, decimal? OutputShare) CreateDirectionShares(
        IReadOnlyList<DailyUsagePoint> daily)
    {
        long input = 0, output = 0, total = 0;
        foreach (var point in daily)
        {
            input += point.InputTokens;
            output += point.OutputTokens;
            total += point.TotalTokens;
        }

        if (total == 0)
        {
            return (null, null);
        }

        return ((decimal)input / total, (decimal)output / total);
    }

    /// <summary>
    /// 未知成本说明行（internal 供测试对账）：(x,true) 部分未知 → 已知成本下限说明；
    /// (null,true) 全部未知 → 全未知说明；(null,false) / (x,false) → null（隐藏）。
    /// </summary>
    internal static string? BuildUnknownCostNote(AggregatedCost cost) => cost.KnownCostUsd is { } known
        ? cost.HasUnknownCost
            ? $"估算总成本为已知成本下限（${known.ToString("F2", CultureInfo.InvariantCulture)}+）："
              + "存在未计价模型的成本未知，未计入总额（缺价模型见「待确认价格」）。"
            : null
        : cost.HasUnknownCost
            ? "当前范围成本全部未知：范围内用量的模型均缺价，成本未计入任何合计（绝不折 0）。"
            : null;

    /// <summary>待确认价格 tooltip：缺价模型名（引擎序）分号连接；空清单「无缺价模型」。</summary>
    internal static string BuildUnpricedTooltip(IReadOnlyList<UnpricedModelInfo> unpriced) =>
        unpriced.Count == 0
            ? "无缺价模型"
            : string.Join("; ", unpriced.Select(m => m.Model));

    /// <summary>
    /// 成本计算口径三行定稿（internal 供测试断言；依据 docs/reconciliation/2026-10-11-reconciliation.md
    /// §1.2 计价优先级探针结论 + <see cref="PricingEngine"/> 三级回退实现，非凭记忆转述）：
    /// <list type="number">
    /// <item>实际计费——日志自带 costUSD 直接采用（引擎 LogCost 路径；与 ccusage 20.0.29 实测口径一致，
    /// 对账证据见对账文档 §1.2/§2）。</item>
    /// <item>估算成本——用户覆盖 → models.dev 远程数据 → 内置快照三级回退；缓存输入 5m/1h 单独计价
    /// （蓝本 aggregation.rs 规则移植，fixture 对账逐分钱一致）。价格版本与逐笔计算时间当前未在 facts 层
    /// 记录，页面如实不展示该二项（原型「显示价格版本与计算时间」为建议展示，落地差距登记 Dev Log 遗留）。</item>
    /// <item>未知成本——缺价模型保留「未知」，绝不折 0 混入合计（I-024/I-025 纪律 + AggregatedCost 契约）。</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<CostBasisRowViewModel> CreateCostBasisRows() =>
    [
        new("实际计费", "优先",
            "日志自带 costUSD 时直接采用（与 ccusage 计价优先级一致，对账证据见 docs/reconciliation）；"
            + "仅在来源提供可靠计费数据时使用。"),
        new("估算成本", "模型价格表",
            "按价格快照估算：用户覆盖 → models.dev 远程数据 → 内置快照三级回退；缓存输入（5m/1h）单独计价。"
            + "金额为采集时按当次命中的价格来源计算，页面不另行折算。"),
        new("未知成本", "不计入总额或单独标注",
            "价格缺失的模型保留「未知」，绝不折 0 混入合计；缺价清单见「待确认价格」，逐模型明细见模型统计页。"),
    ];

    public void Dispose() => _filters.PropertyChanged -= OnFilterPropertyChanged;
}
