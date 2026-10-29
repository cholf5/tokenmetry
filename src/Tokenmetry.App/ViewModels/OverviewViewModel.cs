using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 环比指标的价值极性（颜色语义）：涨跌方向与「好/坏」分开表达。
/// 成本越低越好、缓存命中占比越高越好；Tokens/会话数只是用量，涨跌本身没有好坏，
/// 用中性色（不给绿/红暗示）。
/// </summary>
public enum KpiChangeValence
{
    /// <summary>中性指标（用量/会话数）：方向由箭头表达，颜色保持 muted。</summary>
    Neutral,

    /// <summary>越高越好（缓存命中占比）：↑ 绿 / ↓ 红。</summary>
    HigherIsBetter,

    /// <summary>越低越好（成本）：↓ 绿 / ↑ 红。</summary>
    LowerIsBetter,
}

/// <summary>
/// 单张 KPI 卡的展示模型（D-006）：主数值 + 环比箭头文案。不可变小对象，
/// 由 <see cref="OverviewViewModel"/> 整体替换实例触发绑定刷新。
/// 颜色不落在数据方向上：<see cref="KpiChangeValence"/> 决定涨跌是「好」还是「坏」，
/// 视图经 <c>Classes.kpi-change-good/bad/neutral</c> 切换 AppAccentGreen/Red/MutedText 令牌
/// （明暗主题由 DynamicResource 兜底）。成本下降是好事——不能沿用「涨=绿、跌=红」的股票色。
/// </summary>
public sealed class KpiCardViewModel
{
    public KpiCardViewModel(string value, MetricChange change, KpiChangeValence valence = KpiChangeValence.Neutral)
    {
        Value = value;
        var arrow = change.Direction switch
        {
            ChangeDirection.Increase => "↑",
            ChangeDirection.Decrease => "↓",
            _ => "→",
        };
        // 百分比不可定义分两种：无对比基准（上一周期无数据 / 当前成本全未知）→ "—"，不伪造数字；
        // 上一周期为 0 而本周期非 0（方向有 ↑/↓、除数为 0）→ 数学上无穷大，如实显示 "∞"。
        // 有一位小数即可读（原型 "↑ 23.6%" 同款）。方向由箭头表达，数值取绝对值避免 "↓ -1.1%" 双重负号。
        var percent = change.PercentChange is { } p
            ? Math.Abs(p).ToString("F1", CultureInfo.InvariantCulture) + "%"
            : change.Direction == ChangeDirection.Neutral ? "—" : "∞";
        ChangeText = $"{arrow} {percent}";

        // 方向 × 极性 → 颜色语义：中性指标永远 muted；有极性时「朝好的方向」为 Positive。
        var hasSentiment = change.Direction != ChangeDirection.Neutral && valence != KpiChangeValence.Neutral;
        var isGoodDirection = (change.Direction == ChangeDirection.Increase) == (valence == KpiChangeValence.HigherIsBetter);
        IsPositive = hasSentiment && isGoodDirection;
        IsNegative = hasSentiment && !isGoodDirection;
        IsNeutral = !hasSentiment;
    }

    /// <summary>加载完成前的占位（诚实：未加载不冒充 0）。</summary>
    public static KpiCardViewModel Pending { get; } = new("—", MetricChange.Neutral);

    /// <summary>主数值（总 Token 紧凑格式 / 成本四态文案 / 会话数千分位 / 占比百分数）。</summary>
    public string Value { get; }

    /// <summary>环比文案（含箭头前缀，如 "↑ 23.6%"、"→ —"；上一周期为 0 → "↑ ∞"）。</summary>
    public string ChangeText { get; }

    /// <summary>朝「更好」的方向（成本下降 / 缓存占比上升）→ 绿。</summary>
    public bool IsPositive { get; }

    /// <summary>朝「更差」的方向（成本上升 / 缓存占比下降）→ 红。</summary>
    public bool IsNegative { get; }

    /// <summary>中性涨跌（用量/会话数）或无对比基准 → muted，不做好坏暗示。</summary>
    public bool IsNeutral { get; }
}

