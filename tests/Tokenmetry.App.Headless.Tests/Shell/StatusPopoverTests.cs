using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Tokenmetry.App.Controls;
using Tokenmetry.App.Localization;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// D-015 状态栏快捷信息窗内容层的 headless 验证（真实 App.axaml + Semi 宿主）：
/// TC-001 三态渲染（Idle 数字与构成占比 / 注入速率 → Active banner / 注入异常 → Warn banner
/// 与 warn 配色类 / 速率窗口外降级 / 无数据诚实空态）；
/// TC-002 切语言重译（LiveText 经视图 CurrentLanguage 订阅重算，B-007 约定）；
/// 令牌纪律断言（StatusPopoverView.axaml 剔除注释后零硬编码色值）。
/// VM 绑定对账口径：期望值一律用与实现同源的函数计算（TrendChart.FormatCompactTokens /
/// OverviewViewModel.FormatCost / Translations 串），不断言具体语言文案。
/// </summary>
public class StatusPopoverTests
{
    // ── stub 查询服务（fixture 原型同款数值；agents 可变供速率差值配对）──

    private sealed class StubQueryService : IUsageQueryService
    {
        public UsageKpiResult Kpi { get; set; } = new();

        public IReadOnlyList<DailyUsagePoint> Daily { get; set; } = Array.Empty<DailyUsagePoint>();

        public IReadOnlyList<DimensionUsageRow> Agents { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Models { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<SessionListRow> Sessions { get; set; } = Array.Empty<SessionListRow>();

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult(Kpi);

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult(Daily);

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult(Agents);

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Models);

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(Array.Empty<DimensionUsageRow>());

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            Task.FromResult(new SessionListResult { Rows = Sessions });

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult());
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static UsageKpiResult FixtureKpi() => new()
    {
        TotalTokens = 42381,
        Cost = new AggregatedCost(12.46m, false),
        SessionCount = 18,
        CacheReadShare = 0.68m,
    };

    private static IReadOnlyList<DailyUsagePoint> FixtureDaily() =>
    [
        new DailyUsagePoint
        {
            Day = "2026-10-17",
            InputTokens = 26843,
            OutputTokens = 13124,
            CacheReadTokens = 2414,
            TotalTokens = 42381,
            HasData = true,
        },
    ];

    private static IReadOnlyList<DimensionUsageRow> FixtureAgents() =>
    [
        Agent("Claude Code", 20624),
        Agent("Codex", 13582),
        Agent("Cursor", 5261),
        Agent("GitHub Copilot", 2914),
    ];

    private static DimensionUsageRow Agent(string key, long tokens) => new()
    {
        Key = key,
        TotalTokens = tokens,
        TokenShare = tokens / 42381m,
    };

    private static DimensionUsageRow Model(string key, AggregatedCost cost, decimal? costShare, long tokens) => new()
    {
        Key = key,
        TotalTokens = tokens,
        Cost = cost,
        CostShare = costShare,
    };

    // ── TC-001: 三态渲染 ──

    [AvaloniaFact]
    public async Task Reload_populates_kpi_segments_and_top_agents()
    {
        // 最近活动时刻取相对当前时间（当日判定随真实日历；固定时刻会在跨零点后变"昨日"）
        var lastSeen = DateTimeOffset.Now;
        var stub = new StubQueryService
        {
            Kpi = FixtureKpi(),
            Daily = FixtureDaily(),
            Agents = FixtureAgents(),
            Sessions = [Session("Claude Code", lastSeen)],
        };
        var vm = new StatusPopoverViewModel(stub, null, new DateRangeService());

        await vm.ReloadAsync();

        // KPI 三格（与实现同源口径计算期望值）
        Assert.Equal(TrendChart.FormatCompactTokens(42381), vm.TodayTokensText);
        Assert.Equal(OverviewViewModel.FormatCost(new AggregatedCost(12.46m, false)), vm.TodayCostText);
        Assert.Equal(OverviewViewModel.FormatShare(0.68m), vm.CacheShareText);
        Assert.False(vm.IsNoData);

        // 构成条三段：26843 / 13124 / 2414（和 = 42381）
        var total = 42381d;
        Assert.Equal(3, vm.SegmentShares.Count);
        Assert.Equal(26843 / total * 100, vm.SegmentShares[0], 6);
        Assert.Equal(13124 / total * 100, vm.SegmentShares[1], 6);
        Assert.Equal(2414 / total * 100, vm.SegmentShares[2], 6);
        Assert.Equal("26,843", vm.InputTokensText);
        Assert.Equal((26843 / total * 100).ToString("F1", CultureInfo.InvariantCulture) + "%", vm.InputShareText);
        Assert.Equal("13,124", vm.OutputTokensText);
        Assert.Equal("2,414", vm.CacheTokensText);

        // Top4 行：token 千分位 + 占比 + 副题（无速率差值 → 最近活动降级）
        Assert.Equal(4, vm.TopAgents!.Count);
        var first = vm.TopAgents[0];
        Assert.Equal("Claude Code", first.Name);
        Assert.Equal("20,624", first.TokensText);
        Assert.Equal((20624 / 42381m * 100m).ToString("F1", CultureInfo.InvariantCulture) + "%", first.ShareText);
        Assert.Equal((double)(20624 / 42381m * 100m), first.BarValue, 6);
        Assert.Equal(
            string.Format(Translations.Instance.PopoverRowLastActiveFormat, lastSeen.LocalDateTime),
            first.Subtitle);
        Assert.False(first.BarValue < 0);

        // 三态：仅基线 → Idle、无 banner
        Assert.Equal(StatusPopoverState.Idle, vm.State);
        Assert.False(vm.IsBannerVisible);
    }

