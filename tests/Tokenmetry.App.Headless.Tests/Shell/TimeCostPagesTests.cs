using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// D-012 时间分析页 / D-013 成本分析页的自动化验证（headless，真实 App.axaml + Semi，stub 模式同
/// DimensionPagesTests/OverviewTests——stub 只承担数据回传；I-029 小时分布的 unknown 排除口径与
/// I-028 补零语义的聚合对账在 UsageQueryServiceTests / UsageKpiTests）：
/// 1. D-012：小时分布接 TrendChart HourlyTokens + unknown 占比标注（TC-001：无分钟级时间戳的源不入桶、
///    页面可见「N 个会话缺少精确时间，未计入小时分布（占 X%）」，占比分母 = 入桶 + unknown 会话数）；
///    热力图接日序列 Token 强度（补零日淡格保留、悬停数值）；观察项表三行内容与真实数据能力一致；
/// 2. D-013：KPI 四卡绑定（估算总成本四态 / 输入输出占比 Token 构成口径显式标注 / 待确认价格接
///    PricingEngine.GetUnpricedModels——TC-001：3 个缺价模型页面 KPI 与引擎一致、估算不含其成本）；
///    口径说明表三行与 docs/reconciliation 口径对齐；
/// 3. 两页随全局筛选联动（映射窗口对账）、序号防护、Dispose 退订（VM 级 + 外壳导航离开端到端）；
/// 4. 真实查询服务冒烟 + 明暗两套离屏截图留档（docs/design/time-*.png / cost-*.png）。
/// </summary>
public class TimeCostPagesTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_TIMECOST_PREVIEW_DIR");

    // ── stub 查询服务（小时分布 + 日序列 + KPI 可配置 + 调用记账 + 挂起，模式同 DimensionPagesTests）──

    private sealed class StubQueryService : IUsageQueryService
    {
        public UsageKpiResult Kpi { get; set; } = new();

        public IReadOnlyList<DailyUsagePoint> Daily { get; set; } = Array.Empty<DailyUsagePoint>();

        public HourlyDistributionResult Hourly { get; set; } = new() { Hours = Array.Empty<HourlyUsageBucket>() };

        public bool ThrowOnKpi { get; set; }

        public bool ThrowOnHourly { get; set; }

        public bool ThrowOnDaily { get; set; }

        public List<UsageQueryFilter> KpiQueries { get; } = new();

        public List<UsageQueryFilter> DailyQueries { get; } = new();

        public List<UsageQueryFilter> HourlyQueries { get; } = new();

        private TaskCompletionSource<UsageKpiResult>? _heldKpi;
        private UsageKpiResult? _heldKpiResult;

        private TaskCompletionSource<HourlyDistributionResult>? _heldHourly;
        private HourlyDistributionResult? _heldHourlyResult;

        /// <summary>让下一次 KPI 查询挂起，Release 时以指定结果放行（序号防护用例）。</summary>
        public void HoldNextKpi(UsageKpiResult resultOnRelease)
        {
            _heldKpiResult = resultOnRelease;
            _heldKpi = new TaskCompletionSource<UsageKpiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldKpi() => _heldKpi?.TrySetResult(_heldKpiResult ?? Kpi);

        public void HoldNextHourly(HourlyDistributionResult resultOnRelease)
        {
            _heldHourlyResult = resultOnRelease;
            _heldHourly = new TaskCompletionSource<HourlyDistributionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldHourly() => _heldHourly?.TrySetResult(_heldHourlyResult ?? Hourly);

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            KpiQueries.Add(filter);
            if (ThrowOnKpi)
            {
                throw new InvalidOperationException("kpi offline");
            }

            if (_heldKpi is { } held)
            {
                _heldKpi = null;
                return held.Task;
            }

            return Task.FromResult(Kpi);
        }

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            DailyQueries.Add(filter);
            if (ThrowOnDaily)
            {
                throw new InvalidOperationException("daily offline");
            }

            return Task.FromResult(Daily);
        }

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            HourlyQueries.Add(filter);
            if (ThrowOnHourly)
            {
                throw new InvalidOperationException("hourly offline");
            }

            if (_heldHourly is { } held)
            {
                _heldHourly = null;
                return held.Task;
            }

            return Task.FromResult(Hourly);
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            Task.FromResult(new SessionListResult { Rows = [], TotalCount = 0 });
    }

    // ── fixture ──

    /// <summary>
    /// TC-001（D-012）fixture：10 个会话中 2 个来自无分钟级时间戳的源（纪元兜底 / 本地日锚定，
    /// 构造方式同 I-029 UsageQueryServiceTests 的 s7/s8——排除逻辑本身在服务层测试钉死）。
    /// stub 按 I-029 契约回传：8 个精确会话入桶（9 点 5 个 + 14 点 3 个），2 个 unknown 不出现在
    /// 任何桶，以 UnknownSessionCount/UnknownTotalTokens 可见。
    /// </summary>
    private static HourlyDistributionResult FixtureHourlyWithUnknown() => new()
    {
        Hours = Enumerable.Range(0, 24).Select(hour => new HourlyUsageBucket
        {
            Hour = hour,
            SessionCount = hour == 9 ? 5 : hour == 14 ? 3 : 0,
            TotalTokens = hour == 9 ? 50_000 : hour == 14 ? 30_000 : 0,
        }).ToList(),
        UnknownSessionCount = 2,
        UnknownTotalTokens = 1_200,
        TimeZoneId = TimeZoneInfo.Local.Id,
    };

    /// <summary>日序列 fixture（双端有界窗口语义：1 个补零日 HasData=false 夹在两个数据日之间）。</summary>
    private static IReadOnlyList<DailyUsagePoint> FixtureDaily() => new[]
    {
        new DailyUsagePoint
        {
            Day = "2026-03-10", InputTokens = 3100, OutputTokens = 1550,
            CacheCreationTokens = 100, CacheReadTokens = 2000, TotalTokens = 6750,
            Cost = new AggregatedCost(3.50m, true), HasData = true,
        },
        new DailyUsagePoint { Day = "2026-03-11", HasData = false }, // 补零空日
        new DailyUsagePoint
        {
            Day = "2026-03-12", InputTokens = 600, OutputTokens = 400,
            CacheReadTokens = 200, TotalTokens = 1200,
            Cost = new AggregatedCost(0.42m, false), HasData = true,
        },
    };

    // ── hosts ──

    private sealed record TimeHost(TimeViewModel Vm, StubQueryService Stub, FilterStateService Filters);

    private static TimeHost CreateTimeHost(StubQueryService? stub = null, FilterStateService? filters = null)
    {
        stub ??= new StubQueryService { Hourly = FixtureHourlyWithUnknown(), Daily = FixtureDaily() };
        filters ??= new FilterStateService();
        var vm = new TimeViewModel(ShellPages.For(PageId.Time), filters, stub);
        Dispatcher.UIThread.RunJobs();
        return new TimeHost(vm, stub, filters);
    }

    private sealed record CostHost(CostViewModel Vm, StubQueryService Stub, FilterStateService Filters, PricingEngine Engine);

    private static CostHost CreateCostHost(
        StubQueryService? stub = null,
        FilterStateService? filters = null,
        PricingEngine? engine = null)
    {
        stub ??= new StubQueryService
        {
            Kpi = new UsageKpiResult { TotalTokens = 1_000_000, Cost = new AggregatedCost(142.20m, false) },
            Daily = FixtureDaily(),
        };
        filters ??= new FilterStateService();
        engine ??= new PricingEngine();
        var vm = new CostViewModel(ShellPages.For(PageId.Cost), filters, stub, engine);
        Dispatcher.UIThread.RunJobs();
        return new CostHost(vm, stub, filters, engine);
    }

    private static MainWindowViewModel CreateShell(StubQueryService stub, FilterStateService filters, PricingEngine engine)
    {
        var toast = new DimensionPagesStubToast();
        var catalog = new AgentCatalogService(stub);
        return new MainWindowViewModel(
            filters, AppServices.Get<SettingsViewModel>(), AppServices.Get<DataPricingViewModel>(), stub, catalog, toast, engine);
    }

    /// <summary>最小 toast stub（避免与 DimensionPagesTests 的私有嵌套类型耦合）。</summary>
    private sealed class DimensionPagesStubToast : IToastService
    {
        public void Show(string message, Visual? host = null)
        {
        }
    }

    /// <summary>渲染视图并返回全部 TextBlock 文本（页面可见性断言用）。</summary>
    private static List<string> RenderAndGetTexts(UserControl view)
    {
        var window = new Window { Width = 1180, Height = 860, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!)
            .ToList();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        return texts;
    }

    // ══════════════════════════ D-012 时间分析页 ══════════════════════════

    [AvaloniaFact]
    public void Hourly_distribution_binds_chart_and_unknown_annotation_is_visible()
    {
        // TC-001（D-012）：无分钟级时间戳的源不入桶（stub 按 I-029 契约回传，排除逻辑在
        // UsageQueryServiceTests.Hourly_distribution_excludes_imprecise_timestamps_and_counts_unknown 钉死），
        // 页面可见「未知」占比说明。
        var host = CreateTimeHost();
        var hourly = host.Stub.Hourly;

        Assert.Same(hourly, host.Vm.HourlyDistribution);
        Assert.True(host.Vm.HasUnknownNote);
        // 入桶 5 + 3 = 8，unknown 2 → 分母 10，占 20.0%；unknown 携带 1200 tokens 一并可见
        Assert.Equal(8, TimeViewModel.SumKnownSessions(hourly.Hours));
        Assert.DoesNotContain(hourly.Hours, h => h.SessionCount == 2); // unknown 会话不在任何桶
        Assert.Contains("2 个会话缺少精确时间", host.Vm.UnknownNoteText);
        Assert.Contains("占 20.0%", host.Vm.UnknownNoteText);
        Assert.Contains("1200 tokens 用量", host.Vm.UnknownNoteText);

        // 页面可见性：渲染后标注文本出现在视觉树
        var texts = RenderAndGetTexts(new TimeAnalysisView { DataContext = host.Vm });
        Assert.Contains(texts, t => t!.Contains("缺少精确时间") && t.Contains("占 20.0%"));
    }

    [AvaloniaFact]
    public void Unknown_annotation_hidden_when_no_imprecise_sessions()
    {
        var host = CreateTimeHost(new StubQueryService
        {
            Hourly = new HourlyDistributionResult
            {
                Hours = Enumerable.Range(0, 24).Select(h => new HourlyUsageBucket
                {
                    Hour = h,
                    SessionCount = h == 10 ? 4 : 0,
                    TotalTokens = h == 10 ? 900 : 0,
                }).ToList(),
                UnknownSessionCount = 0,
                UnknownTotalTokens = 0,
            },
        });

        Assert.False(host.Vm.HasUnknownNote);
        Assert.Null(host.Vm.UnknownNoteText); // 无 unknown → 无标注（不显示「0 个」占位）
        Assert.Equal(900, host.Vm.HourlyDistribution!.Hours.Single(b => b.SessionCount > 0).TotalTokens);
    }

    [AvaloniaFact]
    public void Heatmap_cells_carry_daily_token_strength_and_keep_zero_filled_days()
    {
        // 热力图选型：双端有界窗口的补零日以淡格保留（HasData=false，Heatmap 空态底色 + 「无数据」tooltip），
        // 不从序列中丢弃；数据日强度 = 当日 TotalTokens，悬停数值经 Heatmap.BuildCellTooltip。
        var host = CreateTimeHost();

        var cells = host.Vm.HeatmapCells!;
        Assert.Equal(3, cells.Count); // 补零日保留（不显示格/淡格的选型见 VM 类型说明）
        Assert.Equal("2026-03-10", cells[0].Label);
        Assert.Equal(6750, cells[0].Value);
        Assert.True(cells[0].HasData);
        Assert.False(cells[1].HasData); // 2026-03-11 补零日
        Assert.Equal(0, cells[1].Value);
        Assert.True(cells[2].HasData);
        Assert.Equal(1200, cells[2].Value);

        Assert.Equal("2026-03-10 · 6750 tok", Heatmap.BuildCellTooltip(cells[0])); // B-006 为 token 数改紧凑单位口径
        Assert.Equal("2026-03-11 · 无数据", Heatmap.BuildCellTooltip(cells[1]));
    }

    [AvaloniaFact]
    public void Observation_table_has_three_rows_matching_real_capabilities()
    {
        // 观察项表内容定稿断言：三行、标题对齐原型，描述与 facts 真实能力一致
        // （会话时长字段存在但部分来源为兜底时间戳 → 建议性文案如实标注；unknown 边界与页面标注互相印证）。
        var rows = TimeViewModel.CreateObservations();

        Assert.Equal(3, rows.Count);
        Assert.Equal("高峰时段", rows[0].Title);
        Assert.Contains("小时级 Token 与成本", rows[0].Description);
        Assert.Contains("分钟级时间精度", rows[0].Description);

        Assert.Equal("会话长度", rows[1].Title);
        Assert.Contains("首次 / 最晚活动时间", rows[1].Description); // first_seen_at/last_seen_at 可推导
        Assert.Contains("兜底时间戳", rows[1].Description); // 不夸大精度（I-029 已知限制）

        Assert.Equal("数据边界", rows[2].Title);
        Assert.Contains("「未知」", rows[2].Description);
        Assert.Contains("不伪造小时级分布", rows[2].Description);

        // 页面渲染后三行标题可见
        var host = CreateTimeHost();
        var texts = RenderAndGetTexts(new TimeAnalysisView { DataContext = host.Vm });
        Assert.Contains(texts, t => t == "高峰时段");
        Assert.Contains(texts, t => t == "会话长度");
        Assert.Contains(texts, t => t == "数据边界");
    }

    [AvaloniaFact]
    public void Time_page_refreshes_on_global_filter_change()
    {
        var host = CreateTimeHost();
        var initialHourly = host.Stub.HourlyQueries.Count;
        var initialDaily = host.Stub.DailyQueries.Count;

        host.Filters.TimeRange = TimeRangeOption.Options[2]; // 最近 30 天
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(initialHourly + 1, host.Stub.HourlyQueries.Count);
        Assert.Equal(initialDaily + 1, host.Stub.DailyQueries.Count);
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(UsageQueryFilter.FormatDay(today.AddDays(-29)), host.Stub.HourlyQueries.Last().FromDay);
        Assert.Equal(UsageQueryFilter.FormatDay(today), host.Stub.HourlyQueries.Last().ToDay);

        host.Filters.Agent = new AgentOption("Codex", "Codex");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Codex", host.Stub.HourlyQueries.Last().Agent);
        Assert.Equal("Codex", host.Stub.DailyQueries.Last().Agent);
    }

    [AvaloniaFact]
    public void Time_page_discards_stale_response_and_recovers_from_errors()
    {
        var host = CreateTimeHost();
        Assert.True(host.Vm.HasUnknownNote); // 初查成功

        // 序号防护：第 2 次查询（30 天）挂起且释放时返回陈旧结果；第 3 次（90 天）同步完成
        host.Stub.HoldNextHourly(new HourlyDistributionResult
        {
            Hours = [],
            UnknownSessionCount = 99,
            UnknownTotalTokens = 99_999,
        });
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        host.Filters.TimeRange = TimeRangeOption.Options[3];
        Assert.True(host.Vm.HasUnknownNote);
        Assert.Contains("占 20.0%", host.Vm.UnknownNoteText);

        host.Stub.ReleaseHeldHourly(); // 旧响应放行：序号防护丢弃，不覆盖新选择
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("占 20.0%", host.Vm.UnknownNoteText);
        Assert.DoesNotContain("99", host.Vm.UnknownNoteText);

        // 失败保留上一次成功数据 + 错误面板；恢复后清除（P2-2 纪律）
        host.Stub.ThrowOnHourly = true;
        host.Filters.TimeRange = TimeRangeOption.Options[1];
        Dispatcher.UIThread.RunJobs();
        Assert.True(host.Vm.HasLoadError);
        Assert.True(host.Vm.HasUnknownNote); // 上一次成功标注保留

        host.Stub.ThrowOnHourly = false;
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        Dispatcher.UIThread.RunJobs();
        Assert.False(host.Vm.HasLoadError);
    }

    [AvaloniaFact]
    public void Time_page_dispose_stops_requery()
    {
        var host = CreateTimeHost();
        var afterInit = host.Stub.HourlyQueries.Count;

        host.Vm.Dispose();
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        host.Filters.Agent = new AgentOption("Codex", "Codex");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(afterInit, host.Stub.HourlyQueries.Count);
    }

    [AvaloniaFact]
    public void Shell_disposes_time_page_on_navigate_away()
    {
        // 外壳端到端：导航离开时间分析页后，旧实例不再随全局筛选重查（Dispose 由外壳统一触发，
        // D-003 审查 P1 的订阅泄漏回归，原占位页用例随真实化迁入本类）。
        var stub = new StubQueryService { Hourly = FixtureHourlyWithUnknown(), Daily = FixtureDaily() };
        var filters = new FilterStateService();
        var shell = CreateShell(stub, filters, new PricingEngine());

        shell.SelectedNavigation = shell.NavigationItems[5]; // 时间分析
        Dispatcher.UIThread.RunJobs();
        var time = Assert.IsType<TimeViewModel>(shell.CurrentPage);
        var afterInit = stub.HourlyQueries.Count;

        shell.SelectedNavigation = shell.NavigationItems[6]; // 离开 → 成本分析
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<CostViewModel>(shell.CurrentPage);

        filters.TimeRange = TimeRangeOption.Options[2];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(afterInit, stub.HourlyQueries.Count); // 旧页已退订，不再重查
        Assert.NotSame(time, shell.CurrentPage);
    }

    // ══════════════════════════ D-013 成本分析页 ══════════════════════════

    [AvaloniaFact]
    public void Cost_kpis_bind_estimated_cost_and_token_composition_shares()
    {
        // 占比为 Token 构成口径（Q-007 扩四项，脚注显式标注）：in 3700 / out 1950 /
        // 缓存读 2200 / 缓存写 100 / tot 7950 → 46.5% / 24.5% / 27.7% / 1.3%（四项合计全部 Token）。
        var host = CreateCostHost(new StubQueryService
        {
            Kpi = new UsageKpiResult { TotalTokens = 7_950, Cost = new AggregatedCost(3.92m, true) },
            Daily = FixtureDaily(),
        });

        Assert.Equal("$3.92+", host.Vm.TotalCostText); // 混合未知：已知成本下限 + "+"（同 D-010 口径）
        Assert.Equal("46.5%", host.Vm.InputShareText);
        Assert.Equal("24.5%", host.Vm.OutputShareText);
        Assert.Equal("27.7%", host.Vm.CacheReadShareText);
        Assert.Equal("1.3%", host.Vm.CacheWriteShareText);
        Assert.True(host.Vm.HasUnknownCostNote);
        Assert.Contains("已知成本下限", host.Vm.UnknownCostNoteText);
        Assert.Equal("0 个", host.Vm.UnpricedCountText); // 空引擎 → 0 个（诚实计数，不隐藏）
    }

    [AvaloniaFact]
    public void Cost_kpi_follows_aggregated_cost_four_states()
    {
        // (null,true) 全未知 → 「未知」+ 全未知说明（绝不折 0）
        var allUnknown = CreateCostHost(new StubQueryService
        {
            Kpi = new UsageKpiResult { Cost = new AggregatedCost(null, true) },
            Daily = FixtureDaily(),
        });
        Assert.Equal("未知", allUnknown.Vm.TotalCostText);
        Assert.Contains("全部未知", allUnknown.Vm.UnknownCostNoteText);

        // (x,false) 全已知 → 无说明行
        var allKnown = CreateCostHost(new StubQueryService
        {
            Kpi = new UsageKpiResult { Cost = new AggregatedCost(5m, false) },
            Daily = FixtureDaily(),
        });
        Assert.Equal("$5.00", allKnown.Vm.TotalCostText);
        Assert.False(allKnown.Vm.HasUnknownCostNote);

        // (null,false) 无成本数据 → "—"
        var noCost = CreateCostHost(new StubQueryService
        {
            Kpi = new UsageKpiResult { Cost = AggregatedCost.Empty },
            Daily = FixtureDaily(),
        });
        Assert.Equal("—", noCost.Vm.TotalCostText);
        Assert.False(noCost.Vm.HasUnknownCostNote);

        // 占比无基数（总 token 0）→ "—"，不伪造 0%
        var noTokens = CreateCostHost(new StubQueryService
        {
            Kpi = new UsageKpiResult { Cost = new AggregatedCost(1m, false) },
            Daily = new[] { new DailyUsagePoint { Day = "2026-03-10", HasData = true } },
        });
        Assert.Equal("—", noTokens.Vm.InputShareText);
        Assert.Equal("—", noTokens.Vm.OutputShareText);
        Assert.Equal("—", noTokens.Vm.CacheReadShareText);
        Assert.Equal("—", noTokens.Vm.CacheWriteShareText);
    }

    [AvaloniaFact]
    public void Unpriced_model_count_matches_pricing_engine_tc001()
    {
        // TC-001（D-013）：fixture 3 个缺价模型 → 页面 KPI 与定价引擎一致；估算总成本不含这三者成本
        // （stub KPI 按 I-028 契约只回传已知成本——未知不折 0 混入，见 UsageQueryServiceTests 成本契约用例）。
        var engine = new PricingEngine();
        foreach (var model in new[] { "unpriced-alpha", "unpriced-beta", "unpriced-gamma" })
        {
            engine.PriceTokens(model, new TokenUsageDetail { InputTokens = 1_000, OutputTokens = 500 });
        }

        Assert.Equal(3, engine.GetUnpricedModels().Count); // 引擎侧前提

        var host = CreateCostHost(
            new StubQueryService
            {
                Kpi = new UsageKpiResult { TotalTokens = 4_500, Cost = new AggregatedCost(2.25m, true) },
                Daily = FixtureDaily(),
            },
            engine: engine);

        Assert.Equal("3 个", host.Vm.UnpricedCountText);
        Assert.Equal(engine.GetUnpricedModels().Count.ToString("N0", CultureInfo.InvariantCulture) + " 个", host.Vm.UnpricedCountText);
        Assert.Contains("unpriced-alpha; unpriced-beta; unpriced-gamma", host.Vm.UnpricedModelsTooltip);
        // 估算总成本只含已知成本（2.25），缺价模型成本不在其中（AggregatedCost 契约：HasUnknownCost=true 标记）
        Assert.Equal("$2.25+", host.Vm.TotalCostText);
        Assert.True(host.Vm.HasUnknownCostNote);

        // 引擎联动：引擎再累计 1 个缺价模型 → 重查后页面计数与引擎一致（同一实例的实时快照）
        engine.PriceTokens("unpriced-delta", new TokenUsageDetail { InputTokens = 10 });
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, engine.GetUnpricedModels().Count);
        Assert.Equal("4 个", host.Vm.UnpricedCountText);
    }

    [AvaloniaFact]
    public void Unpriced_count_is_global_scope_and_visible_on_page()
    {
        // 全局口径说明：缺价清单按引擎计价过程累计、不随时间范围/Agent 筛选变化——页面脚注说明可见，
        // 筛选变化只重查用量数据、清单计数不变（stub 数据恒定）。
        var engine = new PricingEngine();
        engine.PriceTokens("unpriced-alpha", new TokenUsageDetail { InputTokens = 1_000 });

        var host = CreateCostHost(
            new StubQueryService
            {
                Kpi = new UsageKpiResult { Cost = new AggregatedCost(1m, false) },
                Daily = FixtureDaily(),
            },
            engine: engine);
        Assert.Equal("1 个", host.Vm.UnpricedCountText);

        host.Filters.Agent = new AgentOption("Codex", "Codex");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("1 个", host.Vm.UnpricedCountText); // 清单不随 Agent 筛选变化

        // 页面可见性：脚注「全局口径」说明 + 计数渲染
        var texts = RenderAndGetTexts(new CostAnalysisView { DataContext = host.Vm });
        Assert.Contains(texts, t => t!.Contains("全局口径") && t.Contains("不随筛选变化"));
        Assert.Contains(texts, t => t == "1 个");
        // Token 构成口径的显式标注（Q-007 四卡）在占比卡脚注可见；缓存读/写两卡标题与数值渲染
        Assert.Contains(texts, t => t!.Contains("按 Token 构成展示") && t.Contains("拆价口径待扩展"));
        Assert.Contains(texts, t => t == "27.7%");
        Assert.Contains(texts, t => t == "1.3%");
    }

    [AvaloniaFact]
    public void Cost_basis_table_has_three_rows_aligned_with_reconciliation_doc()
    {
        // 口径说明表内容定稿断言：三行、chip 立场对齐原型，注意事项与
        // docs/reconciliation/2026-10-11-reconciliation.md §1.2 的实测口径一致（非凭记忆转述）。
        var rows = CostViewModel.CreateCostBasisRows();

        Assert.Equal(3, rows.Count);
        Assert.Equal("实际计费", rows[0].Item);
        Assert.Equal("优先", rows[0].Stance);
        Assert.Contains("costUSD", rows[0].Note); // LogCost 路径（对账 §1.2 计价优先级行）
        Assert.Contains("docs/reconciliation", rows[0].Note);

        Assert.Equal("估算成本", rows[1].Item);
        Assert.Equal("模型价格表", rows[1].Stance);
        Assert.Contains("models.dev", rows[1].Note); // 三级回退：覆盖 → 远程 → 快照
        Assert.Contains("缓存输入", rows[1].Note); // 5m/1h 单独计价

        Assert.Equal("未知成本", rows[2].Item);
        Assert.Equal("不计入总额或单独标注", rows[2].Stance);
        Assert.Contains("绝不折 0", rows[2].Note);

        // 页面渲染后三行与表头可见
        var host = CreateCostHost();
        var texts = RenderAndGetTexts(new CostAnalysisView { DataContext = host.Vm });
        Assert.Contains(texts, t => t == "实际计费");
        Assert.Contains(texts, t => t == "估算成本");
        Assert.Contains(texts, t => t == "未知成本");
        Assert.Contains(texts, t => t == "成本计算口径");
    }

    [AvaloniaFact]
    public void Cost_page_refresh_guard_and_dispose()
    {
        var host = CreateCostHost();
        var initial = host.Stub.KpiQueries.Count;

        // 全局筛选联动（映射窗口对账）
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        Dispatcher.UIThread.RunJobs();
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(UsageQueryFilter.FormatDay(today.AddDays(-29)), host.Stub.KpiQueries.Last().FromDay);

        // 序号防护：挂起的旧 KPI 响应不覆盖新选择
        host.Stub.HoldNextKpi(new UsageKpiResult { Cost = new AggregatedCost(999m, false) });
        host.Filters.TimeRange = TimeRangeOption.Options[3]; // 挂起
        host.Filters.TimeRange = TimeRangeOption.Options[1]; // 完成
        Assert.Equal("$142.20", host.Vm.TotalCostText); // 默认 fixture KPI
        host.Stub.ReleaseHeldKpi();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("$142.20", host.Vm.TotalCostText); // 陈旧响应被丢弃

        // 失败保留上次成功数据 + 错误面板；Dispose 后不再重查
        host.Stub.ThrowOnKpi = true;
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        Dispatcher.UIThread.RunJobs();
        Assert.True(host.Vm.HasLoadError);
        Assert.Equal("$142.20", host.Vm.TotalCostText);

        host.Vm.Dispose();
        host.Filters.TimeRange = TimeRangeOption.Options[3];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(initial + 4, host.Stub.KpiQueries.Count); // 初查 + 30d + 挂起 + 7d + 失败重查
    }

    // ══════════════════════════ 冒烟与截图 ══════════════════════════

    [AvaloniaFact]
    public void Real_query_service_smoke_binds_and_renders_time_and_cost_pages()
    {
        var query = AppServices.Get<IUsageQueryService>();
        var filters = new FilterStateService();

        // 同口径直查对账（默认筛选 = 最近 7 天 · 所有 Agent）
        var today = DateOnly.FromDateTime(DateTime.Today);
        var expectedFilter = new UsageQueryFilter
        {
            FromDay = UsageQueryFilter.FormatDay(today.AddDays(-6)),
            ToDay = UsageQueryFilter.FormatDay(today),
        };

        var time = new TimeViewModel(ShellPages.For(PageId.Time), filters, query);
        var cost = new CostViewModel(
            ShellPages.For(PageId.Cost), filters, query, AppServices.Get<PricingEngine>());
        // 数据面查询已移到线程池执行（UsageQueryService 线程模型），VM 续体回 UI 队列：泵至加载完成
        // （HourlyDistribution/HeatmapCells 加载后恒非 null；UnpricedCountText 加载后恒为 "N 个"）
        DispatcherPump.PumpUntil(() =>
            time.HourlyDistribution is not null
            && time.HeatmapCells is not null
            && cost.UnpricedCountText != "—");

        var expectedHourly = Task.Run(() => query.GetHourlyDistributionAsync(expectedFilter)).GetAwaiter().GetResult();
        Assert.Equal(expectedHourly.Hours.Count, time.HourlyDistribution!.Hours.Count);
        Assert.Equal(expectedHourly.UnknownSessionCount, time.HourlyDistribution.UnknownSessionCount);
        if (expectedHourly.UnknownSessionCount > 0)
        {
            Assert.True(time.HasUnknownNote);
        }

        var expectedDaily = Task.Run(() => query.GetDailySeriesAsync(expectedFilter)).GetAwaiter().GetResult();
        Assert.Equal(expectedDaily.Count, time.HeatmapCells!.Count);

        var expectedKpi = Task.Run(() => query.GetKpiAsync(expectedFilter)).GetAwaiter().GetResult();
        Assert.Equal(OverviewViewModel.FormatCost(expectedKpi.Cost), cost.TotalCostText);

        // 两页整窗渲染不崩（数据有无随本机 facts 而定）
        foreach (var (vm, view) in new (ViewModelBase, UserControl)[]
                 {
                     (time, new TimeAnalysisView { DataContext = time }),
                     (cost, new CostAnalysisView { DataContext = cost }),
                 })
        {
            var window = new Window { Width = 1180, Height = 860, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Time_and_cost_pages_render_light_and_dark_screenshots_for_archive()
    {
        var dir = Environment.GetEnvironmentVariable("TOKENMETRY_TIMECOST_PREVIEW_DIR");

        foreach (var (navIndex, fileNamePrefix) in new (int, string)[]
                 {
                     (5, "time"),
                     (6, "cost"),
                 })
        {
            foreach (var (variant, suffix) in new (ThemeVariant, string)[]
                     {
                         (ThemeVariant.Light, "light"),
                         (ThemeVariant.Dark, "dark"),
                     })
            {
                var window = new MainWindow
                {
                    DataContext = AppServices.Get<MainWindowViewModel>(),
                    RequestedThemeVariant = variant,
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var shellVm = Assert.IsType<MainWindowViewModel>(window.DataContext);
                shellVm.SelectedNavigation = shellVm.NavigationItems[navIndex];
                Dispatcher.UIThread.RunJobs();
                Assert.IsAssignableFrom<ViewModelBase>(shellVm.CurrentPage);

                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);

                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, $"{fileNamePrefix}-{suffix}.png");
                    frame!.Save(path, new PngBitmapEncoderOptions());
                    Assert.True(File.Exists(path), $"截图未生成：{path}");
                }

                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