/// <summary>
/// D-007 预览表单行（模型分布 / 项目排行共用形状）：名称、token 数、占比文案与 mini bar 值。
/// 不可变小对象，由 <see cref="OverviewViewModel"/> 整体替换实例集合触发绑定刷新。
/// bar 值口径（任务卡定案）：= 该行占范围内全部维度 token 的百分比（I-029 TokenShare × 100，
/// 宽度即占比——原型用相对最大行的宽度，此处按任务卡改为绝对占比，与「占比」列同值同源）。
/// 圆点色 = 行序映射 AppSeries{N}Brush（普通资源不随明暗翻转，VM 侧解析同 DonutChart 段色取法；
/// 无 Application 环境时为 null，视图侧圆点透明，不影响数据可读性）。
/// </summary>
public sealed class DimensionRowViewModel
{
    public DimensionRowViewModel(int rank, string name, long tokens, decimal? tokenShare, IBrush? dotBrush, ModelAvatarVisual? avatar = null)
    {
        Rank = rank;
        Name = name;
        // 预览表是汇总视图（不是逐条对账表）：紧凑格式与模型/项目页同源，10 位全量数字
        // 在 76px 列里会被裁断（真实数据的十亿级 token 已经踩到）。
        TokensText = TrendChart.FormatCompactTokens(tokens);
        ShareText = OverviewViewModel.FormatShare(tokenShare);
        BarValue = tokenShare is { } share ? Math.Clamp((double)(share * 100m), 0d, 100d) : 0d;
        DotBrush = dotBrush;
        Avatar = avatar;
    }

    /// <summary>排名（1 起；项目表显示，模型表不显示）。</summary>
    public int Rank { get; }

    /// <summary>展示名（项目维度为归一化名——歧义守卫下可能是原始路径；模型/Agent 为 Key）。</summary>
    public string Name { get; }

    /// <summary>token 紧凑文案（万/亿或 K/M；与模型/项目统计页同口径，窄列不裁断）。</summary>
    public string TokensText { get; }

    /// <summary>占比文案（0..1 → "36.8%"；无基数 → "—"，不伪造 0%）。</summary>
    public string ShareText { get; }

    /// <summary>mini bar 值（0..100，= TokenShare × 100；无基数为 0）。</summary>
    public double BarValue { get; }

    /// <summary>行首圆点画刷（行序映射系列色；可空）。</summary>
    public IBrush? DotBrush { get; }

    /// <summary>
    /// 头像视觉（模型维度行填充，模型统计页同源；null = 非模型行——项目行保留系列色圆点）。
    /// 模型行恒非 null（未识别厂商落 generic 中性芯片，不回退圆点）。
    /// </summary>
    public ModelAvatarVisual? Avatar { get; }

    /// <summary>本行是否以模型芯片替代系列圆点（模型维度行 true，项目维度行 false）。</summary>
    public bool HasModelIcon => Avatar is not null;
}

/// <summary>
/// D-006/D-007 总览页 VM：上半 KPI 四卡（总 Token / 估算成本 / 会话数 / 缓存读取占比，含环比）+
/// Token 使用趋势（D-004 TrendChart DailyTokens）+ Agent 使用占比环形（D-005 DonutChart）；
/// 下半（D-007）模型分布表 / Token 类型构成环形 / 项目排行表 / 成本趋势折线。
/// 数据来自 <see cref="IUsageQueryService"/>（I-028 KPI/日序列 + I-029 Agent/模型/项目维度），
/// 参数由全局筛选（<see cref="FilterStateService"/>）映射——TimeRange → 本地自然日窗口
/// （7/30/90 天含端点；All 双端无界），Agent.Id 直传查询服务（"all" → 不过滤）。
/// 生命周期：每次导航新建实例，旧实例由外壳 Dispose 退订常驻 FilterStateService
/// （与 PagePlaceholderViewModel 同一约定）；异步加载用序号防护——筛选快速切换时
/// 旧响应在序号比对处丢弃，绝不覆盖新选择（同 ChartPreviewViewModel 的做法）。
/// 线程约定：实例方法（含 LoadAsync 与筛选订阅回调）按 UI 线程亲和设计——
/// 序号自增与比对无锁，依赖 FilterStateService 已把变更通知 marshal 到 UI 线程。
/// 导航机制（D-007 定案）：构造注入 <see cref="Action{PageId}"/> 回调（外壳把 GoToPage 传入），
/// 页面 VM 不反向引用 MainWindowViewModel（无双向外包依赖）；回调为 null 时跳转命令 no-op。
/// </summary>
public partial class OverviewViewModel : ViewModelBase, IDisposable, IDataRefreshAware
{
    /// <summary>预览表 TopN（模型分布 / 项目排行共用；原型表为 Top5，任务卡定 N≈6，与同页 Agent 环形 Top6 一致）。</summary>
    public const int TopN = 6;