    [AvaloniaFact]
    public async Task Rate_rounds_drive_active_banner_with_agent_attribution()
    {
        var stub = new StubQueryService
        {
            Kpi = FixtureKpi(),
            Daily = FixtureDaily(),
            Agents = FixtureAgents(),
        };
        var vm = new StatusPopoverViewModel(stub, null, new DateRangeService());
        vm.HandleTodayTokensRound(42381, T0); // 轮次基线（首轮无速率；不可见不触发重查）
        await vm.ReloadAsyncAt(T0);           // 快照 1 @ T0

        // 本轮回写：今日 +3200 tokens（Claude Code 独增），间隔 60s（速率窗口内）
        stub.Agents =
        [
            Agent("Claude Code", 20624 + 3200),
            Agent("Codex", 13582),
            Agent("Cursor", 5261),
            Agent("GitHub Copilot", 2914),
        ];
        vm.HandleTodayTokensRound(42381 + 3200, T0.AddSeconds(60));
        await vm.ReloadAsyncAt(T0.AddSeconds(60)); // 快照 2 @ T0+60s → 分 Agent 速率成立 → 归属回写

        Assert.Equal(StatusPopoverState.Active, vm.State);
        Assert.True(vm.IsBannerVisible);
        Assert.False(vm.IsBannerWarn);
        Assert.Contains("Claude Code", vm.BannerTitle);
        // 3200 tokens / 1min = 3.2K/min（与实现同源格式化）
        Assert.Contains(
            string.Format(Translations.Instance.PopoverActiveBannerSubtitleFormat, "3.2K/min"),
            vm.BannerSubtitle);
        // Agent 行副题切到速率口径
        Assert.Equal(
            string.Format(Translations.Instance.PopoverRowRateFormat, "3.2K/min"),
            vm.TopAgents![0].Subtitle);
    }

    [AvaloniaFact]
    public async Task Warn_banner_on_anomaly_and_clears_on_next_clean_round()
    {
        var stub = new StubQueryService { Kpi = FixtureKpi(), Daily = FixtureDaily(), Agents = FixtureAgents() };
        var vm = new StatusPopoverViewModel(stub, null, new DateRangeService());
        await vm.ReloadAsync();

        vm.NoteAnomaly(Translations.Instance.AnomalyToastTitle, "Claude Code · 当前速度约为平时的 4.8×");

        Assert.Equal(StatusPopoverState.Warn, vm.State);
        Assert.True(vm.IsBannerVisible);
        Assert.True(vm.IsBannerWarn);
        Assert.Equal(Translations.Instance.AnomalyToastTitle, vm.BannerTitle);
        Assert.Equal("Claude Code · 当前速度约为平时的 4.8×", vm.BannerSubtitle);

        // 下一轮无异常 → Warn 清回（无增量速率 → Idle）
        vm.HandleTodayTokensRound(42381, T0.AddSeconds(60));
        Assert.Equal(StatusPopoverState.Idle, vm.State);
        Assert.False(vm.IsBannerVisible);
        Assert.False(vm.IsBannerWarn);
    }

