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
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// D-009 Agent 统计页 / D-010 模型统计页 / D-011 项目统计页的自动化验证（headless，真实 App.axaml
/// + Semi，stub 模式同 SessionsTests/OverviewTests——stub 只承担数据回传，聚合口径对账在
/// UsageQueryServiceTests）：
/// 1. D-009：Top4 KPI 卡（token / 会话数 / 成本四态，Top4 截断与不足 4 槽位）+ 排行表七列绑定
///    （千分位 / 占比 / 未知成本「未知」绝不折 0）；
/// 2. D-010：明细表绑定 + 关联 Agent 列定案（唯一 → Agent 名 / 多 Agent → "主用 等 N 个" /
///    无会话数据 → "—"）+ 未知成本 chip 态 + 合计行口径（TC-001：缺价行 Token 计入、成本不计入）；
/// 3. D-011：六列绑定（占比 / 均值 = token ÷ 会话数取整、除零 → "—" / 歧义守卫原始路径如实展示）；
/// 4. 三页随全局筛选联动（映射窗口对账）、序号防护（挂起的旧响应不覆盖新选择）、Dispose 退订；
/// 5. 行点击跳转端到端（决策点 2）：Agent 行 → 全局单选 Agent + 会话页过滤；项目行 → 右栏页级
///    项目筛选程序化应用（选项缺失如实追加）+ 会话页过滤，「清除」可复位；
/// 6. 真实查询服务冒烟（同口径直查对账 + 整页渲染）；明暗两套离屏截图留档（docs/design/*-*.png）。
/// </summary>
public class DimensionPagesTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_DIMENSION_PREVIEW_DIR");

    // ── stub toast ──

    private sealed class StubToast : IToastService
    {
        public List<string> Messages { get; } = new();

        public void Show(string message, Visual? host = null) => Messages.Add(message);
    }

    // ── stub 查询服务（三维度可配置 + 调用记账 + 挂起，模式同 SessionsTests）──

    private sealed class StubQueryService : IUsageQueryService
    {
        public UsageKpiResult Kpi { get; set; } = new();

        public IReadOnlyList<DailyUsagePoint> Daily { get; set; } = Array.Empty<DailyUsagePoint>();

        public IReadOnlyList<DimensionUsageRow> Agents { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Models { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Projects { get; set; } = Array.Empty<DimensionUsageRow>();

        public bool ThrowOnAgents { get; set; }

        public bool ThrowOnModels { get; set; }

        public bool ThrowOnProjects { get; set; }

        public List<UsageQueryFilter> AgentQueries { get; } = new();

        public List<UsageQueryFilter> ModelQueries { get; } = new();

        public List<UsageQueryFilter> ProjectQueries { get; } = new();

        public List<SessionListRow> AllSessions { get; set; } = new();

        public List<SessionListQuery> SessionQueries { get; } = new();

        private TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>? _heldAgents;
        private IReadOnlyList<DimensionUsageRow>? _heldAgentsResult;

        private TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>? _heldModels;
        private IReadOnlyList<DimensionUsageRow>? _heldModelsResult;

        private TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>? _heldProjects;
        private IReadOnlyList<DimensionUsageRow>? _heldProjectsResult;

        /// <summary>让下一次维度查询挂起，Release 时以指定结果放行（序号防护用例）。</summary>
        public void HoldNextAgents(IReadOnlyList<DimensionUsageRow> resultOnRelease)
        {
            _heldAgentsResult = resultOnRelease;
            _heldAgents = new TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldAgents() => _heldAgents?.TrySetResult(_heldAgentsResult ?? Agents);

        public void HoldNextModels(IReadOnlyList<DimensionUsageRow> resultOnRelease)
        {
            _heldModelsResult = resultOnRelease;
            _heldModels = new TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldModels() => _heldModels?.TrySetResult(_heldModelsResult ?? Models);

        public void HoldNextProjects(IReadOnlyList<DimensionUsageRow> resultOnRelease)
        {
            _heldProjectsResult = resultOnRelease;
            _heldProjects = new TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldProjects() => _heldProjects?.TrySetResult(_heldProjectsResult ?? Projects);

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Kpi);

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Daily);

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            AgentQueries.Add(filter);
            if (ThrowOnAgents)
            {
                throw new InvalidOperationException("agents offline");
            }

            if (_heldAgents is { } held)
            {
                _heldAgents = null;
                return held.Task;
            }

            return Task.FromResult(Agents);
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            ModelQueries.Add(filter);
            if (ThrowOnModels)
            {
                throw new InvalidOperationException("models offline");
            }

            if (_heldModels is { } held)
            {
                _heldModels = null;
                return held.Task;
            }

            return Task.FromResult(Models);
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            ProjectQueries.Add(filter);
            if (ThrowOnProjects)
            {
                throw new InvalidOperationException("projects offline");
            }

            if (_heldProjects is { } held)
            {
                _heldProjects = null;
                return held.Task;
            }

            return Task.FromResult(Projects);
        }

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default)
        {
            SessionQueries.Add(query);
            var rows = AllSessions.AsEnumerable();
            if (query.Filter.Agent is { } agent)
            {
                rows = rows.Where(r => string.Equals(r.Agent, agent, StringComparison.Ordinal));
            }

            if (query.Project is { } project)
            {
                rows = rows.Where(r => string.Equals(r.Project, project, StringComparison.Ordinal));
            }

            return Task.FromResult(new SessionListResult
            {
                Rows = rows.ToList(),
                TotalCount = rows.Count(),
            });
        }

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult { Hours = Array.Empty<HourlyUsageBucket>() });
    }

    // ── fixture ──

    /// <summary>维度行构造（Agent/模型/项目共用形状；token 列 3:2:0.1 拆分供输入/输出/缓存列对账）。</summary>
    private static DimensionUsageRow DimRow(
        string key,
        long tokens,
        long sessions = 10,
        AggregatedCost? cost = null,
        decimal? share = null,
        string? displayName = null,
        string? dominantAgent = null,
        int? agentCount = null) => new()
    {
        Key = key,
        DisplayName = displayName,
        SessionCount = sessions,
        InputTokens = tokens * 3 / 5,
        OutputTokens = tokens * 2 / 5,
        CacheReadTokens = tokens / 10,
        TotalTokens = tokens,
        Cost = cost ?? new AggregatedCost(1m, false),
        TokenShare = share,
        DominantAgent = dominantAgent,
        AgentCount = agentCount,
    };

    /// <summary>Agent 维度 fixture（5 行：KPI Top4 截断有意义；成本覆盖已知 / 全未知 / 无数据三态）。</summary>
    private static IReadOnlyList<DimensionUsageRow> FixtureAgents() => new[]
    {
        DimRow("Claude Code", 5_000_000, sessions: 152, cost: new AggregatedCost(142.20m, false), share: 0.4118m),
        DimRow("Codex", 2_260_000, sessions: 96, cost: new AggregatedCost(68.50m, false), share: 0.1863m),
        DimRow("Cursor", 1_380_000, sessions: 71, cost: new AggregatedCost(null, true), share: 0.1137m),
        DimRow("GitHub Copilot", 760_000, sessions: 49, cost: new AggregatedCost(18.60m, false), share: 0.0627m),
        DimRow("Cline", 743_316, sessions: 44, cost: AggregatedCost.Empty, share: 0.0612m),
    };

    /// <summary>模型维度 fixture：单一 Agent / 多 Agent / 无会话数据（关联 Agent 列三态）+ 全未知成本行。</summary>
    private static IReadOnlyList<DimensionUsageRow> FixtureModels() => new[]
    {
        DimRow("claude-sonnet-4", 6_680, sessions: 4, cost: new AggregatedCost(3.06m, false),
            dominantAgent: "Claude Code", agentCount: 1),
        DimRow("gpt-x", 3_950, sessions: 3, cost: new AggregatedCost(2.54m, true),
            dominantAgent: "Codex", agentCount: 3),
        DimRow("claude-unpriced-model", 160, sessions: 1, cost: new AggregatedCost(null, true)),
        DimRow("tokenless-model", 0, sessions: 1, cost: AggregatedCost.Empty),
    };

    /// <summary>项目维度 fixture：常规 / 零会话（均值除零）/ 歧义守卫回退原始路径。</summary>
    private static IReadOnlyList<DimensionUsageRow> FixtureProjects() => new[]
    {
        DimRow("/work/alpha", 5_500, sessions: 3, cost: new AggregatedCost(2.25m, false), share: 0.5618m, displayName: "alpha"),
        DimRow("/work/beta", 600, sessions: 0, cost: new AggregatedCost(0.54m, false), share: 0.0613m, displayName: "beta"),
        DimRow("/other/tools", 300, sessions: 2, cost: new AggregatedCost(null, true), share: 0.0306m, displayName: "/other/tools"),
    };

    private static SessionListRow SessionRow(string id, string agent, string project, long tokens = 1_000) => new()
    {
        SessionId = id,
        Agent = agent,
        Project = project,
        ProjectDisplayName = project,
        DominantModel = "claude-sonnet-4",
        FirstSeenAt = DateTimeOffset.Now.AddHours(-1),
        LastSeenAt = DateTimeOffset.Now,
        TotalTokens = tokens,
        CostUsd = 0.5m,
    };

    // ── hosts ──

    private sealed record AgentsHost(
        AgentsViewModel Vm,
        StubQueryService Stub,
        FilterStateService Filters,
        List<string> NavigatedAgents);

    private static AgentsHost CreateAgentsHost(StubQueryService? stub = null, FilterStateService? filters = null)
    {
        stub ??= new StubQueryService { Agents = FixtureAgents() };
        filters ??= new FilterStateService();
        var navigated = new List<string>();
        var vm = new AgentsViewModel(ShellPages.For(PageId.Agents), filters, stub, navigated.Add);
        Dispatcher.UIThread.RunJobs();
        return new AgentsHost(vm, stub, filters, navigated);
    }

    private sealed record ModelsHost(ModelsViewModel Vm, StubQueryService Stub, FilterStateService Filters);

    private static ModelsHost CreateModelsHost(StubQueryService? stub = null, FilterStateService? filters = null)
    {
        stub ??= new StubQueryService { Models = FixtureModels() };
        filters ??= new FilterStateService();
        var vm = new ModelsViewModel(ShellPages.For(PageId.Models), filters, stub);
        Dispatcher.UIThread.RunJobs();
        return new ModelsHost(vm, stub, filters);
    }

    private sealed record ProjectsHost(
        ProjectsViewModel Vm,
        StubQueryService Stub,
        FilterStateService Filters,
        List<(string Path, string? Label)> NavigatedProjects);

    private static ProjectsHost CreateProjectsHost(StubQueryService? stub = null, FilterStateService? filters = null)
    {
        stub ??= new StubQueryService { Projects = FixtureProjects() };
        filters ??= new FilterStateService();
        var navigated = new List<(string, string?)>();
        var vm = new ProjectsViewModel(ShellPages.For(PageId.Projects), filters, stub, (path, label) => navigated.Add((path, label)));
        Dispatcher.UIThread.RunJobs();
        return new ProjectsHost(vm, stub, filters, navigated);
    }

    private static MainWindowViewModel CreateShell(StubQueryService stub, FilterStateService filters, StubToast toast)
    {
        var catalog = new AgentCatalogService(stub);
        var panel = new SessionsPanelViewModel(toast, stub, catalog, filters);
        return new MainWindowViewModel(filters, AppServices.Get<SettingsViewModel>(), panel, stub, catalog, toast,
            AppServices.Get<Core.Services.PricingEngine>());
    }

    /// <summary>页面会话查询（剔除右栏最近会话的 Top6 查询）。</summary>
    private static List<SessionListQuery> PageQueries(StubQueryService stub) =>
        stub.SessionQueries.Where(q => q.PageSize is null || q.PageSize != SessionsPanelViewModel.RecentTopN).ToList();

    // ══════════════════════════ D-009 Agent 统计页 ══════════════════════════

    [AvaloniaFact]
    public void Agent_rows_bind_seven_columns_and_kpi_top4()
    {
        var host = CreateAgentsHost();

        // 排行表：token 降序全量行（含零 token 行——诚实不筛）
        Assert.Equal(5, host.Vm.Rows.Count);
        var first = host.Vm.Rows[0];
        Assert.Equal("Claude Code", first.Name);
        Assert.Equal("152", first.SessionCountText);
        Assert.Equal("300万", first.InputTokensText); // token 列紧凑格式（列宽裁定：宁紧凑不裁切；zh 基线万档）
        Assert.Equal("200万", first.OutputTokensText);
        Assert.Equal("500万", first.TotalTokensText);
        Assert.Equal("$142.20", first.CostText);
        Assert.Equal("41.2%", first.ShareText);

        // 成本四态：全未知 → 「未知」绝不折 0；无数据 → "—"
        Assert.Equal("未知", host.Vm.Rows[2].CostText); // Cursor (null, true)
        Assert.Equal("—", host.Vm.Rows[4].CostText); // Cline Empty

        // KPI Top4（token 降序）：主值紧凑格式 + "N 会话 · 成本"脚注（成本四态同源）
        Assert.Equal("Claude Code", host.Vm.Kpi1!.Name);
        Assert.Equal("500万", host.Vm.Kpi1.TokensText);
        Assert.Equal("152 会话 · $142.20", host.Vm.Kpi1.FootText);
        Assert.Equal("Codex", host.Vm.Kpi2!.Name);
        Assert.Equal("71 会话 · 未知", host.Vm.Kpi3!.FootText); // 未知成本 KPI 卡不折 0
        Assert.Equal("GitHub Copilot", host.Vm.Kpi4!.Name);
    }

    [AvaloniaFact]
    public void Agent_kpi_slots_hide_beyond_available_agents()
    {
        var stub = new StubQueryService
        {
            Agents = new[] { DimRow("Codex", 1_000), DimRow("Cursor", 500) },
        };
        var host = CreateAgentsHost(stub);

        Assert.Equal("Codex", host.Vm.Kpi1!.Name);
        Assert.Equal("Cursor", host.Vm.Kpi2!.Name);
        Assert.Null(host.Vm.Kpi3);
        Assert.Null(host.Vm.Kpi4);
        Assert.Equal(2, host.Vm.Rows.Count);
    }

    [AvaloniaFact]
    public void Agent_row_click_invokes_callback_with_agent_and_resets_selection()
    {
        var host = CreateAgentsHost();

        host.Vm.SelectedAgent = host.Vm.Rows[1];
        Assert.Equal(("Codex"), Assert.Single(host.NavigatedAgents));
        Assert.Null(host.Vm.SelectedAgent); // 选中态复位，同一行可再次点击

        host.Vm.SelectedAgent = host.Vm.Rows[1];
        Assert.Equal(2, host.NavigatedAgents.Count);
    }

    [AvaloniaFact]
    public void Agent_row_click_noop_without_callback()
    {
        // 无外壳环境（预览/纯单测）回调为 null：不抛（no-op）
        var stub = new StubQueryService { Agents = FixtureAgents() };
        var vm = new AgentsViewModel(ShellPages.For(PageId.Agents), new FilterStateService(), stub);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedAgent = vm.Rows[0];
        Assert.Null(vm.SelectedAgent);
    }

    [AvaloniaFact]
    public void Agent_page_refreshes_on_global_filter_change()
    {
        var host = CreateAgentsHost();
        var initial = host.Stub.AgentQueries.Count;

        host.Filters.TimeRange = TimeRangeOption.Options[2]; // 最近 30 天
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(initial + 1, host.Stub.AgentQueries.Count);
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(UsageQueryFilter.FormatDay(today.AddDays(-29)), host.Stub.AgentQueries.Last().FromDay);
        Assert.Equal(UsageQueryFilter.FormatDay(today), host.Stub.AgentQueries.Last().ToDay);

        host.Filters.Agent = new AgentOption("Codex", "Codex");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Codex", host.Stub.AgentQueries.Last().Agent);
    }

    [AvaloniaFact]
    public void Agent_page_discards_stale_response_by_load_sequence_guard()
    {
        var host = CreateAgentsHost();
        Assert.Equal(5, host.Vm.Rows.Count);

        // 第 2 次查询（30 天）挂起且释放时返回陈旧集；第 3 次（90 天）同步完成
        host.Stub.HoldNextAgents(new[] { DimRow("stale-agent", 1) });
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        host.Filters.TimeRange = TimeRangeOption.Options[3];
        Assert.Equal(5, host.Vm.Rows.Count);

        host.Stub.ReleaseHeldAgents(); // 旧响应放行：序号防护丢弃，不覆盖新选择
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(5, host.Vm.Rows.Count);
        Assert.DoesNotContain(host.Vm.Rows, row => row.Name == "stale-agent");
    }

    [AvaloniaFact]
    public void Agent_page_dispose_stops_requery_and_keeps_error_semantics()
    {
        var host = CreateAgentsHost();
        var afterInit = host.Stub.AgentQueries.Count;

        // 加载失败：错误面板 + 已加载行保留 + 空态让位（P2-2 纪律）
        host.Stub.ThrowOnAgents = true;
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        Dispatcher.UIThread.RunJobs();
        Assert.True(host.Vm.HasLoadError);
        Assert.Equal(5, host.Vm.Rows.Count);
        Assert.False(host.Vm.ShowEmptyState);

        // 恢复：筛选再变化重试成功 → 错误态清除
        host.Stub.ThrowOnAgents = false;
        host.Filters.TimeRange = TimeRangeOption.Options[3];
        Dispatcher.UIThread.RunJobs();
        Assert.False(host.Vm.HasLoadError);

        // Dispose 退订：筛选变化不再重查
        host.Vm.Dispose();
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        host.Filters.Agent = new AgentOption("Codex", "Codex");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(afterInit + 2, host.Stub.AgentQueries.Count);
    }

    [AvaloniaFact]
    public void Agent_row_click_navigates_shell_to_filtered_sessions()
    {
        // TC-001 端到端：外壳注入真实跳转回调，行点击 = 写全局单选 Agent + 导航 + 会话页过滤
        var stub = new StubQueryService
        {
            Agents = FixtureAgents(),
            AllSessions = new List<SessionListRow>
            {
                SessionRow("c1", "Claude Code", "/tmp/alpha"),
                SessionRow("c2", "Claude Code", "/tmp/beta"),
                SessionRow("x1", "Codex", "/tmp/alpha"),
            },
        };
        var filters = new FilterStateService();
        var shell = CreateShell(stub, filters, new StubToast());

        shell.SelectedNavigation = shell.NavigationItems[2]; // Agent 统计
        Dispatcher.UIThread.RunJobs();
        var agents = Assert.IsType<AgentsViewModel>(shell.CurrentPage);
        Assert.Equal(PageId.Agents, agents.Page.Id);

        agents.SelectedAgent = agents.Rows[1]; // Codex
        Dispatcher.UIThread.RunJobs();

        // 落地会话页 + 全局单选 Agent 生效（顶部栏可见、可经「所有 Agent」清除）
        var sessions = Assert.IsType<SessionsViewModel>(shell.CurrentPage);
        Assert.Equal("Codex", filters.Agent.Id);
        var query = Assert.Single(PageQueries(stub));
        Assert.Equal("Codex", query.Filter.Agent);
        Assert.Equal(1, sessions.TotalCount); // stub 过滤后只剩 x1
        Assert.Equal("x1", Assert.Single(sessions.Rows).SessionId);
    }

    [AvaloniaFact]
    public void Agent_jump_appends_missing_option_when_catalog_unloaded()
    {
        // 目录加载失败窗口期（选项清单只有「所有 Agent」哨兵）：跳转目标 Agent 如实补入选项，
        // 不静默丢弃（顶部栏下拉随之可见/可复位）
        var stub = new StubQueryService { Agents = FixtureAgents() };
        var filters = new FilterStateService();
        var shell = CreateShell(stub, filters, new StubToast());
        Dispatcher.UIThread.RunJobs(); // 目录加载：stub Agents 非空 → 选项 = [All, ...fixture]

        // 模拟目录未含的 Agent（加载失败场景）：清空选项后点击行
        filters.UpdateAgentOptions(new[] { AgentOption.All });
        shell.SelectedNavigation = shell.NavigationItems[2];
        Dispatcher.UIThread.RunJobs();
        var agents = Assert.IsType<AgentsViewModel>(shell.CurrentPage);

        agents.SelectedAgent = agents.Rows[0]; // Claude Code 不在选项清单中
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Claude Code", filters.Agent.Id);
        Assert.Contains(filters.AgentOptions, o => o.Id == "Claude Code");
        Assert.IsType<SessionsViewModel>(shell.CurrentPage);
    }

    // ══════════════════════════ D-010 模型统计页 ══════════════════════════

    [AvaloniaFact]
    public void Model_rows_bind_columns_and_associated_agent_variants()
    {
        // 决策点 1 展示定案：唯一 Agent → 名称；多 Agent → "主用 等 N 个"；无会话数据 → "—"
        var host = CreateModelsHost();

        Assert.Equal(4, host.Vm.Rows.Count);
        var first = host.Vm.Rows[0];
        Assert.Equal("claude-sonnet-4", first.Name);
        Assert.Equal("Claude Code", first.AssociatedAgentText);
        Assert.False(first.IsMultiAgent);
        // 关联 Agent 图标与文案同源（I-034 补）：显示 Agent 名 → 有图标，且变体识别正确
        Assert.True(first.HasAssociatedAgentIcon);
        Assert.Equal("claudecode", first.AssociatedAgentVendor);
        Assert.Equal("4008", first.InputTokensText);
        Assert.Equal("2672", first.OutputTokensText);
        Assert.Equal("668", first.CacheReadTokensText);
        Assert.Equal("6680", first.TotalTokensText);
        Assert.Equal("$3.06", first.CostText);

        var multi = host.Vm.Rows[1];
        Assert.Equal("Codex 等 3 个", multi.AssociatedAgentText);
        Assert.True(multi.IsMultiAgent);
        Assert.True(multi.HasAssociatedAgentIcon); // 多 Agent 共用：图标给主用 Agent
        Assert.Equal("codex", multi.AssociatedAgentVendor);
        Assert.Equal("$2.54+", multi.CostText); // 混合未知：已知成本下限 + 后缀，不折 0

        Assert.Equal("—", host.Vm.Rows[2].AssociatedAgentText); // 无会话数据不猜
        Assert.False(host.Vm.Rows[2].HasAssociatedAgentIcon);   // 文案为 "—" 时图标整槽隐藏
        Assert.Equal("—", host.Vm.Rows[3].CostText); // 无成本数据
        Assert.False(host.Vm.Rows[3].HasAssociatedAgentIcon);
    }

    [AvaloniaFact]
    public void Unpriced_rows_show_unknown_chip_state_and_never_zero()
    {
        var host = CreateModelsHost();

        var unpriced = host.Vm.Rows[2]; // (null, true) 全未知
        Assert.Equal("未知", unpriced.CostText);
        Assert.True(unpriced.IsUnknownCost);

        // 已知 / 混合 / 无数据行不走 chip 态
        Assert.False(host.Vm.Rows[0].IsUnknownCost);
        Assert.False(host.Vm.Rows[1].IsUnknownCost);
        Assert.False(host.Vm.Rows[3].IsUnknownCost);
    }

    [AvaloniaFact]
    public void Totals_row_includes_unpriced_tokens_but_excludes_unknown_cost()
    {
        // TC-001：缺价行成本未知但 Token 计入、成本不计入；合计行口径在页面可见
        var host = CreateModelsHost();

        var totals = host.Vm.Totals;
        Assert.NotNull(totals);
        Assert.Equal("合计（4 个模型）", totals!.ModelCountText);
        Assert.Equal("6474", totals.InputTokensText); // 4008 + 2370 + 96 + 0（含缺价行的 96）
        Assert.Equal("4316", totals.OutputTokensText);
        Assert.Equal("1079", totals.CacheReadTokensText);
        Assert.Equal("1.08万", totals.TotalTokensText); // 10790（≥1万 → 万档）；含缺价行 160 —— Token 计入
        Assert.Equal("$5.60+", totals.CostText); // 3.06 + 2.54（缺价行成本未计入，混合行保留未知标记）
        Assert.Equal(2, totals.UnknownCount);
        Assert.True(totals.HasUnknownNote);
        Assert.Contains("2 个模型成本未知", totals.UnknownNoteText);
        Assert.Contains("未计入合计", totals.UnknownNoteText);

        // 空明细 → 无合计行（空态接管）
        var empty = CreateModelsHost(new StubQueryService
        {
            Models = new[] { DimRow("m", 0, cost: AggregatedCost.Empty) },
        });
        // 单行（即便全零）也有合计；真正空集由服务端空列表产生：
        Assert.NotNull(empty.Vm.Totals);
    }

    [AvaloniaFact]
    public void Totals_row_cost_states_follow_aggregated_cost_semantics()
    {
        // 全已知 → "$Y" 无脚注；全未知 → 「未知」；空集 → 无合计行
        var allKnown = CreateModelsHost(new StubQueryService
        {
            Models = new[]
            {
                DimRow("a", 100, cost: new AggregatedCost(1m, false)),
                DimRow("b", 200, cost: new AggregatedCost(2m, false)),
            },
        });
        Assert.Equal("$3.00", allKnown.Vm.Totals!.CostText);
        Assert.False(allKnown.Vm.Totals.HasUnknownNote);

        var allUnknown = CreateModelsHost(new StubQueryService
        {
            Models = new[]
            {
                DimRow("a", 100, cost: new AggregatedCost(null, true)),
                DimRow("b", 200, cost: new AggregatedCost(null, true)),
            },
        });
        Assert.Equal("未知", allUnknown.Vm.Totals!.CostText); // 无一已知 → 「未知」，绝不折 0
        Assert.Equal(2, allUnknown.Vm.Totals.UnknownCount);

        // 混合（部分已知 + 未知标记）→ 已知成本下限 + "+" 后缀（含未知口径可见）
        var mixed = CreateModelsHost(new StubQueryService
        {
            Models = new[]
            {
                DimRow("a", 100, cost: new AggregatedCost(null, true)),
                DimRow("b", 200, cost: new AggregatedCost(1m, true)),
            },
        });
        Assert.Equal("$1.00+", mixed.Vm.Totals!.CostText);

        var empty = new ModelsViewModel(
            ShellPages.For(PageId.Models), new FilterStateService(), new StubQueryService());
        Dispatcher.UIThread.RunJobs();
        Assert.Null(empty.Totals);
        Assert.False(empty.ShowTotals);
        Assert.True(empty.ShowEmptyState);
    }

    [AvaloniaFact]
    public void Models_page_refresh_guard_and_dispose()
    {
        var host = CreateModelsHost();
        var initial = host.Stub.ModelQueries.Count;

        // 全局筛选联动（映射窗口对账）
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        Dispatcher.UIThread.RunJobs();
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(UsageQueryFilter.FormatDay(today.AddDays(-29)), host.Stub.ModelQueries.Last().FromDay);

        // 序号防护：挂起的旧响应不覆盖新选择
        host.Stub.HoldNextModels(new[] { DimRow("stale-model", 1) });
        host.Filters.TimeRange = TimeRangeOption.Options[3]; // 挂起
        host.Filters.TimeRange = TimeRangeOption.Options[1]; // 完成
        Assert.Equal(4, host.Vm.Rows.Count);
        host.Stub.ReleaseHeldModels();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, host.Vm.Rows.Count);
        Assert.DoesNotContain(host.Vm.Rows, row => row.Name == "stale-model");

        // 失败保留上次成功数据 + 错误面板；Dispose 后不再重查
        host.Stub.ThrowOnModels = true;
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        Dispatcher.UIThread.RunJobs();
        Assert.True(host.Vm.HasLoadError);
        Assert.Equal(4, host.Vm.Rows.Count);
        Assert.NotNull(host.Vm.Totals); // 合计随上次成功数据保留

        host.Vm.Dispose();
        host.Filters.TimeRange = TimeRangeOption.Options[3];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(initial + 4, host.Stub.ModelQueries.Count); // 初查 + 30d + 挂起 + 7d + 失败重查
    }

    // ══════════════════════════ D-011 项目统计页 ══════════════════════════

    [AvaloniaFact]
    public void Project_rows_bind_six_columns_with_average_and_guard()
    {
        var host = CreateProjectsHost();

        Assert.Equal(3, host.Vm.Rows.Count);
        var first = host.Vm.Rows[0];
        Assert.Equal("alpha", first.Name); // 归一化展示名
        Assert.Equal("/work/alpha", first.RawPath); // 原始路径（跳转筛选值）
        Assert.Equal("3", first.SessionCountText);
        Assert.Equal("5500", first.TotalTokensText); // 紧凑格式（列宽裁定，同 Agent/模型页；zh 基线 <1万 全量显示）
        Assert.Equal("$2.25", first.CostText);
        Assert.Equal("56.2%", first.ShareText);
        Assert.Equal("1833", first.AverageText); // round(5500 / 3) = 1833 → 紧凑格式

        // 除零保护：零会话（纯 CLI 源项目）均值 "—"，不伪造 0
        Assert.Equal("—", host.Vm.Rows[1].AverageText);

        // 歧义守卫（I-025 同式）：展示名退回原始路径时页面如实展示
        var guarded = host.Vm.Rows[2];
        Assert.Equal("/other/tools", guarded.Name);
        Assert.Equal("/other/tools", guarded.RawPath);
        Assert.Equal("未知", guarded.CostText);
    }

    [AvaloniaFact]
    public void Project_row_click_invokes_callback_with_raw_path()
    {
        var host = CreateProjectsHost();

        host.Vm.SelectedProject = host.Vm.Rows[0];
        var (path, label) = Assert.Single(host.NavigatedProjects);
        Assert.Equal("/work/alpha", path); // 查询值 = 原始路径（I-029 Project 精确匹配）
        Assert.Equal("alpha", label); // 展示值 = 归一化名
        Assert.Null(host.Vm.SelectedProject);
    }

    [AvaloniaFact]
    public void Project_row_click_noop_without_callback()
    {
        var stub = new StubQueryService { Projects = FixtureProjects() };
        var vm = new ProjectsViewModel(ShellPages.For(PageId.Projects), new FilterStateService(), stub);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedProject = vm.Rows[0];
        Assert.Null(vm.SelectedProject);
    }

    [AvaloniaFact]
    public void Project_page_refresh_guard_and_dispose()
    {
        var host = CreateProjectsHost();
        var initial = host.Stub.ProjectQueries.Count;

        host.Filters.Agent = new AgentOption("Codex", "Codex");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Codex", host.Stub.ProjectQueries.Last().Agent);

        host.Stub.HoldNextProjects(new[] { DimRow("/stale", 1) });
        host.Filters.TimeRange = TimeRangeOption.Options[2]; // 挂起
        host.Filters.TimeRange = TimeRangeOption.Options[3]; // 完成
        Assert.Equal(3, host.Vm.Rows.Count);
        host.Stub.ReleaseHeldProjects();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(host.Vm.Rows, row => row.Name == "/stale");

        host.Stub.ThrowOnProjects = true;
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        Dispatcher.UIThread.RunJobs();
        Assert.True(host.Vm.HasLoadError);
        Assert.False(host.Vm.ShowEmptyState);

        host.Vm.Dispose();
        host.Filters.TimeRange = TimeRangeOption.Options[3];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(initial + 4, host.Stub.ProjectQueries.Count);
    }

    [AvaloniaFact]
    public void Project_row_click_navigates_shell_to_filtered_sessions_and_clears()
    {
        // TC-001 端到端：行点击 = 右栏页级项目筛选程序化应用 + 导航 + 会话页过滤；「清除」可复位
        var stub = new StubQueryService
        {
            Projects = FixtureProjects(),
            AllSessions = new List<SessionListRow>
            {
                SessionRow("a1", "Claude Code", "/work/alpha"),
                SessionRow("a2", "Codex", "/work/alpha"),
                SessionRow("b1", "Codex", "/work/beta"),
            },
        };
        var filters = new FilterStateService();
        var toast = new StubToast();
        var shell = CreateShell(stub, filters, toast);

        shell.SelectedNavigation = shell.NavigationItems[4]; // 项目统计
        Dispatcher.UIThread.RunJobs();
        var projects = Assert.IsType<ProjectsViewModel>(shell.CurrentPage);

        projects.SelectedProject = projects.Rows[0]; // /work/alpha
        Dispatcher.UIThread.RunJobs();

        // 落地会话页；筛选在右栏下拉可见（ProjectFilter）且首查已带上项目过滤
        var sessions = Assert.IsType<SessionsViewModel>(shell.CurrentPage);
        var panel = shell.SessionsPanel;
        Assert.Equal("/work/alpha", panel.ProjectFilter.Path);
        Assert.Equal("alpha", panel.ProjectFilter.Label);
        var query = Assert.Single(PageQueries(stub));
        Assert.Equal("/work/alpha", query.Project);
        Assert.Null(query.Filter.Agent); // 全局 Agent 未选
        Assert.Equal(2, sessions.TotalCount); // alpha 两会话

        // 「清除」复位：回到全部项目，重查不再带项目过滤
        panel.ClearQuickFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(panel.ProjectFilter.Path);
        Assert.Equal(ProjectFilterOption.All, panel.ProjectFilter);
        Assert.Null(PageQueries(stub).Last().Project);
    }

    [AvaloniaFact]
    public void Project_jump_appends_missing_option_when_catalog_stale()
    {
        // 选项清单是启动期全集缓存：跳转目标项目不在其中时如实追加（不静默丢弃筛选）。
        // stub 先以空项目目录创建外壳（目录缓存 [All]），随后再供给项目数据模拟"后台刷新后的新项目"。
        var stub = new StubQueryService
        {
            AllSessions = new List<SessionListRow> { SessionRow("a1", "Claude Code", "/work/alpha") },
        };
        var filters = new FilterStateService();
        var shell = CreateShell(stub, filters, new StubToast());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { ProjectFilterOption.All }, shell.SessionsPanel.ProjectFilterOptions);

        stub.Projects = FixtureProjects(); // 目录缓存之后才有数据
        shell.SelectedNavigation = shell.NavigationItems[4]; // 项目统计
        Dispatcher.UIThread.RunJobs();
        var projects = Assert.IsType<ProjectsViewModel>(shell.CurrentPage);

        projects.SelectedProject = projects.Rows[0];
        Dispatcher.UIThread.RunJobs();

        var panel = shell.SessionsPanel;
        Assert.Equal("/work/alpha", panel.ProjectFilter.Path);
        Assert.Single(panel.ProjectFilterOptions, o => o.Path == "/work/alpha"); // 追加一次，无重复
    }

    [AvaloniaFact]
    public void Agent_jump_with_agent_outside_checkbox_snapshot_shows_sessions_and_recovers()
    {
        // P1 审查修复回归（死胡同变体）：右栏复选组是启动期目录快照（[Alpha, Beta]），
        // 新 Agent X 后台刷新入库（目录缓存整个生命周期不刷新）→ Agent 页显示 X → 点击 X 行。
        // 修复前：只补全局选项 → 会话页 ResolveAgentScope 全局=X、复选组全勾但不含 X → Empty
        // 硬呈"显示 0 / 0 条"，且「清除」只重勾已知复选项（X 仍不在）救不回。
        // 修复后：同步补入已勾选的 X 复选项 → Single 模式正常出数，「清除」后仍按全局 X 出数。
        var stub = new StubQueryService
        {
            Agents = new[] { DimRow("Alpha", 100), DimRow("Beta", 50) }, // 启动期目录快照：不含 X
            AllSessions = new List<SessionListRow>
            {
                SessionRow("a1", "Alpha", "/tmp/alpha"),
                SessionRow("x1", "X", "/tmp/x"),
            },
        };
        var filters = new FilterStateService();
        var shell = CreateShell(stub, filters, new StubToast());
        Dispatcher.UIThread.RunJobs(); // 目录快照落缓存：全局选项 [All, Alpha, Beta]、复选组 [Alpha, Beta]
        Assert.Equal(2, shell.SessionsPanel.AgentFilterCount);
        Assert.DoesNotContain(shell.SessionsPanel.AgentFilters, a => a.AgentId == "X");

        stub.Agents = new[] { DimRow("X", 999_999), DimRow("Alpha", 100), DimRow("Beta", 50) }; // 刷新后 X 入库
        shell.SelectedNavigation = shell.NavigationItems[2]; // Agent 统计
        Dispatcher.UIThread.RunJobs();
        var agents = Assert.IsType<AgentsViewModel>(shell.CurrentPage);
        Assert.Equal("X", agents.Rows[0].Name);

        agents.SelectedAgent = agents.Rows[0]; // 点击 X 行
        Dispatcher.UIThread.RunJobs();

        // 死胡同解除：全局 X + 复选组补入已勾选 X（状态可见）→ 会话页正常出数
        var sessions = Assert.IsType<SessionsViewModel>(shell.CurrentPage);
        Assert.Equal("X", filters.Agent.Id);
        Assert.Equal(3, shell.SessionsPanel.AgentFilterCount);
        var appended = Assert.Single(shell.SessionsPanel.AgentFilters, a => a.AgentId == "X");
        Assert.True(appended.IsChecked);
        var jumpQuery = PageQueries(stub).Last();
        Assert.Equal("X", jumpQuery.Filter.Agent);
        Assert.Equal(1, sessions.TotalCount); // x1 —— 而非修复前的 0/0 死胡同

        // 「清除」复位：复选项全部重勾（含补入的 X）→ 全局 X 未动 → 仍按 X 出数（可恢复，非死胡同）
        shell.SessionsPanel.ClearQuickFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.All(shell.SessionsPanel.AgentFilters, a => Assert.True(a.IsChecked));
        Assert.Equal("X", PageQueries(stub).Last().Filter.Agent);
        Assert.Equal(1, sessions.TotalCount);
    }

    [AvaloniaFact]
    public void ApplyExternalProjectFilter_reuses_existing_option_without_duplicates()
    {
        var stub = new StubQueryService { Projects = FixtureProjects() };
        var filters = new FilterStateService();
        var catalog = new AgentCatalogService(stub);
        var panel = new SessionsPanelViewModel(new StubToast(), stub, catalog, filters);
        Dispatcher.UIThread.RunJobs(); // 选项清单 = fixture 三项

        var before = panel.ProjectFilterOptions.Count;
        panel.ApplyExternalProjectFilter("/work/beta", "beta");

        Assert.Equal("/work/beta", panel.ProjectFilter.Path);
        Assert.Equal(before, panel.ProjectFilterOptions.Count); // 已存在 → 不追加
    }

    // ══════════════════════════ 冒烟与截图 ══════════════════════════

    [AvaloniaFact]
    public void Real_query_service_smoke_binds_and_renders_three_dimension_pages()
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

        var agents = new AgentsViewModel(ShellPages.For(PageId.Agents), filters, query);
        var models = new ModelsViewModel(ShellPages.For(PageId.Models), filters, query);
        var projects = new ProjectsViewModel(ShellPages.For(PageId.Projects), filters, query);

        var expectedAgents = Task.Run(() => query.GetAgentBreakdownAsync(expectedFilter)).GetAwaiter().GetResult();
        var expectedModels = Task.Run(() => query.GetModelBreakdownAsync(expectedFilter)).GetAwaiter().GetResult();
        var expectedProjects = Task.Run(() => query.GetProjectBreakdownAsync(expectedFilter)).GetAwaiter().GetResult();

        // 数据面查询已移到线程池执行（UsageQueryService 线程模型），VM 续体回 UI 队列：泵至加载完成
        // （Rows 初始为空集，与"无数据"不可区分——按与同口径直查行数一致等待）
        DispatcherPump.PumpUntil(() =>
            agents.Rows.Count == expectedAgents.Count
            && models.Rows.Count == expectedModels.Count
            && projects.Rows.Count == expectedProjects.Count);

        Assert.Equal(expectedAgents.Count, agents.Rows.Count);
        if (expectedAgents.Count > 0)
        {
            var expected = expectedAgents.OrderByDescending(r => r.TotalTokens).ThenBy(r => r.Key, StringComparer.Ordinal).First();
            Assert.Equal(expected.Key, agents.Rows[0].Name);
            Assert.Equal(
                TrendChart.FormatCompactTokens(expected.TotalTokens),
                agents.Rows[0].TotalTokensText);
        }

        Assert.Equal(expectedModels.Count, models.Rows.Count);
        if (expectedModels.Count > 0)
        {
            // 关联 Agent 派生（决策点 1）：与直查同口径——唯一 Agent 显示名称、多 Agent 聚合文案
            var expected = expectedModels.OrderByDescending(r => r.TotalTokens).ThenBy(r => r.Key, StringComparer.Ordinal).First();
            Assert.Equal(expected.Key, models.Rows[0].Name);
            var expectedText = expected.AgentCount switch
            {
                1 => expected.DominantAgent ?? "—",
                > 1 => $"{expected.DominantAgent} 等 {expected.AgentCount.Value.ToString("N0", CultureInfo.InvariantCulture)} 个",
                _ => "—",
            };
            Assert.Equal(expectedText, models.Rows[0].AssociatedAgentText);
        }

        Assert.Equal(expectedProjects.Count, projects.Rows.Count);

        // 三页整窗渲染不崩（数据有无随本机 facts 而定）
        foreach (var (vm, view) in new (ViewModelBase, UserControl)[]
                 {
                     (agents, new AgentsView { DataContext = agents }),
                     (models, new ModelsView { DataContext = models }),
                     (projects, new ProjectsView { DataContext = projects }),
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
    public void Dimension_pages_render_light_and_dark_screenshots_for_archive()
    {
        var dir = Environment.GetEnvironmentVariable("TOKENMETRY_DIMENSION_PREVIEW_DIR");

        foreach (var (navIndex, fileNamePrefix) in new (int, string)[]
                 {
                     (2, "agents"),
                     (3, "models"),
                     (4, "projects"),
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

    [AvaloniaTheory]
    [InlineData(typeof(OverviewView))]
    [InlineData(typeof(AgentsView))]
    [InlineData(typeof(ModelsView))]
    [InlineData(typeof(ProjectsView))]
    public void Long_scroll_pages_root_is_scrollviewer_so_min_window_keeps_bottom_reachable(Type viewType)
    {
        // Q-002 审查 P2：四页"最小窗口下底部面板可达"修复（根容器改 ScrollViewer）此前无自动化锁，
        // 回退（有人把根改回普通容器）将无声——这里按视图类型锁定结构。
        var view = (UserControl)Activator.CreateInstance(viewType)!;
        Assert.IsAssignableFrom<ScrollViewer>(view.Content);
    }
}
