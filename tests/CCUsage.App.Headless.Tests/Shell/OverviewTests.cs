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
using CCUsage.App.Controls;
using CCUsage.App.DependencyInjection;
using CCUsage.App.Services;
using CCUsage.App.ViewModels;
using CCUsage.App.Views;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.App.Headless.Tests.Controls;
using Xunit;

namespace CCUsage.App.Headless.Tests.Shell;

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
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("CCUSAGE_OVERVIEW_PREVIEW_DIR");

    // ── stub 查询服务（可配置 KPI/日序列/Agent 维度 + 调用记账 + KPI 响应挂起）──

    private sealed class StubQueryService : IUsageQueryService
    {
        public UsageKpiResult Kpi { get; set; } = new();

        public IReadOnlyList<DailyUsagePoint> Daily { get; set; } = Array.Empty<DailyUsagePoint>();

        public IReadOnlyList<DimensionUsageRow> Agents { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Models { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Projects { get; set; } = Array.Empty<DimensionUsageRow>();

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

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult { Hours = Array.Empty<HourlyUsageBucket>() });
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

        // 总 Token：紧凑格式（原型 "8.74M"）+ ↑ 绿方向
        Assert.Equal("8.74M", vm.TokensCard.Value);
        Assert.Equal("↑ 23.6%", vm.TokensCard.ChangeText);
        Assert.True(vm.TokensCard.IsUp);
        Assert.False(vm.TokensCard.IsDown);
        Assert.False(vm.TokensCard.IsFlat);

        // 估算成本：已知成本两位小数；↓ 红
        Assert.Equal("$286.41", vm.CostCard.Value);
        Assert.Equal("↓ 18.2%", vm.CostCard.ChangeText);
        Assert.True(vm.CostCard.IsDown);

        // 会话数：千分位
        Assert.Equal("412", vm.SessionsCard.Value);
        Assert.Equal("↑ 12.8%", vm.SessionsCard.ChangeText);
        Assert.True(vm.SessionsCard.IsUp);

        // 缓存读取占比：0..1 → 一位小数百分数；上一周期为 0 → 箭头有方向、数字留 "—"（不伪造）
        Assert.Equal("67.3%", vm.CacheCard.Value);
        Assert.Equal("↑ —", vm.CacheCard.ChangeText);
        Assert.True(vm.CacheCard.IsUp);

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
        Assert.True(vm.TokensCard.IsFlat);
        Assert.Null(vm.LoadError);
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
        filters.TimeRange = TimeRangeOption.Options[1];
        Assert.Equal(2, stub.KpiCalls);
        Assert.Equal(UsageQueryFilter.FormatDay(today.AddDays(-29)), stub.KpiFilters[^1].FromDay);
        Assert.Equal(UsageQueryFilter.FormatDay(today), stub.KpiFilters[^1].ToDay);
        Assert.Null(stub.KpiFilters[^1].Agent);

        // Agent 精确过滤直传选项 Id
        filters.Agent = AgentOption.Options[2]; // Codex
        Assert.Equal(3, stub.KpiCalls);
        Assert.Equal("Codex", stub.KpiFilters[^1].Agent);

        // 全部时间：双端无界 → 无上一周期，环比呈平箭头 "→ —"（不臆造对比）
        filters.TimeRange = TimeRangeOption.Options[3];
        Assert.Equal(4, stub.KpiCalls);
        Assert.Null(stub.KpiFilters[^1].FromDay);
        Assert.Null(stub.KpiFilters[^1].ToDay);
        Assert.Equal("Codex", stub.KpiFilters[^1].Agent); // Agent 维度保留
        Assert.True(vm.TokensCard.IsFlat);
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
        filters.TimeRange = TimeRangeOption.Options[1]; // 30 天 → 挂起
        filters.TimeRange = TimeRangeOption.Options[2]; // 90 天 → 完成
        Assert.Equal("9.5M", vm.TokensCard.Value);

        // 放行旧响应：序号防护使其被丢弃，UI 仍是新选择（90 天）的结果
        stub.ReleaseHeldKpi();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("9.5M", vm.TokensCard.Value);
        Assert.NotEqual("3.1M", vm.TokensCard.Value);
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
        Assert.Equal("8.74M", vm.AgentDonutCenterTitle);
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
        filters.TimeRange = TimeRangeOption.Options[1];
        filters.Agent = AgentOption.Options[1];

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
            && bg is IBrush brush ? brush : new SolidColorBrush(Color.Parse("#0b1120")),
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
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "8.74M"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "$286.41"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "412"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "67.3%"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "Claude Code")); // 环形图例行

            // 环比箭头颜色经 Classes.kpi-change-* 类绑定切换（↑ 绿 / ↓ 红，回归防线）
            Assert.True(Application.Current!.TryGetResource("AppAccentGreenBrush", variant, out var green));
            Assert.True(Application.Current!.TryGetResource("AppAccentRedBrush", variant, out var red));
            var upText = ChartTestHelpers.FindTextBlock(window, "↑ 23.6%");
            Assert.NotNull(upText);
            Assert.Same(green, upText!.Foreground);
            var downText = ChartTestHelpers.FindTextBlock(window, "↓ 18.2%");
            Assert.NotNull(downText);
            Assert.Same(red, downText!.Foreground);

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
        ChartTestHelpers.RenderFrame(window);

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
        Dispatcher.UIThread.RunJobs();

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
