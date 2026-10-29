using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// 会话/模型两页表格列的几何回归（P0-1 Agent 列叠字 + P0-2 表头硬裁 + P1-11 数字口径，2026-10-29）。
/// 十种 UI 语言全跑：德/法/俄的表头是最长的（"Letzte Aktivität" / "Lecture du cache" / "Последняя активность"），
/// 只核 zh/en 会把"按中文长度定的列宽"放过去。数值用例喂 9 位数（"14,260,000"，对账口径 N0 的年量级），
/// 千分位全量必须整串可读——明细表精确、汇总面紧凑是本计划定稿的数字政策。
/// 窗口宽度取主内容区默认宽度（1280 外壳 − 184 侧栏 = 1096）；最小窗口（776）另有用例，
/// 只核定宽列与单元格溢出——星宽列在窄窗被压缩属既有挤压行为。
/// </summary>
public class TableColumnFitTests
{
    /// <summary>主内容区默认宽度（MainWindow 默认 1280 宽 − 侧栏 184）。</summary>
    private const double DefaultPageWidth = 1096;

    /// <summary>最小窗口下的主内容区宽度（MinWidth 960 − 侧栏 184）。</summary>
    private const double MinPageWidth = 776;

    private static readonly AppLanguage[] AllLanguages =
    [
        AppLanguage.SimplifiedChinese,
        AppLanguage.TraditionalChinese,
        AppLanguage.English,
        AppLanguage.Japanese,
        AppLanguage.Korean,
        AppLanguage.BrazilianPortuguese,
        AppLanguage.Russian,
        AppLanguage.Spanish,
        AppLanguage.German,
        AppLanguage.French,
    ];

    public static TheoryData<AppLanguage> Languages
    {
        get
        {
            var data = new TheoryData<AppLanguage>();
            foreach (var language in AllLanguages)
            {
                data.Add(language);
            }

            return data;
        }
    }

    // ── stub 查询服务（只承担数据回传；聚合口径对账在 UsageQueryServiceTests）──

    private sealed class StubQueryService : IUsageQueryService
    {
        public IReadOnlyList<DimensionUsageRow> Models { get; set; } = [];

