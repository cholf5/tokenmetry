using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.App.Headless.Tests.Controls;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// D-006 总览页（上半）的自动化验证（headless，真实 App.axaml + Semi）：
/// 1. KPI 四卡绑定 stub IUsageQueryService 的值（数值 / 环比箭头方向 / 未知成本四态文案）；
/// 2. 筛选切换触发重查且 TimeRange/Agent → UsageQueryFilter 映射正确（本地日窗口、"all" 不过滤）；
/// 3. 快速切换筛选时加载序号防护生效（旧响应不覆盖新选择）；
/// 4. Agent 环形段与 GetAgentBreakdownAsync 结果一致（Top6 + 其他、环心 = KPI 总 Token）；
/// 5. Dispose 退订全局筛选（常驻单例订阅不退订会随导航次数无界累积的回归防线）；
/// 6. 真实查询服务冒烟（同口径直查对账，环境无关）；
/// 7. 明暗两套离屏截图留档（docs/design/overview-*.png）。
/// TC-001（KPI 数值对账）分工：查询层 SQL 对账在 UsageQueryServiceTests 已完成，
/// 本类做"stub 值 → 卡片显示值"的绑定对账；真实服务一层只做同口径直查对账冒烟。
/// </summary>
public class OverviewTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_OVERVIEW_PREVIEW_DIR");

    // ── stub 查询服务（可配置 KPI/日序列/Agent 维度 + 调用记账 + KPI 响应挂起）──

    private sealed class StubQueryService : IUsageQueryService
    {
        public UsageKpiResult Kpi { get; set; } = new();

        public IReadOnlyList<DailyUsagePoint> Daily { get; set; } = Array.Empty<DailyUsagePoint>();

        public IReadOnlyList<DimensionUsageRow> Agents { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Models { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Projects { get; set; } = Array.Empty<DimensionUsageRow>();

        /// <summary>单日窗口的小时分布（D-027；非单日窗口不该被查）。</summary>
        public HourlyDistributionResult Hourly { get; set; } = new();

        public int HourlyCalls { get; private set; }

        public int KpiCalls { get; private set; }

        public int DailyCalls { get; private set; }

        public int AgentCalls { get; private set; }

        public int ModelCalls { get; private set; }

        public int ProjectCalls { get; private set; }

        public List<UsageQueryFilter> KpiFilters { get; } = new();

        private TaskCompletionSource<UsageKpiResult>? _heldKpi;
        private UsageKpiResult? _heldResult;

        /// <summary>让下一次 GetKpiAsync 挂起，ReleaseHeldKpi 时以指定结果放行（序号防护用例）。</summary>
        public void HoldNextKpi(UsageKpiResult resultOnRelease)
        {
            _heldResult = resultOnRelease;
            _heldKpi = new TaskCompletionSource<UsageKpiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldKpi() => _heldKpi?.TrySetResult(_heldResult ?? Kpi);

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            KpiCalls++;
            KpiFilters.Add(filter);
            if (_heldKpi is { } held)
            {
                _heldKpi = null;
                return held.Task;
            }

            return Task.FromResult(Kpi);
        }

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            DailyCalls++;
            return Task.FromResult(Daily);
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            AgentCalls++;
            return Task.FromResult(Agents);
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            ModelCalls++;
            return Task.FromResult(Models);
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            ProjectCalls++;
            return Task.FromResult(Projects);
        }

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            HourlyCalls++;
            return Task.FromResult(Hourly);
        }
    }

    // ── fixture ──

    private static UsageKpiResult FixtureKpi { get; } = new()
    {
        TotalTokens = 8_740_000,
        Cost = new AggregatedCost(286.41m, hasUnknownCost: false),
        SessionCount = 412,
        CacheReadShare = 0.673m,
        PreviousPeriod = new KpiPeriodComparison
        {
            TotalTokensChange = new MetricChange(ChangeDirection.Increase, 23.6m),
            CostChange = new MetricChange(ChangeDirection.Decrease, 18.2m),
            SessionCountChange = new MetricChange(ChangeDirection.Increase, 12.8m),
            CacheReadShareChange = new MetricChange(ChangeDirection.Increase, null), // 上一周期为 0 → 百分比不可定义
        },
    };

    /// <summary>同 FixtureKpi 但无上一周期（I-028 契约：无界范围没有 PreviousPeriod）。</summary>
    private static UsageKpiResult FixtureKpiNoPrevious { get; } = new()
    {
        TotalTokens = FixtureKpi.TotalTokens,
        Cost = FixtureKpi.Cost,
        SessionCount = FixtureKpi.SessionCount,
        CacheReadShare = FixtureKpi.CacheReadShare,
    };

    private static IReadOnlyList<DimensionUsageRow> FixtureAgents { get; } = new[]
    {
        Row("Claude Code", 4_120_000),
        Row("Codex", 2_260_000),
        Row("Cursor", 1_580_000),
        Row("GitHub Copilot", 870_000),
    };

    /// <summary>模型维度 fixture（D-007 预览表；share 按范围总量 8,740,000 记）。</summary>
    private static IReadOnlyList<DimensionUsageRow> FixtureModels { get; } = new[]
    {
        Row("claude-sonnet-4-5", 3_215_482),
        Row("gpt-4o", 1_842_317),
    };

    /// <summary>项目维度 fixture（D-007 预览表；DisplayName 为归一化项目名）。</summary>
    private static IReadOnlyList<DimensionUsageRow> FixtureProjects { get; } = new[]
    {
        ProjectRow("/Users/dev/project-a", "Project-A", 2_341_628),
        ProjectRow("/Users/dev/ai-project", "AI-Project", 1_682_317),
    };

    private static DimensionUsageRow Row(string key, long tokens) => new()
    {
        Key = key,
        SessionCount = 10,
        TotalTokens = tokens,
        Cost = new AggregatedCost(1m, false),
        TokenShare = tokens / 8_740_000m,
    };

    private static DimensionUsageRow ProjectRow(string path, string displayName, long tokens) => new()
    {
        Key = path,
        DisplayName = displayName,
        SessionCount = 10,
        TotalTokens = tokens,
        Cost = new AggregatedCost(1m, false),
        TokenShare = tokens / 8_740_000m,
    };

    private static OverviewViewModel CreateVm(StubQueryService stub, FilterStateService? filters = null) =>
        new(ShellPages.For(PageId.Overview), filters ?? new FilterStateService(), stub);

    [AvaloniaFact]
    public void Overview_page_loads_via_composite_snapshot_query()
    {
        // P0-2：总览页必须经复合快照入口一次装载（KPI/日序列/三维度）——五个分量方法的返回值
        // 不再被消费（stub 恒空），页面数据全部来自 GetOverviewSnapshotAsync 的快照。
        var stub = new SnapshotOnlyStub();
        var vm = new OverviewViewModel(ShellPages.For(PageId.Overview), new FilterStateService(), stub);

        Assert.Equal("555万", vm.TokensCard.Value);
        Assert.NotNull(vm.DailyPoints);
        Assert.Single(vm.DailyPoints!);
        Assert.NotEmpty(vm.AgentSegments!);
        Assert.NotEmpty(vm.ModelRows!);
        Assert.NotEmpty(vm.ProjectRows!);
        // 页面数据面收敛到复合快照后，分量方法零调用。
        Assert.Equal(0, stub.IndividualMethodCalls);
    }

    /// <summary>只有复合快照产数据的查询桩：五个分量方法返回空值并记账（P0-2c 回归锁）。</summary>
    private sealed class SnapshotOnlyStub : IUsageQueryService
    {
        public int IndividualMethodCalls { get; private set; }

        public Task<OverviewPageSnapshot> GetOverviewSnapshotAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new OverviewPageSnapshot
            {
                Kpi = new UsageKpiResult { TotalTokens = 5_550_000 },
                Daily = [new DailyUsagePoint { Day = "2026-03-10", TotalTokens = 5_550_000, HasData = true }],
                Agents = [new DimensionUsageRow { Key = "Snapshot Agent", TotalTokens = 5_550_000, SessionCount = 3 }],
                Models = [new DimensionUsageRow { Key = "snapshot-model", TotalTokens = 5_550_000 }],
                Projects = [new DimensionUsageRow { Key = "/snapshot/project", TotalTokens = 5_550_000 }],
            });

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) => EmptyDaily();

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) => CountEmpty();

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => CountRows();

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => CountRows();

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => CountRows();

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            Task.FromResult(new SessionListResult());

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult());

        private Task<UsageKpiResult> CountEmpty()
        {
            IndividualMethodCalls++;
            return Task.FromResult(new UsageKpiResult());
        }

        private Task<IReadOnlyList<DimensionUsageRow>> CountRows()
        {
            IndividualMethodCalls++;
            return Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);
        }

        private Task<IReadOnlyList<DailyUsagePoint>> EmptyDaily()
        {
            IndividualMethodCalls++;
            return Task.FromResult<IReadOnlyList<DailyUsagePoint>>([]);
        }
    }

    // ── KPI 绑定（TC-001 页面层：stub 值 → 卡片显示值）──

    [AvaloniaFact]
    public void Kpi_cards_bind_stub_values_with_change_directions()
    {
        var vm = CreateVm(new StubQueryService
        {
            Kpi = FixtureKpi,
            Daily = ChartTestHelpers.DailyPoints30(),
            Agents = FixtureAgents,
            Models = FixtureModels,
            Projects = FixtureProjects,
        });

        // 总 Token：紧凑格式（原型 "8.74M"；zh 基线为万档）+ ↑ 方向；用量类指标无好坏 → 中性色
        Assert.Equal("874万", vm.TokensCard.Value);
        Assert.Equal("↑ 23.6%", vm.TokensCard.ChangeText);
        Assert.True(vm.TokensCard.IsNeutral);
        Assert.False(vm.TokensCard.IsPositive);
        Assert.False(vm.TokensCard.IsNegative);

        // 估算成本：已知成本两位小数；成本下降是好事 → 正向（绿），不是红
        Assert.Equal("$286.41", vm.CostCard.Value);
        Assert.Equal("↓ 18.2%", vm.CostCard.ChangeText);
        Assert.True(vm.CostCard.IsPositive);
        Assert.False(vm.CostCard.IsNegative);

        // 会话数：千分位；用量类指标 → 中性
        Assert.Equal("412", vm.SessionsCard.Value);
        Assert.Equal("↑ 12.8%", vm.SessionsCard.ChangeText);
        Assert.True(vm.SessionsCard.IsNeutral);

        // 缓存读取占比：0..1 → 一位小数百分数；上一周期为 0 → 箭头有方向、百分比呈 "∞"（除数为 0 的如实表达）。
        // 缓存命中上升是好事 → 正向（绿）
        Assert.Equal("67.3%", vm.CacheCard.Value);
        Assert.Equal("↑ ∞", vm.CacheCard.ChangeText);
        Assert.True(vm.CacheCard.IsPositive);

        // 日序列与 Agent 段已装载；D-007 预览行已就位
        Assert.NotNull(vm.DailyPoints);
        Assert.NotEmpty(vm.AgentSegments!);
        Assert.NotEmpty(vm.ModelRows!);
        Assert.NotEmpty(vm.ProjectRows!);
    }

    [AvaloniaTheory]
    [InlineData("286.41", false, "$286.41")]        // 已知成本
    [InlineData("12.50", true, "$12.50+")]          // 部分未知 → 已知成本下限
    [InlineData("", true, "未知")]                  // 全部未知 → 不折 0
    [InlineData("", false, "—")]                    // 无成本数据
    [InlineData("0", false, "$0.00")]               // 已知为零（与未知严格区分）
    public void Cost_kpi_shows_four_state_contract_never_zero_for_unknown(string knownText, bool hasUnknown, string expected)
    {
        var known = knownText switch
        {
            "" => (decimal?)null,
            "0" => 0m,
            _ => decimal.Parse(knownText, CultureInfo.InvariantCulture),
        };
        var stub = new StubQueryService
        {
            Kpi = new UsageKpiResult { Cost = new AggregatedCost(known, hasUnknown) },
        };
        var vm = CreateVm(stub);

        Assert.Equal(expected, vm.CostCard.Value);
    }

    [AvaloniaFact]
    public void Empty_range_shows_honest_zero_and_dash_not_fake_data()
    {
        // 空窗口（KPI 全零 / 无基数）：计数如实为 0，成本与占比留 "—"，环比无对比 → 平 "→ —"
        var vm = CreateVm(new StubQueryService
        {
            Kpi = new UsageKpiResult { Cost = AggregatedCost.Empty },
            Daily = ChartTestHelpers.AllPadded30(),
        });

        Assert.Equal("0", vm.TokensCard.Value);
        Assert.Equal("0", vm.SessionsCard.Value);
        Assert.Equal("—", vm.CostCard.Value);
        Assert.Equal("—", vm.CacheCard.Value);
        Assert.Equal("→ —", vm.TokensCard.ChangeText);
        Assert.True(vm.TokensCard.IsNeutral);
        Assert.Null(vm.LoadError);
    }

    // ── D-027：单日窗口切小时形态（今天/昨天单点死图收口）──

    /// <summary>小时分布 fixture：10/14 点高峰、每桶都有成本；3 个会话无分钟级精度（unknown 可见）。</summary>
    private static HourlyDistributionResult FixtureHourly { get; } = new()
    {
        Hours = Enumerable.Range(0, 24).Select(h => new HourlyUsageBucket
        {
            Hour = h,
            SessionCount = h is 10 or 14 ? 4 : 1,
            TotalTokens = h * 1_000,
            Cost = new AggregatedCost(1.5m, false),
        }).ToList(),
        UnknownSessionCount = 3,
        UnknownTotalTokens = 12_000,
        TimeZoneId = TimeZoneInfo.Local.Id,
    };

    [AvaloniaFact]
    public void Single_day_window_switches_trend_charts_to_hourly_mode()
    {
        var filters = new FilterStateService { TimeRange = TimeRangeOption.Today };
        var stub = new StubQueryService
        {
            Kpi = FixtureKpi,
            Daily = ChartTestHelpers.AllPadded30(),
            Agents = FixtureAgents,
            Hourly = FixtureHourly,
        };
        var vm = CreateVm(stub, filters);

        // 单日窗口：两张趋势图切小时形态，小时数据 + 已发生边界 + unknown 标注一次落位
        Assert.True(vm.IsSingleDayWindow);
        Assert.Equal(TrendChartMode.HourlyTokens, vm.TokenTrendMode);
        Assert.Equal(TrendChartMode.HourlyCost, vm.CostTrendMode);
        Assert.Same(FixtureHourly, vm.HourlyDistribution);
        Assert.Equal(DateTimeOffset.Now.Hour, vm.HourlyCutoffHour); // 今天 → 当前小时（含，本小时进行中也是真实数据）

        // unknown 占比与时间分析页同一实现（口径同级不是「看起来差不多」）
        Assert.Equal(
            TimeViewModel.BuildUnknownNote(3, TimeViewModel.SumKnownSessions(FixtureHourly.Hours), 12_000),
            vm.HourlyUnknownNoteText);
        Assert.True(vm.HasHourlyUnknownNote);

        // 回到多日窗口：形态复位、小时数据清空，日序列仍在（非单日窗口行为零变化）
        filters.TimeRange = TimeRangeOption.Last7Days;
        Assert.False(vm.IsSingleDayWindow);
        Assert.Equal(TrendChartMode.DailyTokens, vm.TokenTrendMode);
        Assert.Equal(TrendChartMode.CostUsd, vm.CostTrendMode);
        Assert.Null(vm.HourlyDistribution);
        Assert.Null(vm.HourlyCutoffHour);
        Assert.Null(vm.HourlyUnknownNoteText);
        Assert.False(vm.HasHourlyUnknownNote);
        Assert.NotNull(vm.DailyPoints);
    }

    [AvaloniaFact]
    public void Multi_day_window_does_not_query_hourly_distribution()
    {
        // 默认 7 天档：小时查询一次都不发（多日窗口不走小时形态，不多付一次 sessions 读）
        var stub = new StubQueryService { Kpi = FixtureKpi, Hourly = FixtureHourly };
        var vm = CreateVm(stub);
        Assert.False(vm.IsSingleDayWindow);
        Assert.Null(vm.HourlyDistribution);
        Assert.Equal(0, stub.HourlyCalls);
    }

    [AvaloniaFact]
    public void Single_day_yesterday_window_has_no_future_hour_cutoff()
    {
        // 昨天档 0..23 全是真实过去：不做淡化处理（cutoff null）
        var filters = new FilterStateService { TimeRange = TimeRangeOption.Yesterday };
        var vm = CreateVm(new StubQueryService { Kpi = FixtureKpi, Hourly = FixtureHourly }, filters);
        Assert.True(vm.IsSingleDayWindow);
        Assert.Null(vm.HourlyCutoffHour);
    }

    [AvaloniaFact]
    public void Hourly_cutoff_hour_and_single_day_detection_are_pure()
    {
        var local = new DateTime(2026, 5, 20, 9, 15, 0, DateTimeKind.Unspecified);
        var now = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));

        Assert.Equal(9, OverviewViewModel.ResolveHourlyCutoffHour("2026-05-20", now));   // 今天 → 当前小时
        Assert.Null(OverviewViewModel.ResolveHourlyCutoffHour("2026-05-19", now));        // 昨天 → 全已发生
        Assert.Equal(-1, OverviewViewModel.ResolveHourlyCutoffHour("2026-05-21", now));  // 未来单日 → 整天未到
        Assert.Null(OverviewViewModel.ResolveHourlyCutoffHour(null, now));                // 无上界 → 不适用

        Assert.True(OverviewViewModel.IsSingleDayFilter(
            new UsageQueryFilter { FromDay = "2026-05-20", ToDay = "2026-05-20" }));
        Assert.False(OverviewViewModel.IsSingleDayFilter(
            new UsageQueryFilter { FromDay = "2026-05-19", ToDay = "2026-05-20" })); // 两日窗口不扩
        Assert.False(OverviewViewModel.IsSingleDayFilter(
            new UsageQueryFilter { FromDay = "2026-05-20" }));                       // 单边无界
        Assert.False(OverviewViewModel.IsSingleDayFilter(new UsageQueryFilter()));  // 全部时间
    }

    [AvaloniaFact]
    public void Overview_view_renders_hourly_trends_for_single_day_window()
    {
        var filters = new FilterStateService { TimeRange = TimeRangeOption.Today };
        var vm = CreateVm(new StubQueryService
        {
            Kpi = FixtureKpi,
            Daily = ChartTestHelpers.AllPadded30(),
            Agents = FixtureAgents,
            Hourly = FixtureHourly,
        }, filters);

        var window = CreateOverviewWindow(vm, ThemeVariant.Dark);
        var frame = ChartTestHelpers.RenderFrame(window);
        ChartTestHelpers.SaveArchive(frame, ArchiveDir, "overview-hourly-dark.png");

        // 编译绑定真的把两张图切到小时形态（属性名拼错 / 漏绑会在这里露出来）
        var charts = window.GetVisualDescendants().OfType<TrendChart>().ToList();
        Assert.Equal(2, charts.Count);
        Assert.Contains(charts, c => c.Mode == TrendChartMode.HourlyTokens && c.HourlyCutoffHour is not null);
        Assert.Contains(charts, c => c.Mode == TrendChartMode.HourlyCost);

        // 口径标注可见（按会话开始时间归集 + unknown 占比）
        var attribution = ChartTestHelpers.FindTextBlock(window, Translations.Instance.AttributionSessionStart);
        Assert.NotNull(attribution);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(attribution!));

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 筛选 → 查询参数映射 + 重查触发 ──

    [AvaloniaFact]
    public void Filter_switch_requeries_with_mapped_window_and_agent()
    {
        var filters = new FilterStateService();
        var stub = new StubQueryService { Kpi = FixtureKpiNoPrevious, Daily = ChartTestHelpers.DailyPoints30(), Agents = FixtureAgents };
        var vm = CreateVm(stub, filters);

        // 构造即按默认筛选（最近 7 天 · 所有 Agent）查了一次
        Assert.Equal(1, stub.KpiCalls);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var initial = stub.KpiFilters[0];
        Assert.Equal(UsageQueryFilter.FormatDay(today.AddDays(-6)), initial.FromDay);
        Assert.Equal(UsageQueryFilter.FormatDay(today), initial.ToDay);
        Assert.Null(initial.Agent); // "all" 不过滤

        // 30 天：窗口本地自然日、含端点
        filters.TimeRange = TimeRangeOption.Last30Days;
        Assert.Equal(2, stub.KpiCalls);
        Assert.Equal(UsageQueryFilter.FormatDay(today.AddDays(-29)), stub.KpiFilters[^1].FromDay);
        Assert.Equal(UsageQueryFilter.FormatDay(today), stub.KpiFilters[^1].ToDay);
        Assert.Null(stub.KpiFilters[^1].Agent);

        // Agent 精确过滤直传选项 Id
        filters.Agent = new AgentOption("Codex", "Codex"); // D-008 起无占位清单，直接构造选项值
        Assert.Equal(3, stub.KpiCalls);
        Assert.Equal("Codex", stub.KpiFilters[^1].Agent);

        // 全部时间：双端无界 → 无上一周期，环比呈平箭头 "→ —"（不臆造对比）
        filters.TimeRange = TimeRangeOption.All;
        Assert.Equal(4, stub.KpiCalls);
        Assert.Null(stub.KpiFilters[^1].FromDay);
        Assert.Null(stub.KpiFilters[^1].ToDay);
        Assert.Equal("Codex", stub.KpiFilters[^1].Agent); // Agent 维度保留
        Assert.True(vm.TokensCard.IsNeutral);
        Assert.Equal("→ —", vm.TokensCard.ChangeText);
    }

    [AvaloniaFact]
    public void Rapid_filter_switch_discards_stale_responses()
    {
        var filters = new FilterStateService();
        var staleKpi = new UsageKpiResult
        {
            TotalTokens = 3_100_000,
            Cost = new AggregatedCost(31m, false),
            SessionCount = 31,
        };
        var stub = new StubQueryService
        {
            Kpi = new UsageKpiResult { TotalTokens = 9_500_000, SessionCount = 95 }, // 90 天窗口结果
            Daily = ChartTestHelpers.DailyPoints30(),
            Agents = FixtureAgents,
        };
        var vm = CreateVm(stub, filters);

        // 第 2 次查询（切 30 天）在 KPI 处挂起；第 3 次（切 90 天）同步走完
        stub.HoldNextKpi(staleKpi);
        filters.TimeRange = TimeRangeOption.Last30Days; // 30 天 → 挂起
        filters.TimeRange = TimeRangeOption.Last90Days; // 90 天 → 完成
        Assert.Equal("950万", vm.TokensCard.Value);

        // 放行旧响应：序号防护使其被丢弃，UI 仍是新选择（90 天）的结果
        stub.ReleaseHeldKpi();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("950万", vm.TokensCard.Value);
        Assert.NotEqual("310万", vm.TokensCard.Value);
        // 挂起的那次只走到 KPI 就被丢弃，日序列/Agent/模型/项目维度不再查（D-007 新查询同走序号防护路径）
        Assert.Equal(3, stub.KpiCalls);
        Assert.Equal(2, stub.DailyCalls);
        Assert.Equal(2, stub.AgentCalls);
        Assert.Equal(2, stub.ModelCalls);
        Assert.Equal(2, stub.ProjectCalls);
    }

    // ── Agent 环形段 ──

    [AvaloniaFact]
    public void Agent_segments_match_breakdown_top6_plus_other()
    {
        var stub = new StubQueryService
        {
            Kpi = FixtureKpi,
            Daily = ChartTestHelpers.DailyPoints30(),
            Agents = new[]
            {
                Row("Claude Code", 4_120_000),
                Row("Codex", 2_260_000),
                Row("Cursor", 1_580_000),
                Row("GitHub Copilot", 870_000),
                Row("Cline", 500_000),
                Row("Codex CLI", 300_000),
                Row("Gemini CLI", 120_000),
                Row("Droid", 60_000),
            },
        };
        var vm = CreateVm(stub);

        // token 量 Top6 + 「其他」聚合（第 7、8 行合并），零 token 行不入环
        var names = vm.AgentSegments!.Select(s => s.Name).ToArray();
        var values = vm.AgentSegments!.Select(s => s.Value).ToArray();
        Assert.Equal(
            new[] { "Claude Code", "Codex", "Cursor", "GitHub Copilot", "Cline", "Codex CLI", "其他" },
            names);
        Assert.Equal(4_120_000, values[0]);
        Assert.Equal(120_000 + 60_000, values[^1]);
        // 环心 = KPI 总 Token（紧凑），与卡片同源
        Assert.Equal("874万", vm.AgentDonutCenterTitle);
    }

    [AvaloniaFact]
    public void Agent_breakdown_all_zero_shows_honest_empty_donut()
    {
        var stub = new StubQueryService
        {
            Kpi = new UsageKpiResult(),
            Agents = new[] { Row("Claude Code", 0) },
        };
        var vm = CreateVm(stub);

        Assert.Empty(vm.AgentSegments!); // DonutChart 自带空态（空环 + 暂无数据），VM 不造假段
        Assert.Equal("0", vm.AgentDonutCenterTitle);
    }

    // ── Dispose 退订 ──

    [AvaloniaFact]
    public void Dispose_stops_reload_on_filter_change()
    {
        var filters = new FilterStateService();
        var stub = new StubQueryService { Kpi = FixtureKpi };
        var vm = CreateVm(stub, filters);
        Assert.Equal(1, stub.KpiCalls);

        vm.Dispose();
        filters.TimeRange = TimeRangeOption.Last30Days;
        filters.Agent = new AgentOption("Claude Code", "Claude Code");

        // FilterStateService 是常驻单例：页面 VM 不退订会随导航次数无界累积（D-003 审查 P1 同源回归）
        Assert.Equal(1, stub.KpiCalls);
        Assert.Equal(1, stub.DailyCalls);
        Assert.Equal(1, stub.AgentCalls);
        Assert.Equal(1, stub.ModelCalls);
        Assert.Equal(1, stub.ProjectCalls);
    }

    // ── 视图渲染（stub 数据，明暗两套 + 截图留档）──

    private static Window CreateOverviewWindow(OverviewViewModel vm, ThemeVariant variant) => new()
    {
        Width = 1020,
        Height = 1160, // D-007 后整页更高：离屏渲染含两个 gridEqual 下行
        Content = new OverviewView { DataContext = vm },
        RequestedThemeVariant = variant,
        Background = Application.Current!.TryGetResource("AppBackgroundBrush", variant, out var bg)
            && bg is IBrush brush ? brush : new SolidColorBrush(Color.Parse("#0f1118")),
    };

    [AvaloniaFact]
    public void Overview_view_renders_stub_data_in_light_and_dark()
    {
        var vm = CreateVm(new StubQueryService
        {
            Kpi = FixtureKpi,
            Daily = ChartTestHelpers.DailyPoints30(),
            Agents = FixtureAgents,
            Models = FixtureModels,
            Projects = FixtureProjects,
        });

        foreach (var (variant, file) in new (ThemeVariant, string)[]
                 {
                     (ThemeVariant.Light, "overview-light.png"),
                     (ThemeVariant.Dark, "overview-dark.png"),
                 })
        {
            var window = CreateOverviewWindow(vm, variant);
            var frame = ChartTestHelpers.RenderFrame(window);

            // KPI 值、Agent 图例、趋势图空态不可见（stub 有数据）
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "874万"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "$286.41"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "412"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "67.3%"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "Claude Code")); // 环形图例行

            // 环比颜色 = 语义极性（KpiChangeValence，回归防线）：
            // 用量上升无好坏 → muted；成本下降是好事 → 绿（不再沿用「涨=绿、跌=红」的方向色）
            Assert.True(Application.Current!.TryGetResource("AppAccentGreenBrush", variant, out var green));
            Assert.True(Application.Current!.TryGetResource("AppMutedTextBrush", variant, out var muted));
            var upText = ChartTestHelpers.FindTextBlock(window, "↑ 23.6%");
            Assert.NotNull(upText);
            Assert.Same(muted, upText!.Foreground);
            var downText = ChartTestHelpers.FindTextBlock(window, "↓ 18.2%");
            Assert.NotNull(downText);
            Assert.Same(green, downText!.Foreground);

            var emptyText = ChartTestHelpers.FindTextBlock(window, "暂无数据");
            if (emptyText is not null)
            {
                Assert.False(ChartTestHelpers.IsEffectivelyVisible(emptyText), "有数据时趋势/环形不应呈空态");
            }

            // 系列色出图（趋势输入柱 / 环形段共用 AppSeries0）
            Assert.True(ChartTestHelpers.FrameContainsColor(frame, Color.Parse("#4c8dff")),
                $"{variant} 总览帧缺少系列色 #4c8dff");
            ChartTestHelpers.SaveArchive(frame, ArchiveDir, file);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Overview_view_shows_honest_empty_state_without_data()
    {
        var vm = CreateVm(new StubQueryService { Kpi = new UsageKpiResult { Cost = AggregatedCost.Empty } });
        var window = CreateOverviewWindow(vm, ThemeVariant.Dark);
        var frame = ChartTestHelpers.RenderFrame(window);

        // 空态留档（同有数据用例）：首启扫描完成前 / 库内无数据的真实画面，用于目检
        // 「坐标轴不露出 -10..10 默认坐标系、环形不重复"暂无数据"」这两条空态契约
        ChartTestHelpers.SaveArchive(frame, ArchiveDir, "overview-empty-dark.png");

        // 全补零窗口：趋势图空态可见；KPI 如实 0 / "—"；环形空态（DonutChart 内建）
        var emptyText = ChartTestHelpers.FindTextBlock(window, "暂无数据");
        Assert.NotNull(emptyText);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(emptyText!), "无数据窗口应呈诚实空态");
        Assert.True(ChartTestHelpers.FindTextBlock(window, "0") is not null);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 真实查询服务冒烟（环境无关：同口径直查对账）──

    [AvaloniaFact]
    public void Real_query_service_smoke_binds_same_values_as_direct_kpi_call()
    {
        var query = AppServices.Get<IUsageQueryService>();
        var vm = new OverviewViewModel(ShellPages.For(PageId.Overview), new FilterStateService(), query);
        // 数据面查询已移到线程池执行（UsageQueryService 线程模型），VM 续体回 UI 队列：泵至加载完成
        DispatcherPump.PumpUntil(() => !ReferenceEquals(vm.TokensCard, KpiCardViewModel.Pending));

        // 同口径直查（默认筛选 = 最近 7 天 · 所有 Agent）
        var to = DateOnly.FromDateTime(DateTime.Today);
        var filter = new UsageQueryFilter
        {
            FromDay = UsageQueryFilter.FormatDay(to.AddDays(-6)),
            ToDay = UsageQueryFilter.FormatDay(to),
        };
        var expected = Task.Run(() => query.GetKpiAsync(filter)).GetAwaiter().GetResult();

        // TC-001（页面层）：卡片显示值 == 直查值经同一格式化口径的产物
        Assert.Equal(TrendChart.FormatCompactTokens(expected.TotalTokens), vm.TokensCard.Value);
        Assert.Equal(
            expected.Cost.KnownCostUsd is { } known
                ? TrendChart.FormatCostUsd(known, expected.Cost.HasUnknownCost)
                : expected.Cost.HasUnknownCost ? "未知" : "—",
            vm.CostCard.Value);
        Assert.Equal(expected.SessionCount.ToString("N0", CultureInfo.InvariantCulture), vm.SessionsCard.Value);
        Assert.Equal(
            expected.CacheReadShare is { } share
                ? (share * 100m).ToString("F1", CultureInfo.InvariantCulture) + "%"
                : "—",
            vm.CacheCard.Value);
        Assert.Equal(TrendChart.FormatCompactTokens(expected.TotalTokens), vm.AgentDonutCenterTitle);

        // 真实数据整页可渲染（不崩；数据有无随本机 facts 而定）
        var window = CreateOverviewWindow(vm, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