    [AvaloniaFact]
    public async Task Rate_degrades_outside_refresh_window()
    {
        var stub = new StubQueryService { Kpi = FixtureKpi(), Daily = FixtureDaily(), Agents = FixtureAgents() };
        var vm = new StatusPopoverViewModel(stub, null, new DateRangeService());
        vm.HandleTodayTokensRound(42381, T0);

        // 间隔 30s（< 45s 下限：不是两个连续轮次，防启动缓存热载误判）
        vm.HandleTodayTokensRound(42381 + 3200, T0.AddSeconds(30));
        Assert.Equal(StatusPopoverState.Idle, vm.State);
        Assert.False(vm.IsBannerVisible);

        // 间隔 91s（> 90s 上限：卡定「刷新间隔 >1 分钟降级」；上一轮回写在 T0+30s）
        vm.HandleTodayTokensRound(42381 + 6400, T0.AddSeconds(121));
        Assert.Equal(StatusPopoverState.Idle, vm.State);
        Assert.False(vm.IsBannerVisible);
    }

    [AvaloniaFact]
    public async Task Empty_store_shows_honest_no_data_state()
    {
        var stub = new StubQueryService();
        var vm = new StatusPopoverViewModel(stub, null, new DateRangeService());

        await vm.ReloadAsync();

        Assert.True(vm.IsNoData);
        Assert.False(vm.HasTopAgents);
        Assert.Equal("—", vm.TodayTokensText);
        Assert.Equal("—", vm.TodayCostText);
        Assert.All(vm.SegmentShares, share => Assert.Equal(0, share));
    }

    // ── D-019: 模型用量区（Top3 按已知成本降序，未知成本沉底，无数据整块隐藏）──

    [AvaloniaFact]
    public async Task Reload_ranks_top_models_by_known_cost_and_sinks_unknown()
    {
        var stub = new StubQueryService
        {
            Kpi = FixtureKpi(),
            Daily = FixtureDaily(),
            Agents = FixtureAgents(),
            Models =
            [
                Model("claude-opus-4-5", new AggregatedCost(3.20m, false), 0.20m, 12000),
                Model("glm-5.3-flash", new AggregatedCost(12.46m, false), 0.80m, 20000),
                Model("unknown-priced", new AggregatedCost(null, true), null, 30000),
                Model("zcode-custom", new AggregatedCost(0m, false), 0m, 8000),
            ],
        };
        var vm = new StatusPopoverViewModel(stub, null, new DateRangeService());
        await vm.ReloadAsync();

        // Top3 截断：已知成本降序 glm(12.46) → claude(3.20) → zcode(已知为零)；
        // unknown-priced tokens 最多但成本未知（-1 哨兵）沉底被截断——排序看钱不看 token
        Assert.Equal(3, vm.TopModels!.Count);
        Assert.Equal(
            new[] { "glm-5.3-flash", "claude-opus-4-5", "zcode-custom" },
            vm.TopModels.Select(m => m.Name).ToArray());

        // 金额/占比与实现同源格式化（语言无关）；mini bar = CostShare × 100
        Assert.Equal(OverviewViewModel.FormatCost(new AggregatedCost(12.46m, false)), vm.TopModels[0].CostText);
        Assert.Equal(OverviewViewModel.FormatShare(0.80m), vm.TopModels[0].ShareText);
        Assert.Equal(80d, vm.TopModels[0].BarValue, 6);
        // 修正项 1：副题 = 今日 token 紧凑数（与 KPI 同口径同源格式化）
        Assert.Equal(TrendChart.FormatCompactTokens(20000), vm.TopModels[0].TokensText);
        // 已知为零 ≠ 未知：zcode 行占比 0 诚实显示、bar 空轨道，不冒充"—"
        Assert.Equal(OverviewViewModel.FormatShare(0m), vm.TopModels[2].ShareText);
        Assert.Equal(0d, vm.TopModels[2].BarValue, 6);
    }

    [AvaloniaFact]
    public async Task Model_section_renders_three_rows_and_hides_when_empty()
    {
        var stub = new StubQueryService
        {
            Kpi = FixtureKpi(),
            Daily = FixtureDaily(),
            Agents = FixtureAgents(),
            Models =
            [
                Model("claude-opus-4-5", new AggregatedCost(3.20m, false), 0.20m, 12000),
                Model("glm-5.3-flash", new AggregatedCost(12.46m, false), 0.80m, 20000),
                Model("zcode-custom", new AggregatedCost(0m, false), 0m, 8000),
            ],
        };
        var vm = new StatusPopoverViewModel(stub, null, new DateRangeService());
        await vm.ReloadAsync();
        var view = new StatusPopoverView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        var section = view.FindControl<StackPanel>("TopModelsSection");
        Assert.NotNull(section);
        Assert.True(section.IsVisible);
        Assert.Equal(3, view.GetVisualDescendants().OfType<Border>()
            .Count(b => b.Classes.Contains("model-chip-lg")));

        // 清空模型数据 → 重查 → 区块整体隐藏（Agent 区已有诚实空态，不渲染第二处）
        stub.Models = Array.Empty<DimensionUsageRow>();
        await vm.ReloadAsync();
        Assert.False(vm.HasTopModels);
        Assert.False(section.IsVisible);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(),
            b => b.Classes.Contains("model-chip-lg"));