        public SessionListResult Sessions { get; set; } = new();

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DailyUsagePoint>>([]);

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new UsageKpiResult());

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(Models);

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            Task.FromResult(Sessions);

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult { Hours = [] });
    }

    private sealed class StubToast : IToastService
    {
        public void Show(string message, Visual? host = null)
        {
        }
    }

    /// <summary>最宽数值 fixture：9 位 token（"14,260,000"）+ 四位数成本（"$1234.56"）。</summary>
    private static List<DimensionUsageRow> WideModels() =>
    [
        new()
        {
            Key = "claude-sonnet-4",
            InputTokens = 14_260_000,
            OutputTokens = 12_345_678,
            CacheReadTokens = 9_876_543,
            TotalTokens = 14_260_000,
            SessionCount = 42,
            Cost = new AggregatedCost(1234.56m, false),
            TokenShare = 0.42m,
            DominantAgent = "Claude Code",
            AgentCount = 1,
            ProviderCount = 1,
            Provider = "anthropic",
        },
    ];

    /// <summary>最长 Agent 名 fixture（"Claude Code"）+ 最宽 token/成本值。</summary>
    private static SessionListResult WideSessions() => new()
    {
        TotalCount = 1,
        Rows =
        [
            new SessionListRow
            {
                SessionId = "0123456789abcdef0123456789abcdef",
                Agent = "Claude Code",
                Project = "/Users/someone/code/very-long-project-name",
                ProjectDisplayName = "very-long-project-name",
                DominantModel = "claude-sonnet-4-5-20250929",
                FirstSeenAt = DateTimeOffset.Now.AddHours(-3),
                LastSeenAt = DateTimeOffset.Now.AddMinutes(-2),
                InputTokens = 14_260_000,
                OutputTokens = 123_456,
                TotalTokens = 1_234_567_890, // 10 位千分位（"1,234,567,890" = 71.9px）是 Token 列的余量判据
                CostUsd = 1234.56m,
            },
        ],
    };

    private static Window ShowPage(Control page, double width)
    {
        var window = new Window { Width = width, Height = 800, Content = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void WithLanguage(AppLanguage language, Action action)
    {
        var original = Translations.Instance.CurrentLanguage;
        Translations.Instance.SetLanguage(language);
        try
        {
            action();
        }
        finally
        {
            Translations.Instance.SetLanguage(original);
        }
    }

    // ── 会话明细页 ──

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public void Sessions_table_headers_and_numbers_fit_at_default_width(AppLanguage language) => WithLanguage(language, () =>
    {
        var filters = new FilterStateService();
        var vm = new SessionsViewModel(
            ShellPages.For(PageId.Sessions),
            filters,
            new StubQueryService { Sessions = WideSessions() },
            new StubToast());
        Dispatcher.UIThread.RunJobs();

        var window = ShowPage(new SessionsView { DataContext = vm }, DefaultPageWidth);
        try
        {
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindClippedHeaders(window, fixedColumnsOnly: false), "会话页表头被裁");
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindClippedNumbers(window), "会话页数值被裁");
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindOverflowingCells(window), "会话页单元格内容溢出（叠字）");
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindTruncatedAgentNames(window), "会话页 Agent 名被截断");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        }
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public void Sessions_table_cells_stay_inside_their_column_at_minimum_width(AppLanguage language) => WithLanguage(language, () =>
    {
        // 最小窗口：星宽列被压缩属既有挤压行为（名字列有 TextTrimming），但定宽列的表头/数值与
        // Agent 单元格都不得越界——P0-1 的叠字在窄窗口下最容易复发。
        var filters = new FilterStateService();
        var vm = new SessionsViewModel(
            ShellPages.For(PageId.Sessions),
            filters,
            new StubQueryService { Sessions = WideSessions() },
            new StubToast());
        Dispatcher.UIThread.RunJobs();

        var window = ShowPage(new SessionsView { DataContext = vm }, MinPageWidth);
        try
        {
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindClippedHeaders(window, fixedColumnsOnly: true), "会话页定宽表头被裁（最小窗口）");
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindClippedNumbers(window), "会话页数值被裁（最小窗口）");
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindOverflowingCells(window), "会话页单元格内容溢出（最小窗口）");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        }
    });

    // ── 模型统计页 ──

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public void Models_table_headers_and_numbers_fit_at_default_width(AppLanguage language) => WithLanguage(language, () =>
    {
        var vm = new ModelsViewModel(
            ShellPages.For(PageId.Models),
            new FilterStateService(),
            new StubQueryService { Models = WideModels() });
        Dispatcher.UIThread.RunJobs();

        var window = ShowPage(new ModelsView { DataContext = vm }, DefaultPageWidth);
        try
        {
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindClippedHeaders(window, fixedColumnsOnly: false), "模型页表头被裁");
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindClippedNumbers(window), "模型页数值被裁");
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindOverflowingCells(window), "模型页单元格内容溢出（叠字）");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        }
    });

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public void Models_table_numbers_fit_at_minimum_width(AppLanguage language) => WithLanguage(language, () =>
    {
        var vm = new ModelsViewModel(
            ShellPages.For(PageId.Models),
            new FilterStateService(),
            new StubQueryService { Models = WideModels() });
        Dispatcher.UIThread.RunJobs();

        var window = ShowPage(new ModelsView { DataContext = vm }, MinPageWidth);
        try
        {
            // 最小窗口下模型名/关联 Agent 星宽列被压到很窄属既有挤压行为；判据是数字列（定宽）
            // 不得裁切、单元格不得越界（Review Focus 2：后者必须回退列间距，而不是回退 N0）。
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindClippedNumbers(window), "模型页数值被裁（最小窗口）");
            TableLayoutAssert.AssertNone(TableLayoutAssert.FindOverflowingCells(window), "模型页单元格内容溢出（最小窗口）");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        }
    });
}
