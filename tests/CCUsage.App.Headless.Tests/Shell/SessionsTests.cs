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
using CCUsage.App.DependencyInjection;
using CCUsage.App.Services;
using CCUsage.App.ViewModels;
using CCUsage.App.Views;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using Xunit;

namespace CCUsage.App.Headless.Tests.Shell;

/// <summary>
/// D-008 会话明细页 + 右栏最近会话联动的自动化验证（headless，真实 App.axaml + Semi）：
/// 1. 页面状态（搜索/排序/右栏快速筛选/全局筛选）→ I-029 SessionListQuery 的映射对账；
/// 2. 搜索防抖（重启式 300ms；headless 不驱动 DispatcherTimer 时钟，经 CommitSearchForTests 直调提交）；
/// 3. 「加载更多」分页契约（I-029 Page/PageSize，页大小 50）与计数条 "显示 X / Y 条"；
/// 4. Agent 复选集 → 查询范围（全勾→null / 单勾→单值 / 真子集→逐 Agent 合并 + 客户端分页；
///    全局单选 Agent 与复选集取交集，无交集→诚实空态不发查询）；
/// 5. 未知成本列四态呈现（null ≠ 0）；Agent/模型/项目清单目录（单一真相源 + 服务层缓存）；
/// 6. ≥1 万行 fixture 虚拟化（realized container 数远小于总行数；真机滚动流畅性留人工目检）；
/// 7. 序号防护（挂起的旧响应不覆盖新选择）与 Dispose 退订（全局筛选 + 右栏事件 + 防抖定时器）；
/// 8. 右栏最近会话（全局筛选映射 + 最近活动 Top6）与下钻 toast 占位；
/// 9. 真实查询服务冒烟（同口径直查对账，环境无关）；明暗两套离屏截图留档（docs/design/sessions-*.png）。
/// 分工：会话查询的过滤/排序/分页 SQL 对账在 UsageQueryServiceTests（I-029）已完成，
/// 本类做"页面状态 → 查询参数 → 行展示"的页面层对账（stub 最小复刻同口径，只承担数据回传）。
/// </summary>
public class SessionsTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("CCUSAGE_SESSIONS_PREVIEW_DIR");

    // ── stub toast（记录消息，下钻 toast 与右栏 toast 断言用）──

    private sealed class StubToast : IToastService
    {
        public List<string> Messages { get; } = new();

        public void Show(string message, Visual? host = null) => Messages.Add(message);
    }

    // ── stub 查询服务（会话列表可配置 + 目录维度 + 调用记账 + 挂起）──

    private sealed class StubQueryService : IUsageQueryService
    {
        public UsageKpiResult Kpi { get; set; } = new();

        public IReadOnlyList<DailyUsagePoint> Daily { get; set; } = Array.Empty<DailyUsagePoint>();

        public IReadOnlyList<DimensionUsageRow> Agents { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Models { get; set; } = Array.Empty<DimensionUsageRow>();

        public IReadOnlyList<DimensionUsageRow> Projects { get; set; } = Array.Empty<DimensionUsageRow>();

        public bool ThrowOnAgents { get; set; }

        /// <summary>会话列表查询失败开关（加载失败语义用例）。</summary>
        public bool ThrowOnSessions { get; set; }

        /// <summary>会话列表全集（stub 以最小口径复刻服务侧过滤/排序/分页，见 DefaultSessions）。</summary>
        public List<SessionListRow> AllSessions { get; set; } = new();

        public List<SessionListQuery> SessionQueries { get; } = new();

        private TaskCompletionSource<SessionListResult>? _heldSessions;
        private SessionListResult? _heldResult;

        /// <summary>让下一次 GetSessionsAsync 挂起（序号防护用例）。</summary>
        public void HoldNextSessions(SessionListResult resultOnRelease)
        {
            _heldResult = resultOnRelease;
            _heldSessions = new TaskCompletionSource<SessionListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldSessions() => _heldSessions?.TrySetResult(_heldResult ?? new SessionListResult());

        /// <summary>
        /// 让下一次 GetAgentBreakdownAsync 挂起（目录未就绪的启动竞态用例：P2-1）。
        /// 不用 RunContinuationsAsynchronously：放行时续体需在调用线程（UI 线程）内联走完
        /// （目录赋值 → 面板初始化 → 最近会话查询）。已返回的挂起任务与"待消费挂起点"分开记账——
        /// 消费即清 _heldAgents，放行引用 _releasedAgentsTask，否则 Release 会因引用丢失而空转。
        /// </summary>
        public void HoldNextAgents(IReadOnlyList<DimensionUsageRow> resultOnRelease)
        {
            _heldAgents = new TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>();
            _heldAgentsResult = resultOnRelease;
        }

        public void ReleaseHeldAgents()
        {
            var task = _releasedAgentsTask;
            _releasedAgentsTask = null;
            task?.TrySetResult(_heldAgentsResult ?? Agents);
        }

        private TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>? _heldAgents;
        private TaskCompletionSource<IReadOnlyList<DimensionUsageRow>>? _releasedAgentsTask;
        private IReadOnlyList<DimensionUsageRow>? _heldAgentsResult;

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default)
        {
            SessionQueries.Add(query);
            if (ThrowOnSessions)
            {
                throw new InvalidOperationException("sessions offline");
            }

            // 挂起只作用于"页面查询"（页大小 50 或合并模式不分页）；右栏最近会话（Top6）不参与挂起，
            // 否则全局筛选变更时右栏重查会先消费掉挂起点、序号防护用例失真。
            var isPageQuery = query.PageSize is null || query.PageSize != SessionsPanelViewModel.RecentTopN;
            if (_heldSessions is { } held && isPageQuery)
            {
                _heldSessions = null;
                return held.Task;
            }

            return Task.FromResult(DefaultSessions(query));
        }

        /// <summary>与 UsageQueryService.GetSessionsAsync 同口径的最小复刻（仅承担数据回传；
        /// 服务行为的逐项对账在 CCUsage.Infrastructure.Tests UsageQueryServiceTests）。</summary>
        private SessionListResult DefaultSessions(SessionListQuery query)
        {
            var rows = AllSessions.AsEnumerable();
            if (query.Filter.Agent is { } agent)
            {
                rows = rows.Where(r => string.Equals(r.Agent, agent, StringComparison.Ordinal));
            }

            if (query.Project is { } project)
            {
                rows = rows.Where(r => string.Equals(r.Project, project, StringComparison.Ordinal));
            }

            if (query.Model is { } model)
            {
                rows = rows.Where(r => string.Equals(r.DominantModel, model, StringComparison.Ordinal));
            }

            if (query.SearchTerm is { } term)
            {
                rows = rows.Where(r =>
                    r.SessionId.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || r.Agent.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || r.Project.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || r.ProjectDisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || r.DominantModel.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            IOrderedEnumerable<SessionListRow> ordered = query.SortBy switch
            {
                SessionSortField.TotalTokens => query.SortDescending
                    ? rows.OrderByDescending(r => r.TotalTokens)
                    : rows.OrderBy(r => r.TotalTokens),
                SessionSortField.Cost => query.SortDescending
                    ? rows.OrderByDescending(r => r.CostUsd ?? decimal.MinValue)
                    : rows.OrderBy(r => r.CostUsd ?? decimal.MinValue),
                _ => query.SortDescending
                    ? rows.OrderByDescending(r => r.LastSeenAt)
                    : rows.OrderBy(r => r.LastSeenAt),
            };
            var sorted = ordered.ThenBy(r => r.SessionId, StringComparer.Ordinal).ToList();
            var page = Math.Max(1, query.Page);
            List<SessionListRow> pageRows;
            if (query.PageSize is { } size)
            {
                var skip = (long)(page - 1) * size;
                pageRows = skip >= sorted.Count
                    ? new List<SessionListRow>()
                    : sorted.Skip((int)skip).Take(size).ToList();
            }
            else
            {
                pageRows = sorted;
            }

            return new SessionListResult { Rows = pageRows, TotalCount = sorted.Count, Page = page, PageSize = query.PageSize };
        }

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Kpi);

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Daily);

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default)
        {
            if (ThrowOnAgents)
            {
                throw new InvalidOperationException("agent catalog offline");
            }

            if (_heldAgents is { } held)
            {
                _heldAgents = null;
                _releasedAgentsTask = held;
                return held.Task;
            }

            return Task.FromResult(Agents);
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Models);

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Projects);

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult { Hours = Array.Empty<HourlyUsageBucket>() });
    }

    // ── fixture ──

    /// <summary>目录用 Agent 维度 fixture（token 降序 = 清单顺序；3 个 Agent 供复选集用例）。</summary>
    private static IReadOnlyList<DimensionUsageRow> CatalogAgents() => new[]
    {
        DimRow("Claude Code", 5_000_000),
        DimRow("Codex", 2_000_000),
        DimRow("Cursor", 1_000_000),
    };

    private static IReadOnlyList<DimensionUsageRow> CatalogModels() => new[]
    {
        DimRow("model-a", 3_000_000),
        DimRow("model-b", 1_000_000),
    };

    private static IReadOnlyList<DimensionUsageRow> CatalogProjects() => new[]
    {
        ProjectDimRow("/tmp/alpha", "alpha", 4_000_000),
        ProjectDimRow("/tmp/beta", "beta", 2_000_000),
    };

    private static DimensionUsageRow DimRow(string key, long tokens) => new()
    {
        Key = key,
        SessionCount = 10,
        TotalTokens = tokens,
        Cost = new AggregatedCost(1m, false),
        TokenShare = 0.5m,
    };

    private static DimensionUsageRow ProjectDimRow(string path, string displayName, long tokens) => new()
    {
        Key = path,
        DisplayName = displayName,
        SessionCount = 10,
        TotalTokens = tokens,
        Cost = new AggregatedCost(1m, false),
        TokenShare = 0.5m,
    };

    /// <summary>
    /// 确定性会话 fixture（测试内合成，不进 git）：Agent/模型/项目循环分配，
    /// LastSeenAt 随 i 递增变新（"最近活动"序 = i 降序），每 10 行一个未知成本（CostUsd=null）。
    /// </summary>
    private static List<SessionListRow> GenerateSessions(int count)
    {
        var agents = new[] { "Claude Code", "Codex", "Cursor" };
        var models = new[] { "model-a", "model-b" };
        var now = DateTimeOffset.Now;
        return Enumerable.Range(0, count).Select(i => new SessionListRow
        {
            SessionId = $"session-{i:D6}",
            Agent = agents[i % 3],
            Project = $"/tmp/project-{i % 5}",
            ProjectDisplayName = $"project-{i % 5}",
            DominantModel = models[i % 2],
            FirstSeenAt = now.AddMinutes(-(count - i) * 3),
            LastSeenAt = now.AddMinutes(-(count - i) * 3).AddMinutes(30),
            InputTokens = 100 + i,
            OutputTokens = 50,
            CacheCreationTokens = 0,
            CacheReadTokens = 10,
            TotalTokens = 160 + (i % 97),
            CostUsd = i % 10 == 9 ? null : 0.5m + i % 13 / 10m,
        }).ToList();
    }

    /// <summary>手工精排的小型会话集（Agent 交集 / 合并排序用例，token 互不相同）。</summary>
    private static List<SessionListRow> HandcraftedSessions() => new()
    {
        Row("c1", "Claude Code", 500),
        Row("c2", "Claude Code", 100),
        Row("x1", "Codex", 900),
        Row("x2", "Codex", 50),
        Row("r1", "Cursor", 700),
        Row("r2", "Cursor", 10),
    };

    private static SessionListRow Row(string id, string agent, long tokens, decimal? cost = null) => new()
    {
        SessionId = id,
        Agent = agent,
        Project = $"/tmp/{agent}",
        ProjectDisplayName = agent,
        DominantModel = "model-a",
        FirstSeenAt = DateTimeOffset.Now.AddHours(-1),
        LastSeenAt = DateTimeOffset.Now,
        TotalTokens = tokens,
        CostUsd = cost,
    };

    private sealed record TestHost(
        SessionsViewModel Vm,
        SessionsPanelViewModel Panel,
        StubQueryService Stub,
        StubToast Toast,
        FilterStateService Filters);

    private static TestHost CreateHost(StubQueryService? stub = null, FilterStateService? filters = null)
    {
        stub ??= new StubQueryService();
        filters ??= new FilterStateService();
        var toast = new StubToast();
        var panel = new SessionsPanelViewModel(toast, stub, new AgentCatalogService(stub), filters);
        var vm = new SessionsViewModel(ShellPages.For(PageId.Sessions), filters, stub, panel, toast);
        Dispatcher.UIThread.RunJobs(); // 面板选项/最近会话初始化 + 页面首查完成
        return new TestHost(vm, panel, stub, toast, filters);
    }

    /// <summary>页面查询（页大小 50 或合并模式不分页），剔除右栏最近会话的 Top6 查询。</summary>
    private static List<SessionListQuery> PageQueries(StubQueryService stub) =>
        stub.SessionQueries.Where(q => q.PageSize is null || q.PageSize != SessionsPanelViewModel.RecentTopN).ToList();

    // ── 1. 页面状态 → I-029 查询映射（组合筛选）──

    [AvaloniaFact]
    public void Combined_filters_map_to_session_list_query()
    {
        var stub = new StubQueryService
        {
            Agents = CatalogAgents(),
            Models = CatalogModels(),
            Projects = CatalogProjects(),
            AllSessions = GenerateSessions(120),
        };
        var host = CreateHost(stub);

        host.Filters.TimeRange = TimeRangeOption.Options[1]; // 最近 30 天
        host.Filters.Agent = new AgentOption("Claude Code", "Claude Code");
        host.Vm.SearchText = "algo";
        host.Vm.CommitSearchForTests();
        host.Vm.SelectedSort = host.Vm.SortOptions[1]; // 按成本排序
        host.Panel.ModelFilter = host.Panel.ModelFilterOptions[^1]; // model-b
        host.Panel.ProjectFilter = host.Panel.ProjectFilterOptions[^1]; // /tmp/beta
        host.Panel.ApplyQuickFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var query = PageQueries(stub).Last();
        Assert.Equal("algo", query.SearchTerm);
        Assert.Equal(SessionSortField.Cost, query.SortBy);
        Assert.True(query.SortDescending);
        Assert.Equal("Claude Code", query.Filter.Agent);
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(UsageQueryFilter.FormatDay(today.AddDays(-29)), query.Filter.FromDay);
        Assert.Equal(UsageQueryFilter.FormatDay(today), query.Filter.ToDay);
        Assert.Equal("model-b", query.Model);
        Assert.Equal("/tmp/beta", query.Project);
        Assert.Equal(1, query.Page);
        Assert.Equal(SessionsViewModel.PageSize, query.PageSize);
    }

    // ── 2. 分页渲染 + 加载更多 + 计数条 ──

    [AvaloniaFact]
    public void Paged_stub_renders_rows_and_load_more_appends_pages()
    {
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);

        // 首页：50 行 + 计数条（X=已加载，Y=过滤后总数）
        Assert.Equal(50, host.Vm.Rows.Count);
        Assert.Equal("显示 50 / 120 条", host.Vm.CountText);
        // 行 VM 携带软色底语义变体（P1 修复后颜色经 XAML 伪类 DynamicResource 呈现）
        Assert.Contains(host.Vm.Rows[0].ChipVariant, new[] { "blue", "green", "purple", "neutral" });
        // 默认按 Token 降序（对齐原型首项），平局键 SessionId 序数 → 首行可预期
        var expectedFirst = stub.AllSessions.OrderByDescending(r => r.TotalTokens).ThenBy(r => r.SessionId, StringComparer.Ordinal).First();
        Assert.Equal(expectedFirst.SessionId, host.Vm.Rows[0].SessionId);
        Assert.True(host.Vm.HasMore);

        // 加载更多：追加第 2、3 页，直到全部加载（HasMore 翻转）
        host.Vm.LoadMoreCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(100, host.Vm.Rows.Count);
        Assert.Equal(2, PageQueries(stub).Last().Page);

        host.Vm.LoadMoreCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(120, host.Vm.Rows.Count);
        Assert.Equal("显示 120 / 120 条", host.Vm.CountText);
        Assert.False(host.Vm.HasMore); // 加载更多按钮经 IsVisible={Binding HasMore} 隐藏
    }

    // ── 3. 搜索防抖 ──

    [AvaloniaFact]
    public void Search_debounce_coalesces_rapid_input_into_single_query()
    {
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);
        // 防抖窗口护栏（审查修复轮 Nit：原 Assert.Equal(常量, 300) 恒真无回归力）——
        // 300ms 是产品口径（D-008 Dev Log），测试锁健康区间：太小近似逐键重查、太大近于失效
        Assert.InRange(SessionsViewModel.SearchDebounceMilliseconds, 100, 1000);
        var initial = PageQueries(stub).Count;

        // 快速输入三次：重启式定时器不断重置，停顿前不发查询（headless 不驱动 DispatcherTimer 时钟）
        host.Vm.SearchText = "a";
        host.Vm.SearchText = "al";
        host.Vm.SearchText = "algo";
        Assert.Equal(initial, PageQueries(stub).Count);

        // 测试钩子直调提交（任务卡允许"虚拟时间或直接调命令"）→ 恰一次查询、以最终词为准
        host.Vm.CommitSearchForTests();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(initial + 1, PageQueries(stub).Count);
        Assert.Equal("algo", PageQueries(stub).Last().SearchTerm);
    }

    // ── 4. 排序映射 ──

    [AvaloniaFact]
    public void Sort_options_map_to_contract_fields()
    {
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);

        Assert.Equal(SessionSortField.TotalTokens, PageQueries(stub).Last().SortBy); // 默认对齐原型首项

        host.Vm.SelectedSort = host.Vm.SortOptions[2]; // 最近活动
        Assert.Equal(SessionSortField.LastSeenAt, PageQueries(stub).Last().SortBy);
        Assert.True(PageQueries(stub).Last().SortDescending);

        host.Vm.SelectedSort = host.Vm.SortOptions[1]; // 按成本排序
        Assert.Equal(SessionSortField.Cost, PageQueries(stub).Last().SortBy);
    }

    // ── 5/6/7. Agent 复选集 → 查询范围（右栏联动定案）──

    [AvaloniaFact]
    public void Checkbox_all_checked_maps_to_no_agent_filter()
    {
        var stub = new StubQueryService { Agents = CatalogAgents(), AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);

        Assert.Equal(3, host.Panel.AgentFilterCount);
        Assert.All(host.Panel.AgentFilters, a => Assert.True(a.IsChecked));
        // 全勾 = 不过滤（页级复选集不收窄；全局 Agent 亦未选）
        Assert.Null(PageQueries(stub).Last().Filter.Agent);
    }

    [AvaloniaFact]
    public void Checkbox_single_agent_maps_to_single_value_filter()
    {
        var stub = new StubQueryService { Agents = CatalogAgents(), AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);

        host.Panel.AgentFilters[0].IsChecked = false; // Claude Code
        host.Panel.AgentFilters[2].IsChecked = false; // Cursor
        host.Panel.ApplyQuickFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var query = PageQueries(stub).Last();
        Assert.Equal("Codex", query.Filter.Agent);
        Assert.Equal(SessionsViewModel.PageSize, query.PageSize); // 单值走服务分页
    }

    [AvaloniaFact]
    public void Checkbox_subset_merges_per_agent_and_sorts_client_side()
    {
        var stub = new StubQueryService { Agents = CatalogAgents(), AllSessions = HandcraftedSessions() };
        var host = CreateHost(stub);

        host.Panel.AgentFilters[2].IsChecked = false; // 只勾 Claude Code + Codex → 真子集
        host.Panel.ApplyQuickFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // 并集无法在单值 Agent 过滤里表达 → 逐已选 Agent 查询（PageSize=null 不分页）
        var queries = PageQueries(stub).TakeLast(2).ToList();
        Assert.All(queries, q => Assert.Null(q.PageSize));
        Assert.Equal(["Claude Code", "Codex"], queries.Select(q => q.Filter.Agent!).ToArray());

        // 合并后按查询排序口径（Token 降序 + SessionId 平局键）排序；4 行 < 页大小 → 首屏即全集
        Assert.Equal(new[] { "x1", "c1", "c2", "x2" }, host.Vm.Rows.Select(r => r.SessionId).ToArray());
        Assert.Equal("显示 4 / 4 条", host.Vm.CountText); // Cursor 行被排除，计数如实
        Assert.False(host.Vm.HasMore);
    }

    [AvaloniaFact]
    public void Global_agent_with_unloaded_catalog_falls_back_to_global_single()
    {
        // 审查修复轮 P2-1 启动竞态：全局已选 Agent 而 AgentCatalogService 尚未返回（复选组为空）时，
        // 原实现对空复选集取交集得 Empty → 页面假呈"没有匹配的会话"且选项到位后无重查。
        // 修复：目录未就绪时走 Single(全局) 兜底（与复选组未加载的 NoRestriction 分支同一原则）。
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        stub.HoldNextAgents(CatalogAgents()); // 目录查询挂起：页面首查发生时复选组必然未就绪
        var filters = new FilterStateService();
        filters.Agent = new AgentOption("Codex", "Codex");
        var host = CreateHost(stub, filters);

        var first = Assert.Single(PageQueries(stub));
        Assert.Equal("Codex", first.Filter.Agent); // 全局单选继续生效，而非 Empty 假空态
        Assert.Equal(40, host.Vm.Rows.Count); // fixture 120 行中 Codex 占 40（i%3==1），stub 按口径过滤
        Assert.Equal("显示 40 / 40 条", host.Vm.CountText); // Y = 过滤后总数（同真实服务口径）

        // 选项就绪后不强制重查：全选 ∩ 全局单选 = 全局单选，当前页面数据与交集语义一致
        stub.ReleaseHeldAgents();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, host.Panel.AgentFilterCount);
        Assert.Single(PageQueries(stub));
        Assert.Equal(40, host.Vm.Rows.Count);
    }

    [AvaloniaFact]
    public void Load_failure_keeps_last_good_rows_and_hides_count_and_empty_state()
    {
        // 审查修复轮 P2-2：加载失败不再清已加载行（与 LoadMore 追加失败不清数据纪律对齐），
        // 错误面板出现时空态与计数条隐藏（避免错误 + "没有匹配的会话" + "显示 0 / 0 条"三重矛盾态）。
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);
        Assert.Equal(50, host.Vm.Rows.Count);
        Assert.True(host.Vm.ShowCountLine);
        Assert.False(host.Vm.ShowEmptyState);

        stub.ThrowOnSessions = true;
        host.Filters.TimeRange = TimeRangeOption.Options[1];
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(host.Vm.LoadError);
        Assert.True(host.Vm.HasLoadError);
        Assert.Equal(50, host.Vm.Rows.Count); // 已加载行保留
        Assert.Equal(120, host.Vm.TotalCount);
        Assert.False(host.Vm.ShowCountLine);
        Assert.False(host.Vm.ShowEmptyState);

        // 恢复：筛选再变化重试成功 → 错误态清除、计数条回归
        stub.ThrowOnSessions = false;
        host.Vm.SearchText = "session";
        host.Vm.CommitSearchForTests();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(host.Vm.LoadError);
        Assert.True(host.Vm.ShowCountLine);
        Assert.Equal("显示 50 / 120 条", host.Vm.CountText);
    }

    [AvaloniaFact]
    public void Global_agent_intersecting_checkbox_subset_yields_honest_empty()
    {
        var stub = new StubQueryService { Agents = CatalogAgents(), AllSessions = HandcraftedSessions() };
        var host = CreateHost(stub);

        host.Filters.Agent = new AgentOption("Claude Code", "Claude Code");
        Dispatcher.UIThread.RunJobs();
        var queriesAfterGlobal = PageQueries(stub).Count;
        Assert.Equal("Claude Code", PageQueries(stub).Last().Filter.Agent);

        // 复选集不含全局选中的 Agent：交集为空 → 诚实空态，不发查询
        host.Panel.AgentFilters[0].IsChecked = false;
        host.Panel.ApplyQuickFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(queriesAfterGlobal, PageQueries(stub).Count);
        Assert.Empty(host.Vm.Rows);
        Assert.Equal("显示 0 / 0 条", host.Vm.CountText);
    }

    // ── 6. 未知成本列 ──

    [AvaloniaFact]
    public void Cost_column_shows_unknown_never_zero()
    {
        var sessions = GenerateSessions(12); // i%10==9 → 未知成本
        var stub = new StubQueryService { AllSessions = sessions };
        var host = CreateHost(stub);

        var unknown = host.Vm.Rows.First(r => r.SessionId == "session-000009");
        Assert.Equal("未知", unknown.CostText); // null（会话内全部成本未知）不折 0
        var known = host.Vm.Rows.First(r => r.SessionId == "session-000000");
        Assert.Equal("$0.50", known.CostText);
    }

    // ── 7. 虚拟化（≥1 万行 fixture）──

    [AvaloniaFact]
    public void Ten_thousand_rows_virtualize_to_viewport_containers()
    {
        // subset 合并模式（PageSize=null）让客户端真正持有约 2/3 的万行全集：
        // 首屏取页大小 50，经「加载更多」补齐全部 6,667 行（顺带覆盖子集模式的客户端分页路径），
        // 再断言 ListBox 只实例化视口内的行（真机滚动流畅性留人工目检，见任务卡 Verification）。
        var stub = new StubQueryService { Agents = CatalogAgents(), AllSessions = GenerateSessions(10_000) };
        var host = CreateHost(stub);

        host.Panel.AgentFilters[2].IsChecked = false; // Claude Code + Codex 两个 Agent 的并集
        host.Panel.ApplyQuickFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(50, host.Vm.Rows.Count);
        Assert.Equal(6_667, host.Vm.TotalCount);
        while (host.Vm.HasMore)
        {
            host.Vm.LoadMoreCommand.Execute(null);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(6_667, host.Vm.Rows.Count);
        Assert.Equal("显示 6,667 / 6,667 条", host.Vm.CountText);

        var window = new Window { Width = 1280, Height = 800, Content = new SessionsView { DataContext = host.Vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = window.GetVisualDescendants().OfType<ListBox>()
            .First(lb => lb.Classes.Contains("data-table")); // Q-002：原 session-table 类收编为共享 data-table
        var realized = list.GetRealizedContainers().ToList();
        Assert.InRange(realized.Count, 1, 200); // 远小于 6,667 —— 虚拟化生效
        Assert.True(realized.Count < host.Vm.Rows.Count);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 8. 序号防护 ──

    [AvaloniaFact]
    public void Stale_session_response_discarded_by_load_sequence_guard()
    {
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);

        // 第 2 次页面查询（切 30 天）在会话查询处挂起；第 3 次（切 90 天）同步走完
        stub.HoldNextSessions(new SessionListResult
        {
            Rows = GenerateSessions(3),
            TotalCount = 3,
            PageSize = SessionsViewModel.PageSize,
        });
        host.Filters.TimeRange = TimeRangeOption.Options[1]; // 30 天 → 挂起
        host.Filters.TimeRange = TimeRangeOption.Options[2]; // 90 天 → 完成（120 行全集）
        Assert.Equal("显示 50 / 120 条", host.Vm.CountText);

        stub.ReleaseHeldSessions(); // 旧响应放行：序号防护丢弃，不覆盖新选择
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("显示 50 / 120 条", host.Vm.CountText);
        Assert.NotEqual(3, host.Vm.TotalCount);
    }

    // ── 9. Dispose 退订 ──

    [AvaloniaFact]
    public void Dispose_stops_all_requery_paths()
    {
        var stub = new StubQueryService { Agents = CatalogAgents(), AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);
        var afterInit = PageQueries(stub).Count;

        host.Vm.Dispose();
        host.Filters.TimeRange = TimeRangeOption.Options[1];
        host.Filters.Agent = new AgentOption("Codex", "Codex");
        host.Panel.ClearQuickFiltersCommand.Execute(null); // 右栏提交事件不再触达页面
        host.Vm.SearchText = "algo";
        host.Vm.CommitSearchForTests(); // 防抖提交也不应再触发
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(afterInit, PageQueries(stub).Count);
    }

    // ── 10. Agent 清单目录（单一真相源 + 服务层缓存）──

    [AvaloniaFact]
    public void Agent_catalog_feeds_filter_state_and_panel_checkboxes()
    {
        var stub = new StubQueryService { Agents = CatalogAgents() };
        var catalog = new AgentCatalogService(stub);

        var options = catalog.GetAgentOptionsAsync().GetAwaiter().GetResult();
        // [所有 Agent, ...真实 Agent（token 降序）]
        Assert.Equal(new[] { AgentOption.AllId, "Claude Code", "Codex", "Cursor" }, options.Select(o => o.Id).ToArray());
        Assert.Equal("所有 Agent", options[0].Label);

        // 全局下拉注入（单一真相源）+ 属性通知
        var filters = new FilterStateService();
        var notified = false;
        filters.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(FilterStateService.AgentOptions);
        filters.UpdateAgentOptions(options);
        Assert.True(notified);
        Assert.Equal(options, filters.AgentOptions);

        // 右栏复选组共用同一份缓存（panel 走同一 catalog 实例）
        var panel = new SessionsPanelViewModel(
            new StubToast(), stub, catalog, filters);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "Claude Code", "Codex", "Cursor" }, panel.AgentFilters.Select(a => a.AgentId).ToArray());

        // 缓存：第二次请求不再打查询服务
        var before = stub.SessionQueries.Count;
        catalog.GetAgentOptionsAsync().GetAwaiter().GetResult();
        Assert.Equal(before, stub.SessionQueries.Count);
    }

    [AvaloniaFact]
    public void Agent_catalog_empty_or_failed_keeps_all_only()
    {
        // 无数据：诚实只留"所有 Agent"哨兵
        var emptyStub = new StubQueryService();
        var emptyOptions = new AgentCatalogService(emptyStub).GetAgentOptionsAsync().GetAwaiter().GetResult();
        Assert.Equal(new[] { AgentOption.All }, emptyOptions);

        // 加载失败：目录抛出，FilterStateService 保持初始 [所有 Agent]（外壳 catch 后不伪造占位清单）
        var failStub = new StubQueryService { ThrowOnAgents = true };
        var catalog = new AgentCatalogService(failStub);
        Assert.Throws<InvalidOperationException>(() => catalog.GetAgentOptionsAsync().GetAwaiter().GetResult());
        var filters = new FilterStateService();
        Assert.Equal(new[] { AgentOption.All }, filters.AgentOptions);

        // 外壳接线：MainWindowViewModel 构造拉取失败 → 顶部栏选项仍为 [所有 Agent]
        var shell = new MainWindowViewModel(
            filters,
            AppServices.Get<SettingsViewModel>(),
            new SessionsPanelViewModel(new StubToast(), failStub, catalog, filters),
            failStub,
            catalog,
            new StubToast(),
            new Core.Services.PricingEngine());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { AgentOption.All }, filters.AgentOptions);
    }

    // ── 11. 右栏最近会话 ──

    [AvaloniaFact]
    public void Recent_sessions_follow_global_filter_with_top_n()
    {
        var stub = new StubQueryService { AllSessions = GenerateSessions(8) };
        var filters = new FilterStateService();
        var host = CreateHost(stub, filters);

        // Top6（对齐原型 slice(0,6)），最近活动降序
        Assert.Equal(SessionsPanelViewModel.RecentTopN, host.Panel.RecentSessions.Count);
        Assert.Equal("session-000007", host.Panel.RecentSessions[0].SessionId);
        Assert.True(host.Panel.HasRecentSessions);

        var recentQuery = stub.SessionQueries[0];
        Assert.Equal(SessionSortField.LastSeenAt, recentQuery.SortBy);
        Assert.True(recentQuery.SortDescending);
        Assert.Equal(SessionsPanelViewModel.RecentTopN, recentQuery.PageSize);

        // 全局筛选切换 → 最近会话重查（映射窗口正确；右栏查询以页大小 = TopN 识别）
        filters.TimeRange = TimeRangeOption.Options[1];
        Dispatcher.UIThread.RunJobs();
        var latest = stub.SessionQueries.Where(q => q.PageSize == SessionsPanelViewModel.RecentTopN).Last();
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(UsageQueryFilter.FormatDay(today.AddDays(-29)), latest.Filter.FromDay);

        // 行展示：紧凑 token + 相对时间（fixture 最新行落在未来 27 分钟，时钟偏差防御收敛为「刚刚」）
        Assert.EndsWith(" tokens", host.Panel.RecentSessions[0].TokensText);
        Assert.Equal("刚刚", host.Panel.RecentSessions[0].TimeText);
    }

    // ── 12. 右栏提交点（应用/清除）──

    [AvaloniaFact]
    public void Panel_apply_and_clear_commit_and_toast()
    {
        var stub = new StubQueryService
        {
            Agents = CatalogAgents(),
            Models = CatalogModels(),
            Projects = CatalogProjects(),
            AllSessions = GenerateSessions(120),
        };
        var host = CreateHost(stub);
        var afterInit = PageQueries(stub).Count;

        // 改动暂存（勾选/下拉不直接触发重查）
        host.Panel.AgentFilters[0].IsChecked = false;
        host.Panel.ModelFilter = host.Panel.ModelFilterOptions[^1];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(afterInit, PageQueries(stub).Count);

        // 应用筛选 → 提交 + toast（原型 applyFilters 同款文案）；剩 2 个勾选 → 真子集逐 Agent 查询
        host.Panel.ApplyQuickFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(afterInit + 2, PageQueries(stub).Count);
        var subsetQueries = PageQueries(stub).TakeLast(2).ToList();
        Assert.Equal(new[] { "Codex", "Cursor" }, subsetQueries.Select(q => q.Filter.Agent!).ToArray());
        Assert.All(subsetQueries, q => Assert.Null(q.PageSize)); // 并集走不分页合并
        Assert.Contains(host.Toast.Messages, m => m.StartsWith("筛选已应用："));

        // 清除 → 复位（全勾 + 全部模型/项目）+ 提交 + toast；全勾 → 单查询不过滤
        host.Panel.ClearQuickFiltersCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.All(host.Panel.AgentFilters, a => Assert.True(a.IsChecked));
        Assert.Equal(ModelFilterOption.All, host.Panel.ModelFilter);
        Assert.Equal(ProjectFilterOption.All, host.Panel.ProjectFilter);
        Assert.Contains("筛选条件已清除", host.Toast.Messages);
        Assert.Null(PageQueries(stub).Last().Filter.Agent);
        Assert.Equal(SessionsViewModel.PageSize, PageQueries(stub).Last().PageSize);
        Assert.Equal(afterInit + 3, PageQueries(stub).Count);
    }

    // ── 13. 下钻 toast 占位（页面行 + 右栏行）──

    [AvaloniaFact]
    public void Session_row_click_shows_planned_details_toast()
    {
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);

        host.Vm.SelectedSession = host.Vm.Rows[0];
        var message = Assert.Single(host.Toast.Messages);
        Assert.Contains(host.Vm.Rows[0].SessionId, message);
        Assert.Contains("规划中", message);
        Assert.Null(host.Vm.SelectedSession); // 选中态复位，同一行可再次点击

        host.Panel.SelectedRecentSession = null; // 无最近会话（stub 空目录下初始即无）
        host.Panel.RecentSessions.Clear(); // 直接驱动一行用于点击断言
        host.Panel.RecentSessions.Add(new RecentSessionRowViewModel(
            new SessionListRow { SessionId = "s-x", Agent = "Codex", LastSeenAt = DateTimeOffset.Now, TotalTokens = 1_234_567 }));
        host.Panel.SelectedRecentSession = host.Panel.RecentSessions[0];
        Assert.Equal(2, host.Toast.Messages.Count);
        Assert.Contains("s-x", host.Toast.Messages[1]);
        Assert.Null(host.Panel.SelectedRecentSession);
    }

    // ── 14. 相对时间格式 ──

    [AvaloniaTheory]
    [InlineData(0, 30, "刚刚")]
    [InlineData(5, 0, "5 分钟前")]
    [InlineData(0, 0, "刚刚")]
    public void Relative_time_formats_buckets(int minutes, int seconds, string expected)
    {
        var now = DateTimeOffset.Now;
        var stamp = now.AddMinutes(-minutes).AddSeconds(-seconds);
        Assert.Equal(expected, SessionsViewModel.FormatRelativeTime(stamp, now));
    }

    [AvaloniaFact]
    public void Relative_time_falls_back_to_date_beyond_a_month()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var stamp = now.AddDays(-40).ToOffset(TimeSpan.Zero);
        Assert.Equal("2026-09-03", SessionsViewModel.FormatRelativeTime(stamp, now));
    }

    // ── 15. 真实查询服务冒烟（环境无关：同口径直查对账）──

    [AvaloniaFact]
    public void Real_query_service_smoke_binds_same_sessions_as_direct_call()
    {
        var query = AppServices.Get<IUsageQueryService>();
        var filters = new FilterStateService();
        var panel = new SessionsPanelViewModel(
            AppServices.Get<IToastService>(), query, new AgentCatalogService(query), filters);
        var vm = new SessionsViewModel(ShellPages.For(PageId.Sessions), filters, query, panel, AppServices.Get<IToastService>());

        // 同口径直查（默认筛选 = 最近 7 天 · 所有 Agent · 按 Token 降序 · 首页 50）
        var today = DateOnly.FromDateTime(DateTime.Today);
        var expected = Task.Run(() => query.GetSessionsAsync(new SessionListQuery
        {
            Filter = new UsageQueryFilter
            {
                FromDay = UsageQueryFilter.FormatDay(today.AddDays(-6)),
                ToDay = UsageQueryFilter.FormatDay(today),
            },
            SortBy = SessionSortField.TotalTokens,
            SortDescending = true,
            Page = 1,
            PageSize = SessionsViewModel.PageSize,
        })).GetAwaiter().GetResult();

        // 数据面查询已移到线程池执行（UsageQueryService 线程模型），VM 续体回 UI 队列：
        // 泵至加载完成再对账（空库时 TotalCount 恒 0 无法区分 pending，改按行内容等待——
        // 加载完成时 TotalCount 必与同口径直查相等）。
        DispatcherPump.PumpUntil(
            () => vm.TotalCount == expected.TotalCount
                && (expected.Rows.Count == 0 || vm.Rows.Count > 0));

        // 计数条 Y 与首行 token 同口径一致（本机有无 facts 皆可对账）
        Assert.Equal(expected.TotalCount, vm.TotalCount);
        if (expected.Rows.Count > 0)
        {
            Assert.Equal(
                expected.Rows[0].TotalTokens.ToString("N0", CultureInfo.InvariantCulture),
                vm.Rows[0].TokensText);
        }

        // 真实数据整页可渲染（不崩；数据有无随本机 facts 而定）
        var window = new Window { Width = 1180, Height = 860, Content = new SessionsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ── 16. 明暗截图留档（含右栏）──

    [AvaloniaFact]
    public void Sessions_page_renders_light_and_dark_screenshots_for_archive()
    {
        // 整窗外壳截图（含右栏最近会话 + 快速筛选）：真实 DI 数据，条目随本机 facts 而定。
        var dir = Environment.GetEnvironmentVariable("CCUSAGE_SESSIONS_PREVIEW_DIR");

        foreach (var (variant, fileName) in new (ThemeVariant, string)[]
                 {
                     (ThemeVariant.Light, "sessions-light.png"),
                     (ThemeVariant.Dark, "sessions-dark.png"),
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
            shellVm.SelectedNavigation = shellVm.NavigationItems[1]; // 会话明细
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<SessionsViewModel>(shellVm.CurrentPage);

            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, fileName);
                frame!.Save(path, new PngBitmapEncoderOptions());
                Assert.True(File.Exists(path), $"截图未生成：{path}");
            }

            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