        window.Close();
    }

    [Fact]
    public void Model_row_command_navigates_to_models_page()
    {
        PageId? navigated = null;
        var vm = new StatusPopoverViewModel(null, null, null, pageId => navigated = pageId);

        vm.OpenModelsCommand!.Execute(null);

        Assert.Equal(PageId.Models, navigated);
    }

    // ── 布局回归：行钮/footer 按钮须撑满对齐（Semi Button 主题默认 HorizontalAlignment=Center，
    //    收缩到内容自然宽度后整行居中，左右两列逐行漂移——见 2026-10-19 Top agents 对齐修复）──

    [AvaloniaFact]
    public async Task Top_agent_rows_stretch_and_align_both_edges()
    {
        var stub = new StubQueryService { Kpi = FixtureKpi(), Daily = FixtureDaily(), Agents = FixtureAgents() };
        var vm = new StatusPopoverViewModel(stub, null, new DateRangeService());
        await vm.ReloadAsync();

        var view = new StatusPopoverView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show(); // 触发首局布局

        var rows = view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("popover-agent-row")).ToList();
        Assert.Equal(4, rows.Count);

        // 行按钮等宽撑满（fixture 四行 token 量级不同 → 自然宽度本就不同，等宽即证明 stretch）
        Assert.All(rows, r => Assert.True(
            Math.Abs(r.Bounds.Width - rows[0].Bounds.Width) < 0.5,
            $"行按钮宽度不一致（应撑满）: {string.Join(", ", rows.Select(x => x.Bounds.Width))}"));

        // 行内元素 Bounds 相对各自父级，先折算到行按钮坐标系再跨行比较
        static double OffsetWithin(Avalonia.Visual node, Avalonia.Visual row)
        {
            var offset = 0d;
            for (var cur = node.Parent as Avalonia.Visual; cur is not null && cur != row; cur = cur.Parent as Avalonia.Visual)
                offset += cur.Bounds.Left;
            return offset;
        }

        // 图标芯片左缘逐行对齐
        var chipLefts = rows.Select(r =>
            {
                var chip = r.GetVisualDescendants().OfType<Border>()
                    .First(b => b.Classes.Contains("agent-chip-lg"));
                return chip.Bounds.Left + OffsetWithin(chip, r);
            })
            .ToList();
        Assert.All(chipLefts, c => Assert.True(
            Math.Abs(c - chipLefts[0]) < 0.5,
            $"行图标左缘未对齐: {string.Join(", ", chipLefts)}"));

