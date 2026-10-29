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
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.App.Headless.Tests.Controls;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// D-007 总览页下半的自动化验证（headless，真实 App.axaml + Semi；stub 模式同 OverviewTests）：
/// 1. 模型分布表绑定 stub 值（Top6 顺序 / token 千分位 / 占比 / mini bar 值 = 占比 × 100 / 无「其他」行 / 零 token 行不入榜）；
/// 2. 项目排行表绑定（归一化项目名含歧义守卫回退原始路径 / 排名）；
/// 3. Token 类型构成环形三段映射（输入(未缓存) = InputTokens 契约语义，缓存相关 = cacheRead + cacheCreation，
///    段和 = Σ TotalTokens；固定三色 = AppTokenType* 令牌）；
/// 4. 成本趋势与上半 Token 趋势共用同一次 GetDailySeriesAsync（单查询同源）；
/// 5. 「查看详情 ↗」经构造注入的 Action&lt;PageId&gt; 回调跳模型/项目页（含无回调 no-op）；
/// 6. 序号防护覆盖本卡新增查询（模型维度挂起的旧响应不覆盖新选择）；
/// 7. 明暗两套离屏截图留档（docs/design/overview-bottom-*.png）；
/// 8. 真实查询服务冒烟（同口径直查对账，环境无关）。
/// </summary>
public class OverviewBottomTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_OVERVIEW_BOTTOM_PREVIEW_DIR");

    /// <summary>fixture 占比基数（stub 手记 TokenShare 的分母，仅测试内部对账用）。</summary>
    private const long ScopeTotal = 10_000_000;

    // ── stub 查询服务（可配置模型/项目维度 + 模型响应挂起 + 调用记账）──

    private sealed class StubQueryService : IUsageQueryService
    {
        public UsageKpiResult Kpi { get; set; } = new();

        public IReadOnlyList<DailyUsagePoint> Daily { get; set; } = Array.Empty<DailyUsagePoint>();

        public IReadOnlyList<DimensionUsageRow> Agents { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Models { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Projects { get; set; } = Array.Empty<DimensionUsageRow>();

        public int KpiCalls { get; private set; }

        public int DailyCalls { get; private set; }

        public int ModelCalls { get; private set; }

        public int ProjectCalls { get; private set; }

        private TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>? _heldModels;
        private IReadOnlyList<DimensionUsageRow>? _heldModelsResult;

        /// <summary>让下一次 GetModelBreakdownAsync 挂起，ReleaseHeldModels 时以指定结果放行（序号防护用例）。</summary>
        public void HoldNextModels(IReadOnlyList<DimensionUsageRow> resultOnRelease)
        {
            _heldModelsResult = resultOnRelease;
            _heldModels = new TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldModels() => _heldModels?.TrySetResult(_heldModelsResult ?? Models);

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            KpiCalls++;
            return Task.FromResult(Kpi);
        }

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            DailyCalls++;
            return Task.FromResult(Daily);
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Agents);

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            ModelCalls++;
            if (_heldModels is { } held)
            {
                _heldModels = null;
                return held.Task;
            }

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
    };

    private static DimensionUsageRow ModelRow(string name, long tokens, decimal? tokenShare) => new()
    {
        Key = name,
        SessionCount = 10,
        TotalTokens = tokens,
        Cost = new AggregatedCost(2.5m, false),
        TokenShare = tokenShare,
    };

    /// <summary>
    /// 模型维度 fixture：故意乱序 + 含零 token 行 + 8 行（验证防御性排序、Top6 截断与零行排除）。
    /// share 手记 = tokens / 10,000,000（与 I-029 "本行 / 范围全部行之和" 同口径）。
    /// </summary>
    private static IReadOnlyList<DimensionUsageRow> FixtureModels { get; } = new[]
    {
        ModelRow("gpt-4o-mini", 621_307, 0.0621307m),
        ModelRow("claude-sonnet-4-5", 3_215_482, 0.3215482m),
        ModelRow("gpt-4o", 1_842_317, 0.1842317m),
        ModelRow("gemini-1.5-pro", 1_126_473, 0.1126473m),
        ModelRow("claude-3-5-haiku", 0, null),
        ModelRow("claude-3-5-haiku", 842_618, 0.0842618m),
        ModelRow("o3-mini", 421_531, 0.0421531m),
        ModelRow("deepseek-v3", 315_977, 0.0315977m),
    };

    private static DimensionUsageRow ProjectRow(string path, string? displayName, long tokens, decimal? tokenShare) => new()
    {
        Key = path,
        DisplayName = displayName,
        SessionCount = 10,
        TotalTokens = tokens,
        Cost = new AggregatedCost(2.5m, false),
        TokenShare = tokenShare,
    };

    /// <summary>项目维度 fixture：第三行模拟归一化歧义守卫（DisplayName 退回原始路径）。</summary>
    private static IReadOnlyList<DimensionUsageRow> FixtureProjects { get; } = new[]
    {
        ProjectRow("/Users/dev/project-a", "Project-A", 2_341_628, 0.2341628m),
        ProjectRow("/Users/dev/ai-project", "AI-Project", 1_682_317, 0.1682317m),
        ProjectRow("/data/work/project-a", "/data/work/project-a", 987_421, 0.0987421m),
    };

    /// <summary>Agent 维度 fixture（截图留档用，让上半区 Agent 环形同样有数据）。</summary>
    private static IReadOnlyList<DimensionUsageRow> FixtureAgents { get; } = new[]
    {
        ModelRow("Claude Code", 4_120_000, 0.4713959m),
        ModelRow("Codex", 2_260_000, 0.2585812m),
        ModelRow("Cursor", 1_580_000, 0.1807780m),
        ModelRow("GitHub Copilot", 780_000, 0.0892449m),
    };

    /// <summary>带缓存列的日序列 fixture（三段映射用）：两天，input 1000 / output 400 / cache 150。</summary>
    private static IReadOnlyList<DailyUsagePoint> TypeDaily() => new[]
    {
        new DailyUsagePoint
        {
            Day = "2026-10-01",
            InputTokens = 700,
            OutputTokens = 250,
            CacheCreationTokens = 20,
            CacheReadTokens = 30,
            TotalTokens = 1000,
            Cost = new AggregatedCost(1m, false),
            HasData = true,
        },
        new DailyUsagePoint
        {
            Day = "2026-10-02",
            InputTokens = 300,
            OutputTokens = 150,
            CacheCreationTokens = 40,
            CacheReadTokens = 60,
            TotalTokens = 550,
            Cost = AggregatedCost.Empty,
            HasData = true,
        },
    };

    /// <summary>
    /// 30 天日序列 fixture（渲染用）：形态同 ChartTestHelpers.DailyPoints30，但补上缓存列
    /// （cacheRead = input/2、cacheCreation = input/4）——类型构成环形的缓存段才有可见弧，
    /// 截图与 #38d6a4 出图断言都依赖它；total 同步含缓存三段自洽。
    /// </summary>
    private static IReadOnlyList<DailyUsagePoint> DailyWithCache30()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var realOffsets = new HashSet<int> { 2, 5, 6, 11, 17, 23, 29 };
        var points = new List<DailyUsagePoint>();
        for (var i = 0; i < 30; i++)
        {
            var real = realOffsets.Contains(i);
            var input = real ? 320_000L + i * 41_000 : 0;
            var output = real ? 120_000L + i * 17_000 : 0;
            var cacheRead = real ? input / 2 : 0;
            var cacheCreation = real ? input / 4 : 0;
            points.Add(new DailyUsagePoint
            {
                Day = UsageQueryFilter.FormatDay(today.AddDays(-(29 - i))),
                InputTokens = input,
                OutputTokens = output,
                CacheCreationTokens = cacheCreation,
                CacheReadTokens = cacheRead,
                TotalTokens = input + output + cacheCreation + cacheRead,
                Cost = real
                    ? new AggregatedCost(1.20m + i * 0.31m, hasUnknownCost: i == 29)
                    : AggregatedCost.Empty,
                HasData = real,
            });
        }

        return points;
    }

    private static OverviewViewModel CreateVm(
        StubQueryService stub, FilterStateService? filters = null, Action<PageId>? navigateTo = null) =>
        new(ShellPages.For(PageId.Overview), filters ?? new FilterStateService(), stub, navigateToPage: navigateTo);

    // ── 模型分布表（TC-001 页面层：stub 值 → 行显示值）──

    [AvaloniaFact]
    public void Model_rows_bind_top6_order_tokens_share_bar_and_no_other_row()
    {
        var vm = CreateVm(new StubQueryService
        {
            Kpi = FixtureKpi,
            Daily = ChartTestHelpers.DailyPoints30(),
            Models = FixtureModels,
        });

        var rows = vm.ModelRows!;
        Assert.True(vm.HasModelRows);

        // TopN=6：乱序输入按 token 降序、零 token 行与第 7 行以后不入榜、无「其他」聚合行
        Assert.Equal(OverviewViewModel.TopN, rows.Count);
        Assert.Equal(
            new[] { "claude-sonnet-4-5", "gpt-4o", "gemini-1.5-pro", "claude-3-5-haiku", "gpt-4o-mini", "o3-mini" },
            rows.Select(r => r.Name).ToArray());
        Assert.DoesNotContain("其他", rows.Select(r => r.Name));

        // token 紧凑格式（与模型/项目统计页同口径）+ 占比（一位小数）+ mini bar 值 = 占比 × 100（宽度即占比，任务卡定案）
        Assert.Equal("321.5万", rows[0].TokensText);
        Assert.Equal("32.2%", rows[0].ShareText);
        Assert.Equal(32.15482, rows[0].BarValue, 5);
        Assert.Equal("4.2%", rows[^1].ShareText);
        Assert.Equal(4.21531, rows[^1].BarValue, 5);

        // 行首圆点 = 行序系列色（App 资源在 headless 宿主内可用）
        Assert.NotNull(rows[0].DotBrush);
    }

    [AvaloniaFact]
    public void Model_rows_without_share_basis_show_dash_and_zero_bar()
    {
        // 分母为 0（范围总 token 为 0）时 TokenShare 为 null：占比 "—"、bar 0，不伪造 0%
        var vm = CreateVm(new StubQueryService
        {
            Kpi = new UsageKpiResult(),
            Models = new[] { ModelRow("gpt-4o", 1_500, null) },
        });

        var row = Assert.Single(vm.ModelRows!);
        Assert.True(vm.HasModelRows); // 有行：非空态
        Assert.Equal("—", row.ShareText);
        Assert.Equal(0, row.BarValue);
        Assert.Equal("1,500", row.TokensText);
    }

    // ── 项目排行表 ──

    [AvaloniaFact]
    public void Project_rows_use_normalized_name_rank_and_bar()
    {
        var vm = CreateVm(new StubQueryService
        {
            Kpi = FixtureKpi,
            Daily = ChartTestHelpers.DailyPoints30(),
            Projects = FixtureProjects,
        });

        var rows = vm.ProjectRows!;
        Assert.True(vm.HasProjectRows);

        // 归一化项目名；歧义守卫行（同名展示名）按 I-029 契约退回原始路径
        Assert.Equal(new[] { "Project-A", "AI-Project", "/data/work/project-a" }, rows.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.Rank).ToArray());
        Assert.Equal("234.2万", rows[0].TokensText);
        Assert.Equal("23.4%", rows[0].ShareText);
        Assert.Equal(23.41628, rows[0].BarValue, 5);
    }

    // ── Token 类型构成环形（三段映射定案）──

    [AvaloniaFact]
    public void Token_type_segments_map_uncached_input_output_and_cache()
    {
        var stub = new StubQueryService { Kpi = FixtureKpi, Daily = TypeDaily() };
        var vm = CreateVm(stub);

        var segments = vm.TypeSegments!;
        // 三段：输入(未缓存) / 输出 / 缓存相关 —— uncached input 不丢、cacheRead+cacheCreation 合并
        Assert.Equal(3, segments.Count);
        Assert.Equal(OverviewViewModel.TokenTypeInputLabel, segments[0].Name);
        Assert.Equal(1000, segments[0].Value); // 700 + 300
        Assert.Equal(OverviewViewModel.TokenTypeOutputLabel, segments[1].Name);
        Assert.Equal(400, segments[1].Value); // 250 + 150
        Assert.Equal(OverviewViewModel.TokenTypeCacheLabel, segments[2].Name);
        Assert.Equal(150, segments[2].Value); // (20+30) + (40+60)

        // 段和 = Σ TotalTokens（日序列同源自洽；环心 = KPI 总 Token 与上半区同源）
        Assert.Equal(TypeDaily().Sum(p => p.TotalTokens), segments.Sum(s => (long)s.Value));
        Assert.Equal(TrendChart.FormatCompactTokens(FixtureKpi.TotalTokens), vm.TypeDonutCenterTitle);

        // 固定三色 = 原型 typeDonut 三元组（AppTokenType* 令牌；缺资源时回退同值原色）
        Assert.Equal(Color.Parse("#4c8dff"), segments[0].Color);
        Assert.Equal(Color.Parse("#a779ff"), segments[1].Color);
        Assert.Equal(Color.Parse("#38d6a4"), segments[2].Color);
    }

    [AvaloniaFact]
    public void Token_type_segments_all_zero_are_honest_empty()
    {
        // 全补零窗口（token 全 0）→ 空段集，DonutChart 呈内建空态，不造假段
        var vm = CreateVm(new StubQueryService
        {
            Kpi = new UsageKpiResult(),
            Daily = ChartTestHelpers.AllPadded30(),
        });

        Assert.Empty(vm.TypeSegments!);
        Assert.Equal("0", vm.TypeDonutCenterTitle);
    }

    // ── 成本趋势（同源日序列，单查询）──

    [AvaloniaFact]
    public void Cost_trend_reuses_daily_series_without_second_query()
    {
        var stub = new StubQueryService
        {
            Kpi = FixtureKpi,
            Daily = ChartTestHelpers.DailyPoints30(),
            Agents = Array.Empty<DimensionUsageRow>(),
        };
        var vm = CreateVm(stub);

        // 一次装载只发一次日序列查询：上半 Token 趋势与下半成本趋势（Mode=CostUsd）吃同一份点
        Assert.Equal(1, stub.DailyCalls);
        Assert.Same(stub.Daily, vm.DailyPoints);
        Assert.True(ChartTestHelpers.DailyPoints30().Any(p => p.Cost.KnownCostUsd is { }),
            "fixture 应含已知成本日，成本趋势有可画数据");
    }

    // ── 「查看详情 ↗」导航回调 ──

    [AvaloniaFact]
    public void Details_links_invoke_injected_navigation_callback()
    {
        var navigated = new List<PageId>();
        var vm = CreateVm(new StubQueryService { Kpi = FixtureKpi }, navigateTo: navigated.Add);

        vm.OpenModelsCommand.Execute(null);
        vm.OpenProjectsCommand.Execute(null);

        Assert.Equal(new[] { PageId.Models, PageId.Projects }, navigated);
    }

    [AvaloniaFact]
    public void Details_links_noop_without_callback()
    {
        // 无外壳环境（预览/纯单测）回调为 null：命令可执行但不抛（no-op）
        var vm = CreateVm(new StubQueryService { Kpi = FixtureKpi });

        vm.OpenModelsCommand.Execute(null);
        vm.OpenProjectsCommand.Execute(null);
    }

    [AvaloniaFact]
    public void Details_links_navigate_shell_to_models_and_projects_pages()
    {
        // 端到端：外壳 CreatePage 注入真实 GoToPage 回调，「查看详情 ↗」确实到达对应页
        var shell = new MainWindowViewModel(
            new FilterStateService(),
            AppServices.Get<SettingsViewModel>(),
            AppServices.Get<DataPricingViewModel>(),
            AppServices.Get<IUsageQueryService>(),
            AppServices.Get<AgentCatalogService>(),
            AppServices.Get<IToastService>(),
            AppServices.Get<Core.Services.PricingEngine>());

        var overview = Assert.IsType<OverviewViewModel>(shell.CurrentPage);
        overview.OpenModelsCommand.Execute(null);
        var models = Assert.IsType<ModelsViewModel>(shell.CurrentPage); // 模型页 D-010 起为真实页
        Assert.Equal(PageId.Models, models.Page.Id);

        // 回总览（页面 VM 每次导航新建，新实例的回调仍接同一外壳）再跳项目页
        shell.GoToPageCommand.Execute(PageId.Overview);
        var overviewAgain = Assert.IsType<OverviewViewModel>(shell.CurrentPage);
        overviewAgain.OpenProjectsCommand.Execute(null);
        var projects = Assert.IsType<ProjectsViewModel>(shell.CurrentPage); // 项目页 D-011 起为真实页
        Assert.Equal(PageId.Projects, projects.Page.Id);
    }

    // ── 序号防护覆盖本卡新增查询 ──

    [AvaloniaFact]
    public void Rapid_filter_switch_discards_stale_model_response()
    {
        var filters = new FilterStateService();
        var stale = new[] { ModelRow("stale-model", 9_999_999, 0.9999m) };
        var stub = new StubQueryService
        {
            Kpi = FixtureKpi,
            Daily = ChartTestHelpers.DailyPoints30(),
            Models = FixtureModels,
            Projects = FixtureProjects,
        };
        var vm = CreateVm(stub, filters);

        // 第 2 次查询（切 30 天）在模型维度挂起；第 3 次（切 90 天）同步走完
        stub.HoldNextModels(stale);
        filters.TimeRange = TimeRangeOption.Last30Days;
        filters.TimeRange = TimeRangeOption.Last90Days;
        Assert.Equal("claude-sonnet-4-5", vm.ModelRows![0].Name);

        // 放行旧响应：序号防护丢弃，模型/项目行仍是新选择（90 天）的结果
        stub.ReleaseHeldModels();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("claude-sonnet-4-5", vm.ModelRows[0].Name);
        Assert.NotEqual("stale-model", vm.ModelRows[0].Name);
        Assert.Equal("Project-A", vm.ProjectRows![0].Name);
        // 挂起的那次只走到模型维度就被丢弃：3 次装载 → 模型查 3 次、项目只查 2 次
        Assert.Equal(3, stub.ModelCalls);
        Assert.Equal(2, stub.ProjectCalls);
    }

    // ── 视图渲染（stub 数据，明暗两套 + 截图留档）──

    private static Window CreateOverviewWindow(OverviewViewModel vm, ThemeVariant variant) => new()
    {
        Width = 1020,
        Height = 1160, // 整页渲染（D-006 上半 + D-007 下半），截图覆盖下半区
        Content = new OverviewView { DataContext = vm },
        RequestedThemeVariant = variant,
        Background = Application.Current!.TryGetResource("AppBackgroundBrush", variant, out var bg)
            && bg is IBrush brush ? brush : new SolidColorBrush(Color.Parse("#0f1118")),
    };

    [AvaloniaFact]
    public void Overview_bottom_renders_stub_data_in_light_and_dark()
    {
        var vm = CreateVm(new StubQueryService
        {
            Kpi = FixtureKpi,
            Daily = DailyWithCache30(),
            Agents = FixtureAgents,
            Models = FixtureModels,
            Projects = FixtureProjects,
        });

        foreach (var (variant, file) in new (ThemeVariant, string)[]
                 {
                     (ThemeVariant.Light, "overview-bottom-light.png"),
                     (ThemeVariant.Dark, "overview-bottom-dark.png"),
                 })
        {
            var window = CreateOverviewWindow(vm, variant);
            var frame = ChartTestHelpers.RenderFrame(window);

            // 模型表：Top1 行名 / 占比 / 表头；项目表：归一化名与守卫回退名
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "claude-sonnet-4-5"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "32.2%"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "Project-A"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, "/data/work/project-a"));
            // 跳转入口 + 类型构成图例（三段口径文案）
            Assert.Equal(2, window.GetVisualDescendants().OfType<Button>()
                .Count(b => (b.Content as string) == "查看详情 ↗"));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, OverviewViewModel.TokenTypeInputLabel));
            Assert.NotNull(ChartTestHelpers.FindTextBlock(window, OverviewViewModel.TokenTypeCacheLabel));

            // 有数据时不应出现空态文案（表 / 环 / 图）
            var emptyText = ChartTestHelpers.FindTextBlock(window, "暂无数据");
            if (emptyText is not null)
            {
                Assert.False(ChartTestHelpers.IsEffectivelyVisible(emptyText), "有数据时预览表不应呈空态");
            }

            // 类型构成缓存段固定色 #38d6a4 出图（区分于系列色，验证固定三色接线）
            Assert.True(ChartTestHelpers.FrameContainsColor(frame, Color.Parse("#38d6a4")),
                $"{variant} 总览帧缺少类型构成缓存段色 #38d6a4");
            ChartTestHelpers.SaveArchive(frame, ArchiveDir, file);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Overview_bottom_shows_honest_empty_state_without_data()
    {
        var vm = CreateVm(new StubQueryService { Kpi = new UsageKpiResult { Cost = AggregatedCost.Empty } });
        Assert.False(vm.HasModelRows);
        Assert.False(vm.HasProjectRows);
        Assert.Empty(vm.TypeSegments!);

        var window = CreateOverviewWindow(vm, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);

        // 两张预览表诚实空态（原型 .empty 文案），不造假行
        var emptyText = ChartTestHelpers.FindTextBlock(window, "暂无数据");
        Assert.NotNull(emptyText);
        Assert.True(ChartTestHelpers.IsEffectivelyVisible(emptyText!), "无数据窗口应呈诚实空态");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 真实查询服务冒烟（环境无关：同口径直查对账）──

    [AvaloniaFact]
    public void Real_query_service_smoke_binds_breakdowns_same_as_direct_call()
    {
        var query = AppServices.Get<IUsageQueryService>();
        var vm = new OverviewViewModel(ShellPages.For(PageId.Overview), new FilterStateService(), query);
        // 数据面查询已移到线程池执行（UsageQueryService 线程模型），VM 续体回 UI 队列：泵至加载完成
        DispatcherPump.PumpUntil(() => vm.ModelRows is not null && vm.ProjectRows is not null);

        // 同口径直查（默认筛选 = 最近 7 天 · 所有 Agent）
        var to = DateOnly.FromDateTime(DateTime.Today);
        var filter = new UsageQueryFilter
        {
            FromDay = UsageQueryFilter.FormatDay(to.AddDays(-6)),
            ToDay = UsageQueryFilter.FormatDay(to),
        };
        var expectedModels = Task.Run(() => query.GetModelBreakdownAsync(filter)).GetAwaiter().GetResult();
        var expectedProjects = Task.Run(() => query.GetProjectBreakdownAsync(filter)).GetAwaiter().GetResult();
        var expectedDaily = Task.Run(() => query.GetDailySeriesAsync(filter)).GetAwaiter().GetResult();

        // TC-001（页面层）：预览行 == 直查行经同一映射口径（排序 / TopN / 归一化名 / 紧凑格式 / 占比）的产物
        var expectedModelRows = expectedModels
            .Where(r => r.TotalTokens > 0)
            .OrderByDescending(r => r.TotalTokens)
            .Take(OverviewViewModel.TopN)
            .ToList();
        Assert.Equal(
            expectedModelRows.Select(r => r.DisplayName ?? r.Key),
            vm.ModelRows?.Select(r => r.Name) ?? Enumerable.Empty<string>());
        Assert.Equal(
            expectedModelRows.Select(r => TrendChart.FormatCompactTokens(r.TotalTokens)),
            vm.ModelRows?.Select(r => r.TokensText) ?? Enumerable.Empty<string>());

        var expectedProjectRows = expectedProjects
            .Where(r => r.TotalTokens > 0)
            .OrderByDescending(r => r.TotalTokens)
            .Take(OverviewViewModel.TopN)
            .ToList();
        Assert.Equal(
            expectedProjectRows.Select(r => r.DisplayName ?? r.Key),
            vm.ProjectRows?.Select(r => r.Name) ?? Enumerable.Empty<string>());

        // 类型构成三段与日序列同源自洽：段和 = Σ TotalTokens
        Assert.Equal(
            expectedDaily.Sum(p => p.TotalTokens),
            vm.TypeSegments?.Sum(s => (long)s.Value) ?? 0);

        // 真实数据整页可渲染（不崩；数据有无随本机 facts 而定）
        var window = CreateOverviewWindow(vm, ThemeVariant.Dark);
        ChartTestHelpers.RenderFrame(window);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