    private readonly FilterStateService _filters;
    private readonly IUsageQueryService _query;
    private readonly ModelAvatarResolver? _avatars;

    /// <summary>外壳导航回调（「查看详情 ↗」用）；null（测试/预览）时跳转命令 no-op。</summary>
    private readonly Action<PageId>? _navigateToPage;

    /// <summary>加载序号：每次触发自增；跨 await 回来时序号不一致的响应直接丢弃。</summary>
    private int _loadSequence;

    public PageDescriptor Page { get; }

    // ── KPI 四卡（原型 .kpis 顺序：总 Token / 估算成本 / 会话数 / 缓存读取占比）──
    [ObservableProperty]
    private KpiCardViewModel _tokensCard = KpiCardViewModel.Pending;

    [ObservableProperty]
    private KpiCardViewModel _costCard = KpiCardViewModel.Pending;

    [ObservableProperty]
    private KpiCardViewModel _sessionsCard = KpiCardViewModel.Pending;

    [ObservableProperty]
    private KpiCardViewModel _cacheCard = KpiCardViewModel.Pending;

    /// <summary>日序列（I-028 双端有界补零窗口；TrendChart DailyTokens 直接消费）。</summary>
    [ObservableProperty]
    private IReadOnlyList<DailyUsagePoint>? _dailyPoints;

    /// <summary>Agent 环形段（token 量 Top6 + 「其他」聚合，段序映射 AppSeries0..6）。</summary>
    [ObservableProperty]
    private IReadOnlyList<DonutSegment>? _agentSegments;

    /// <summary>环中心主文字（= KPI 总 Token 的紧凑格式，对齐原型 donut 中心 "8.74M"）。</summary>
    [ObservableProperty]
    private string _agentDonutCenterTitle = string.Empty;

    // ── D-007 下半区（模型分布 / Token 类型构成 / 项目排行 / 成本趋势）──

    /// <summary>
    /// 模型分布预览行（TopN，服务已按 token 降序；零 token 行不入榜）。
    /// 无「其他」聚合行——原型同款（榜单只列头部，占比列按范围总量独立成立；
    /// 完整清单在模型统计页，经「查看详情 ↗」到达）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelRows))]
    private IReadOnlyList<DimensionRowViewModel>? _modelRows;

    /// <summary>项目排行预览行（TopN；Name 用归一化项目名，歧义守卫下为原始路径）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectRows))]
    private IReadOnlyList<DimensionRowViewModel>? _projectRows;

    /// <summary>
    /// Token 类型构成三段（输入(未缓存)/输出/缓存相关，源 = 日序列聚合，段和 = Σ TotalTokens）。
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<DonutSegment>? _typeSegments;

    /// <summary>类型构成环中心主文字（= KPI 总 Token 的紧凑格式，与上半区同源）。</summary>
    [ObservableProperty]
    private string _typeDonutCenterTitle = string.Empty;

    /// <summary>模型分布表有无数据（驱动诚实空态行）。</summary>
    public bool HasModelRows => ModelRows is { Count: > 0 };

    /// <summary>项目排行表有无数据（驱动诚实空态行）。</summary>
    public bool HasProjectRows => ProjectRows is { Count: > 0 };

    /// <summary>加载失败状态（诚实错误行，不静默吞异常；筛选再变化会自动重试）。</summary>
    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private bool _hasLoadError;