        // token 数字右缘逐行对齐
        var tokenRights = rows.Zip(vm.TopAgents!, (r, row) => (Row: r, Text: row.TokensText))
            .Select(x =>
            {
                var text = x.Row.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Text == x.Text);
                return text.Bounds.Right + OffsetWithin(text, x.Row);
            })
            .ToList();
        Assert.All(tokenRights, t => Assert.True(
            Math.Abs(t - tokenRights[0]) < 0.5,
            $"token 数字右缘未对齐: {string.Join(", ", tokenRights)}"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task Footer_buttons_fill_their_grid_columns()
    {
        var stub = new StubQueryService { Kpi = FixtureKpi(), Daily = FixtureDaily(), Agents = FixtureAgents() };
        var vm = new StatusPopoverViewModel(stub, null, new DateRangeService());
        await vm.ReloadAsync();

        var view = new StatusPopoverView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        var footer = view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("popover-btn")).ToList();
        Assert.Equal(2, footer.Count);

        var grid = Assert.IsType<Grid>(footer[0].Parent);
        // 两枚按钮分别贴齐所在列外缘（列宽 *,1.4*），而非收缩后居中（Bounds 均相对同一 Grid）
        Assert.True(Math.Abs(footer[0].Bounds.Left) < 0.5,
            $"footer 次按钮未贴列左缘: {footer[0].Bounds.Left}");
        Assert.True(Math.Abs(footer[1].Bounds.Right - grid.Bounds.Width) < 0.5,
            $"footer 主按钮未贴列右缘: {footer[1].Bounds.Right} vs {grid.Bounds.Width}");

        window.Close();
    }

    // ── TC-002: 切语言重译 ──

    [AvaloniaFact]
    public void Language_switch_retranslates_live_text_via_view_subscription()
    {
        var vm = new StatusPopoverViewModel(null, null, null);
        var view = new StatusPopoverView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show(); // 挂树订阅 CurrentLanguage（B-007 挂树/脱树对称）

        var live = view.FindControl<TextBlock>("LiveTextBlock");
        Assert.NotNull(live);
        var originalLanguage = Translations.Instance.CurrentLanguage;
        try
        {
            Assert.Equal(Translations.Instance.PopoverLiveIdle, live!.Text);

            Translations.Instance.SetLanguage(AppLanguage.English);
            // 语言无关断言：两侧随语言同步重解析（视图订阅 → VM OnLanguageChanged → LiveText 重取串）
            Assert.Equal(Translations.Instance.PopoverLiveIdle, live.Text);
            Assert.Equal(vm.LiveText, live.Text);

            Translations.Instance.SetLanguage(originalLanguage);
            Assert.Equal(Translations.Instance.PopoverLiveIdle, live.Text);
        }
        finally
        {
            Translations.Instance.SetLanguage(originalLanguage); // 进程级单例，恢复基线
        }

        window.Close();
    }

    // ── 令牌纪律：视图零硬编码色值（剔除 XML 注释后扫描 hex 字面量）──

    [Fact]
    public void StatusPopoverView_axaml_has_no_hardcoded_colors()
    {
        var axaml = File.ReadAllText(
            FindSourceFile("src/Tokenmetry.App/Views/StatusPopoverView.axaml"));
        var withoutComments = Regex.Replace(axaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        var hexColors = Regex.Matches(withoutComments, "#[0-9a-fA-F]{3,8}\\b");
        Assert.True(hexColors.Count == 0,
            "StatusPopoverView.axaml 存在硬编码色值（设计令牌纪律：一律 DynamicResource）：" +
            string.Join(", ", hexColors.Select(m => m.Value)));
    }

    // ── 速率跟踪器与分 Agent 差值（纯逻辑对账）──

    [Fact]
    public void RefreshRateTracker_first_round_sets_baseline_only()
    {
        var tracker = new RefreshRateTracker();
        var first = tracker.Feed(42381, T0);
        Assert.Null(first.TokensPerMinute);

        var second = tracker.Feed(42381 + 3200, T0.AddSeconds(60));
        Assert.Equal(3200, second.TokensPerMinute!.Value, 6);
        Assert.Equal(T0.AddSeconds(60), second.LastIncrementAt);
    }

    [Fact]
    public void RefreshRateTracker_rebases_on_decrease_without_rate()
    {
        var tracker = new RefreshRateTracker();
        tracker.Feed(42381, T0);
        // 跨日回落：负增量只重定基线
        var sample = tracker.Feed(500, T0.AddSeconds(60));
        Assert.Null(sample.TokensPerMinute);
        // 回落后再增量照常出速率
        var next = tracker.Feed(3700, T0.AddSeconds(120));
        Assert.Equal(3200, next.TokensPerMinute!.Value, 6);
    }

    [Fact]
    public void ComputeAgentRates_pairs_snapshots_within_window_only()
    {
        var prev = new Dictionary<string, long> { ["Claude Code"] = 20624, ["Codex"] = 13582 };
        var curr = new Dictionary<string, long> { ["Claude Code"] = 23824, ["Codex"] = 13000, ["New Agent"] = 4000 };

        var rates = StatusPopoverViewModel.ComputeAgentRates(prev, T0, curr, T0.AddSeconds(60));

        Assert.Equal(3200, rates["Claude Code"], 6);      // 增量 → 速率
        Assert.False(rates.ContainsKey("Codex"));          // 回落 → 无速率（不伪造）
        Assert.False(rates.ContainsKey("New Agent"));      // 首次出现 → 无同源基线
    }

    [Fact]
    public void ComputeAgentRates_ignores_out_of_window_interval()
    {
        var prev = new Dictionary<string, long> { ["Claude Code"] = 20624 };
        var rates = StatusPopoverViewModel.ComputeAgentRates(
            prev, T0, new Dictionary<string, long> { ["Claude Code"] = 23824 }, T0.AddSeconds(120));
        Assert.Empty(rates);
    }

    private static SessionListRow Session(string agent, DateTimeOffset lastSeen) => new()
    {
        SessionId = "s-" + agent,
        Agent = agent,
        LastSeenAt = lastSeen,
        TotalTokens = 1000,
    };

    private static string FindSourceFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relativePath)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relativePath);
    }
}
