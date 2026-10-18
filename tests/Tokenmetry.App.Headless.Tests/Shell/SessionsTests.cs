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
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// D-008 会话明细页的自动化验证（headless，真实 App.axaml + Semi）：
/// 1. 页面状态（搜索/排序）+ 顶栏全局四维筛选（时间/Agent/Model/Project）→ I-029 SessionListQuery 的映射对账
///    （2026-10-18 右栏撤除定案：原右栏页级快速筛选并入顶栏全局筛选，复选集/SubsetMerge 机制随撤删除）；
/// 2. 搜索防抖（重启式 300ms；headless 不驱动 DispatcherTimer 时钟，经 CommitSearchForTests 直调提交）；
/// 3. 「加载更多」分页契约（I-029 Page/PageSize，页大小 50）与计数条 "显示 X / Y 条"；
/// 4. 未知成本列四态呈现（null ≠ 0）；Agent/模型/项目清单目录（单一真相源 + 服务层缓存）；
/// 5. ≥1 万行 fixture 虚拟化（realized container 数远小于总行数；真机滚动流畅性留人工目检）；
/// 6. 序号防护（挂起的旧响应不覆盖新选择）与 Dispose 退订（全局筛选 + 防抖定时器）；
/// 7. 真实查询服务冒烟（同口径直查对账，环境无关）；明暗两套离屏截图留档（docs/design/sessions-*.png）。
/// 分工：会话查询的过滤/排序/分页 SQL 对账在 UsageQueryServiceTests（I-029）已完成，
/// 本类做"页面状态 → 查询参数 → 行展示"的页面层对账（stub 最小复刻同口径，只承担数据回传）。
/// </summary>
public class SessionsTests
{
    /// <summary>截图留档目录（验证时传 docs/design）；未设置时跳过留档，只断言可渲染。</summary>
    private static string? ArchiveDir => Environment.GetEnvironmentVariable("TOKENMETRY_SESSIONS_PREVIEW_DIR");

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

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default)
        {
            SessionQueries.Add(query);
            if (ThrowOnSessions)
            {
                throw new InvalidOperationException("sessions offline");
            }

            if (_heldSessions is { } held)
            {
                _heldSessions = null;
                return held.Task;
            }

            return Task.FromResult(DefaultSessions(query));
        }

        /// <summary>与 UsageQueryService.GetSessionsAsync 同口径的最小复刻（仅承担数据回传；
        /// 服务行为的逐项对账在 Tokenmetry.Infrastructure.Tests UsageQueryServiceTests）。</summary>
        private SessionListResult DefaultSessions(SessionListQuery query)
        {
            var rows = AllSessions.AsEnumerable();
            if (query.Filter.Agent is { } agent)
            {
                rows = rows.Where(r => string.Equals(r.Agent, agent, StringComparison.Ordinal));
            }

            if (query.Filter.Project is { } filterProject)
            {
                rows = rows.Where(r => string.Equals(r.Project, filterProject, StringComparison.Ordinal));
            }

            if (query.Filter.Model is { } filterModel)
            {
                rows = rows.Where(r => string.Equals(r.DominantModel, filterModel, StringComparison.Ordinal));
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

            return Task.FromResult(Agents);
        }

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Models);

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Projects);

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

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

    private sealed record TestHost(
        SessionsViewModel Vm,
        StubQueryService Stub,
        StubToast Toast,
        FilterStateService Filters);

    private static TestHost CreateHost(StubQueryService? stub = null, FilterStateService? filters = null)
    {
        stub ??= new StubQueryService();
        filters ??= new FilterStateService();
        var toast = new StubToast();
        var vm = new SessionsViewModel(ShellPages.For(PageId.Sessions), filters, stub, toast);
        Dispatcher.UIThread.RunJobs(); // 页面首查完成
        return new TestHost(vm, stub, toast, filters);
    }

    /// <summary>页面查询记账（右栏撤除后全部 SessionQueries 均为页面查询）。</summary>
    private static List<SessionListQuery> PageQueries(StubQueryService stub) =>
        stub.SessionQueries.ToList();

    // ── 1. 页面状态 → I-029 查询映射（组合筛选）──

    [AvaloniaFact]
    public void Combined_filters_map_to_session_list_query()
    {
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);

        host.Filters.TimeRange = TimeRangeOption.Options[2]; // 最近 30 天
        host.Filters.Agent = new AgentOption("Claude Code", "Claude Code");
        host.Filters.Model = new ModelFilterOption("model-b");
        host.Filters.Project = new ProjectFilterOption("/tmp/beta");
        host.Vm.SearchText = "algo";
        host.Vm.CommitSearchForTests();
        host.Vm.SelectedSort = host.Vm.SortOptions[1]; // 按成本排序
        Dispatcher.UIThread.RunJobs();

        var query = PageQueries(stub).Last();
        Assert.Equal("algo", query.SearchTerm);
        Assert.Equal(SessionSortField.Cost, query.SortBy);
        Assert.True(query.SortDescending);
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(UsageQueryFilter.FormatDay(today.AddDays(-29)), query.Filter.FromDay);
        Assert.Equal(UsageQueryFilter.FormatDay(today), query.Filter.ToDay);
        // 四维全部进 Filter（右栏撤除定案：Model/Project 不再走 SessionListQuery 页级字段）
        Assert.Equal("Claude Code", query.Filter.Agent);
        Assert.Equal("model-b", query.Filter.Model);
        Assert.Equal("/tmp/beta", query.Filter.Project);
        Assert.Equal(1, query.Page);
        Assert.Equal(SessionsViewModel.PageSize, query.PageSize);
    }

    [AvaloniaFact]
    public void Build_session_query_carries_all_four_global_dimensions()
    {
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);

        host.Filters.Agent = new AgentOption("Claude Code");
        host.Filters.Model = new ModelFilterOption("model-a");
        host.Filters.Project = new ProjectFilterOption("/tmp/alpha");
        Dispatcher.UIThread.RunJobs();

        var query = host.Vm.BuildSessionQuery(GlobalFilterMapper.Build(host.Filters), page: 1);
        Assert.Equal("Claude Code", query.Filter.Agent);
        Assert.Equal("model-a", query.Filter.Model);
        Assert.Equal("/tmp/alpha", query.Filter.Project);
        // 会话契约页级 Model/Project 字段已随右栏撤除停用（恒 null，不再双口径）
        Assert.Null(query.Model);
        Assert.Null(query.Project);
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
        // 行 VM 携带品牌 logo 变体名（图形/颜色经 XAML agent-* 伪类 DynamicResource 呈现）
        Assert.Contains(host.Vm.Rows[0].LogoVendor, Tokenmetry.App.ViewModels.AgentVisualMap.Vendors);
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

    // ── 5. 顶栏全局筛选驱动重查（右栏撤除定案：页级快速筛选并入顶栏全局四维）──

    [AvaloniaFact]
    public void Global_agent_filter_drives_page_reload_with_single_value()
    {
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);

        host.Filters.Agent = new AgentOption("Codex", "Codex");
        Dispatcher.UIThread.RunJobs();

        var query = PageQueries(stub).Last();
        Assert.Equal("Codex", query.Filter.Agent);
        Assert.Equal(SessionsViewModel.PageSize, query.PageSize); // 全局单选走服务分页
        // stub 同口径过滤：fixture 120 行中 Codex 占 40（i%3==1）
        Assert.Equal(40, host.Vm.TotalCount);
    }

    [AvaloniaFact]
    public void Global_model_and_project_filters_drive_page_reload()
    {
        var stub = new StubQueryService { AllSessions = GenerateSessions(120) };
        var host = CreateHost(stub);
        var afterInit = PageQueries(stub).Count;

        // 两个维度先后各触发一次重查（FilterStateService.AffectsQueries 口径）
        host.Filters.Model = new ModelFilterOption("model-b");
        host.Filters.Project = new ProjectFilterOption("/tmp/project-1");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(afterInit + 2, PageQueries(stub).Count);
        var query = PageQueries(stub).Last();
        Assert.Equal("model-b", query.Filter.Model);
        Assert.Equal("/tmp/project-1", query.Filter.Project);
        // stub 同口径过滤：i%2==1 ∧ i%5==1（≡1 mod 10）→ 12 行
        Assert.Equal(12, host.Vm.TotalCount);
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
        host.Filters.TimeRange = TimeRangeOption.Options[2];
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

    // ── 6b. 行元数据映射（右栏撤除定案：项目名升主文本，会话标识降副文本 + tooltip）──

    [AvaloniaFact]
    public void Session_row_main_text_prefers_project_display_name()
    {
        var vm = new SessionRowViewModel(new SessionListRow
        {
            SessionId = "abc-123",
            Agent = "Claude Code",
            Project = "/work/alpha",
            ProjectDisplayName = "alpha",
            DominantModel = "claude-sonnet-4",
        });

        Assert.Equal("alpha", vm.TitleText); // 项目名升主文本（"这是哪个项目的活"，比 UUID 有信息量）
        Assert.Equal("abc-123", vm.SessionIdText); // 会话标识降副文本（tooltip 可复制完整 ID）
    }

    [AvaloniaFact]
    public void Session_row_falls_back_to_raw_project_then_session_id()
    {
        var noDisplay = new SessionRowViewModel(new SessionListRow
        {
            SessionId = "sid-1", Agent = "Codex", Project = "/work/beta", ProjectDisplayName = "",
        });
        Assert.Equal("/work/beta", noDisplay.TitleText); // 无展示名回退原始路径

        var noProject = new SessionRowViewModel(new SessionListRow
        {
            SessionId = "sid-2", Agent = "Codex", Project = "", ProjectDisplayName = "",
        });
        Assert.Equal("sid-2", noProject.TitleText); // 项目全空回落会话标识，不显示空行
    }

    // ── 7. 虚拟化（≥1 万行 fixture）──

    [AvaloniaFact]
    public void Ten_thousand_rows_virtualize_to_viewport_containers()
    {
        // 全局无过滤的全集分页：首屏取页大小 50，经「加载更多」补齐全部 10,000 行，
        // 再断言 ListBox 只实例化视口内的行（真机滚动流畅性留人工目检，见任务卡 Verification）。
        var stub = new StubQueryService { AllSessions = GenerateSessions(10_000) };
        var host = CreateHost(stub);

        Assert.Equal(50, host.Vm.Rows.Count);
        Assert.Equal(10_000, host.Vm.TotalCount);
        while (host.Vm.HasMore)
        {
            host.Vm.LoadMoreCommand.Execute(null);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(10_000, host.Vm.Rows.Count);
        Assert.Equal("显示 10,000 / 10,000 条", host.Vm.CountText);

        var window = new Window { Width = 1280, Height = 800, Content = new SessionsView { DataContext = host.Vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = window.GetVisualDescendants().OfType<ListBox>()
            .First(lb => lb.Classes.Contains("data-table")); // Q-002：原 session-table 类收编为共享 data-table
        var realized = list.GetRealizedContainers().ToList();
        Assert.InRange(realized.Count, 1, 200); // 远小于 10,000 —— 虚拟化生效
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
        host.Filters.TimeRange = TimeRangeOption.Options[2]; // 30 天 → 挂起
        host.Filters.TimeRange = TimeRangeOption.Options[3]; // 90 天 → 完成（120 行全集）
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
        host.Filters.TimeRange = TimeRangeOption.Options[2];
        host.Filters.Agent = new AgentOption("Codex", "Codex");
        host.Vm.SearchText = "algo";
        host.Vm.CommitSearchForTests(); // 防抖提交也不应再触发
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(afterInit, PageQueries(stub).Count);
    }

    // ── 10. Agent 清单目录（单一真相源 + 服务层缓存）──

    [AvaloniaFact]
    public void Agent_catalog_feeds_global_filter_options()
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
            AppServices.Get<DataPricingViewModel>(),
            failStub,
            catalog,
            new StubToast(),
            new Core.Services.PricingEngine());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { AgentOption.All }, filters.AgentOptions);
    }

    // ── 13. 下钻 toast 占位（页面行）──

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
        var vm = new SessionsViewModel(ShellPages.For(PageId.Sessions), filters, query, AppServices.Get<IToastService>());

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

    // ── 16. 明暗截图留档（两栏外壳）──

    [AvaloniaFact]
    public void Sessions_page_renders_light_and_dark_screenshots_for_archive()
    {
        // 整窗外壳截图（两栏外壳 + 顶栏四维筛选）：真实 DI 数据，条目随本机 facts 而定。
        var dir = Environment.GetEnvironmentVariable("TOKENMETRY_SESSIONS_PREVIEW_DIR");

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