    public OverviewViewModel(
        PageDescriptor page,
        FilterStateService filters,
        IUsageQueryService query,
        ModelAvatarResolver? avatars = null,
        Action<PageId>? navigateToPage = null)
    {
        Page = page;
        _filters = filters;
        _query = query;
        _avatars = avatars;
        _navigateToPage = navigateToPage;
        // FilterStateService 保证通知在 UI 线程发出，这里直接触发重查即可。
        filters.PropertyChanged += OnFilterPropertyChanged;
        _ = LoadAsync();
    }

    /// <summary>「查看详情 ↗」→ 模型统计页（外壳经构造回调注入；无外壳时 no-op）。</summary>
    [RelayCommand]
    private void OpenModels() => _navigateToPage?.Invoke(PageId.Models);

    /// <summary>「查看详情 ↗」→ 项目统计页。</summary>
    [RelayCommand]
    private void OpenProjects() => _navigateToPage?.Invoke(PageId.Projects);

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 只关心会改查询结果的筛选维度（时间档/Agent/自定义起止）；Display 是派生串，忽略避免一次变更触发两次重查
        if (FilterStateService.AffectsQueries(e.PropertyName))
        {
            _ = LoadAsync();
        }
    }

    /// <summary>
    /// 全局筛选 → 查询参数：时间窗口按本地自然日（含端点，今天收口），
    /// Agent.Id 直传（<see cref="AgentOption.AllId"/> → null 不过滤，查询服务语义为空白不过滤）。
    /// D-008 起实现抽到 <see cref="GlobalFilterMapper"/>（会话列表同口径共用）。
    /// </summary>
    internal UsageQueryFilter BuildQueryFilter() => GlobalFilterMapper.Build(_filters);

    /// <summary>后台采集刷新轮次完成后重查本页（IDataRefreshAware）：复用 LoadAsync，序号防护保证与筛选重查并发时旧响应丢弃。</summary>
    public Task OnDataRefreshedAsync() => LoadAsync();

    /// <summary>按当前全局筛选重查整页（序号防护见类型说明）：P0-2 起经复合快照入口一次装载
    /// KPI/日序列/三维聚合——单次 facts + 单次 sessions 全表读，替代逐方法十余次全表重读。</summary>
    public async Task LoadAsync()
    {
        var sequence = ++_loadSequence;
        var filter = BuildQueryFilter();

        try
        {
            var snapshot = await _query.GetOverviewSnapshotAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            ApplyKpi(snapshot.Kpi);
            // 日序列同源复用：上半 Token 趋势与下半成本趋势（CostUsd）吃同一份点（I-028 单列成本），
            // 不为成本趋势另发一次查询（同窗口同筛选必然同结果）。
            DailyPoints = snapshot.Daily;
            AgentSegments = CreateAgentSegments(snapshot.Agents);
            AgentDonutCenterTitle = TrendChart.FormatCompactTokens(snapshot.Kpi.TotalTokens);
            ModelRows = CreateDimensionRows(snapshot.Models, modelIconResolver: _avatars is { } avatars ? avatars.Resolve : null);
            ProjectRows = CreateDimensionRows(snapshot.Projects);
            TypeSegments = CreateTokenTypeSegments(snapshot.Daily);
            // 环心与上半区 KPI 卡同源（同一份快照 KPI），避免环心/段各算各的口径漂移
            TypeDonutCenterTitle = TrendChart.FormatCompactTokens(snapshot.Kpi.TotalTokens);
            HasLoadError = false;
            LoadError = null;
        }
        catch (Exception ex)
        {
            if (sequence != _loadSequence)
            {
                return; // 旧请求的失败不覆盖新请求的状态
            }

            HasLoadError = true;
            LoadError = string.Format(Translations.Instance.LoadErrorFormat, ex.Message);
        }
    }

    // ── 展示映射（internal 供测试对账：stub KPI 值 → 卡片显示值，TC-001 页面层分工）──

    private void ApplyKpi(UsageKpiResult kpi)
    {
        // PreviousPeriod 为 null（如"全部时间"无界窗口）→ 平箭头 + "—"，不臆造对比。
        // 极性：用量/会话数中性（涨跌无好坏），成本越低越好，缓存命中越高越好。
        TokensCard = new KpiCardViewModel(
            TrendChart.FormatCompactTokens(kpi.TotalTokens),
            kpi.PreviousPeriod?.TotalTokensChange ?? MetricChange.Neutral,
            KpiChangeValence.Neutral);
        CostCard = new KpiCardViewModel(
            FormatCost(kpi.Cost),
            kpi.PreviousPeriod?.CostChange ?? MetricChange.Neutral,
            KpiChangeValence.LowerIsBetter);
        SessionsCard = new KpiCardViewModel(
            kpi.SessionCount.ToString("N0", CultureInfo.InvariantCulture),
            kpi.PreviousPeriod?.SessionCountChange ?? MetricChange.Neutral,
            KpiChangeValence.Neutral);
        CacheCard = new KpiCardViewModel(
            FormatShare(kpi.CacheReadShare),
            kpi.PreviousPeriod?.CacheReadShareChange ?? MetricChange.Neutral,
            KpiChangeValence.HigherIsBetter);
    }

    /// <summary>
    /// 成本 KPI 四态文案（未知成本纪律：未知绝不显示 $0.00）：
    /// (x,false) "$x.xx"；(x,true) "$x.xx+"（已知成本下限）；(null,true) "未知"；(null,false) "—"（无成本数据）。
    /// </summary>
    internal static string FormatCost(AggregatedCost cost) => cost.KnownCostUsd is { } known
        ? TrendChart.FormatCostUsd(known, cost.HasUnknownCost) ?? Translations.Instance.Unknown
        : cost.HasUnknownCost ? Translations.Instance.Unknown : "—";

    /// <summary>占比文案：0..1 → "67.3%"（对齐原型一位小数）；无基数（总 token 为 0）→ "—"。</summary>
    internal static string FormatShare(decimal? share) => share is { } value
        ? (value * 100m).ToString("F1", CultureInfo.InvariantCulture) + "%"
        : "—";

    /// <summary>
    /// Agent 维度 → 环形段：token 量 Top6 + 「其他」聚合（超 7 系列色的原型同款处理，
    /// 同 ChartPreviewViewModel）；零 token 行不入环（占比无意义）。
    /// </summary>
    internal static IReadOnlyList<DonutSegment> CreateAgentSegments(IReadOnlyList<DimensionUsageRow> agents)
    {
        var withTokens = agents
            .Where(a => a.TotalTokens > 0)
            .OrderByDescending(a => a.TotalTokens)
            .ToList();
        var segments = withTokens
            .Take(6)
            .Select(a => new DonutSegment(a.Key, a.TotalTokens))
            .ToList();
        var rest = withTokens.Skip(6).ToList();
        if (rest.Count > 0)
        {
            segments.Add(new DonutSegment(Translations.Instance.OthersSegment, rest.Sum(a => a.TotalTokens)));
        }

        return segments;
    }

    // ════════════════════════════ D-007 下半区映射（internal 供测试对账）════════════════════════════

    /// <summary>Token 类型构成图例名（定案口径：契约的 InputTokens 本身不含缓存，见 CreateTokenTypeSegments）。
    /// 经 Translations 实时取串（页面 VM 每次导航新建；测试引用属性即当前语言文案）。</summary>
    public static string TokenTypeInputLabel => Translations.Instance.TokenTypeInput;
    public static string TokenTypeOutputLabel => Translations.Instance.TokenTypeOutput;
    public static string TokenTypeCacheLabel => Translations.Instance.TokenTypeCache;

    /// <summary>
    /// 模型/项目维度 → 预览表行：TopN、零 token 行不入榜（占比无意义）、防御性按 token 降序
    /// （服务契约已排序，不依赖之）；Name 用归一化展示名（项目维度歧义守卫下可能是原始路径）。
    /// 不加「其他」聚合行（原型同款，见 ModelRows 注释）；bar 值 = TokenShare × 100（见 DimensionRowViewModel）。
    /// <paramref name="modelIconResolver"/> 非空时行首给内置厂商图标（模型维度；null = 项目维度，
    /// 保留系列色圆点）。
    /// </summary>
    internal static IReadOnlyList<DimensionRowViewModel> CreateDimensionRows(
        IReadOnlyList<DimensionUsageRow> rows, int topN = TopN, Func<string, ModelAvatarVisual?>? modelIconResolver = null)
    {
        return rows
            .Where(r => r.TotalTokens > 0)
            .OrderByDescending(r => r.TotalTokens)
            .Take(topN)
            .Select((row, index) => new DimensionRowViewModel(
                index + 1,
                row.DisplayName ?? row.Key,
                row.TotalTokens,
                row.TokenShare,
                ResolveSeriesBrush(index),
                modelIconResolver?.Invoke(row.DisplayName ?? row.Key)))
            .ToList();
    }

    /// <summary>
    /// 日序列 → Token 类型构成三段（三段映射定案，任务卡要求读契约后落定）：
    /// <list type="bullet">
    /// <item>输入(未缓存) = Σ InputTokens——契约证据：<see cref="UsageRecord.TotalTokens"/>
    /// = Input + Output + CacheCreation + CacheRead，即 InputTokens 本身<b>不含缓存</b>（未缓存输入），</item>
    /// <item>输出 = Σ OutputTokens，</item>
    /// <item>缓存相关 = Σ (CacheCreation + CacheRead)——写入与读取合一段（原型「缓存相关」同口径）。</item>
    /// </list>
    /// 三段之和 = Σ TotalTokens，环心用 KPI 总 Token（同库同窗口，与上半区同源）。
    /// 口径说明：个别适配器按"展示桶"归并（如 ZCode Z.ai 把 cacheCreation 并入 InputTokens、
    /// Kilo/OpenCode/Goose 把 reasoning/余量并入 Output）——总额不变式不受影响，但这两段
    /// 对这类源是"来源展示桶"语义；让位记录见各适配器卡（I-006/I-016/I-009/I-012 等）。
    /// 数据源用已取回的日序列（与趋势图同一次查询），不另发请求。全零 → 空集（诚实空态，
    /// DonutChart 呈空环）；个别段为 0 时保留该段（图例如实显示 0.0%）。
    /// </summary>
    internal static IReadOnlyList<DonutSegment> CreateTokenTypeSegments(IReadOnlyList<DailyUsagePoint> daily)
    {
        long input = 0, output = 0, cache = 0;
        foreach (var point in daily)
        {
            input += point.InputTokens;
            output += point.OutputTokens;
            cache += point.CacheCreationTokens + point.CacheReadTokens;
        }

        if (input + output + cache == 0)
        {
            return Array.Empty<DonutSegment>();
        }

        return new[]
        {
            new DonutSegment(TokenTypeInputLabel, input, TokenTypeColor(
                "AppTokenTypeInputColor", 0x4c, 0x8d, 0xff)),
            new DonutSegment(TokenTypeOutputLabel, output, TokenTypeColor(
                "AppTokenTypeOutputColor", 0xa7, 0x79, 0xff)),
            new DonutSegment(TokenTypeCacheLabel, cache, TokenTypeColor(
                "AppTokenTypeCacheColor", 0x38, 0xd6, 0xa4)),
        };
    }

    /// <summary>系列色画刷（行序圆点）：普通资源不随明暗翻转，无 Application 环境（纯单测）返回 null。</summary>
    private static IBrush? ResolveSeriesBrush(int index) =>
        Application.Current?.TryGetResource($"AppSeries{index % 7}Brush", ThemeVariant.Default, out var value) == true
            && value is IBrush brush ? brush : null;

    /// <summary>类型构成段色：令牌缺资源时回退原型 typeDonut 原值（与 TrendChart.TokenColor 同纪律）。</summary>
    private static Color TokenTypeColor(string key, byte r, byte g, byte b) =>
        Application.Current?.TryGetResource(key, ThemeVariant.Default, out var value) == true
            && value is Color color
                ? color
                : Color.FromRgb(r, g, b);

    public void Dispose() => _filters.PropertyChanged -= OnFilterPropertyChanged;
}
