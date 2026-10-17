using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Services;
using Tokenmetry.App.ViewModels;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;

namespace Tokenmetry.App.Views;

/// <summary>
/// [I-027] dev-only 样式预览页（Q-002 定案保留）：设计令牌与共享样式的目检载体，
/// 支持就地切换明暗主题与弹出 toast；[D-004/D-005] 图表控件演示区
/// （ChartPreviewViewModel：fixture ↔ 真实 IUsageQueryService 开关）；[D-015] 状态栏快捷
/// 信息窗三态演示（stub 查询服务 + 速率/异常注入，真数据按钮切真实管线）。
/// 定位：截图/测试基建 + 开发预览——<b>无生产入口</b>（不在外壳导航与路由中，唯一打开方式
/// 为 TOKENMETRY_PREVIEW_WINDOW=1 的开发预览窗）；真机目检与明暗截图留档（TOKENMETRY_STYLE_PREVIEW_DIR）
/// 继续依赖本页。生产视图不再依赖它时可删。
/// </summary>
public partial class StylePreviewView : UserControl
{
    public StylePreviewView()
    {
        InitializeComponent();
        DataContext = new ChartPreviewViewModel(AppServices.Get<IUsageQueryService>());
        SetPopoverDemo(BuildFixtureVm());
    }

    private void SetPopoverDemo(StatusPopoverViewModel vm)
    {
        (PopoverDemo.DataContext as IDisposable)?.Dispose();
        PopoverDemo.DataContext = vm;
    }

    // ── D-015 三态演示（stub 数据 + 注入；时间戳合成满足速率窗口 45–90s）──

    private static readonly DateTimeOffset DemoBaselineAt = DateTimeOffset.Now - TimeSpan.FromSeconds(120);

    private static StatusPopoverViewModel BuildFixtureVm() => new(new FixtureQueryService(), null, null);

    private void OnPopoverIdleClick(object? sender, RoutedEventArgs e)
    {
        var vm = BuildFixtureVm();
        vm.HandleTodayTokensRound(42381, DemoBaselineAt); // 仅基线：Idle（首轮无速率）
        SetPopoverDemo(vm);
    }

    private void OnPopoverActiveClick(object? sender, RoutedEventArgs e)
    {
        var vm = BuildFixtureVm();
        vm.HandleTodayTokensRound(42381, DemoBaselineAt);
        vm.HandleTodayTokensRound(45581, DemoBaselineAt + TimeSpan.FromSeconds(60)); // +3200/60s → 3.2K/min
        SetPopoverDemo(vm);
    }

    private void OnPopoverWarnClick(object? sender, RoutedEventArgs e)
    {
        var vm = BuildFixtureVm();
        vm.HandleTodayTokensRound(42381, DemoBaselineAt);
        vm.NoteAnomaly(
            Localization.Translations.Instance.AnomalyToastTitle,
            "Claude Code · 当前速度约为平时的 4.8×");
        SetPopoverDemo(vm);
    }

    private void OnPopoverNoDataClick(object? sender, RoutedEventArgs e)
    {
        var vm = new StatusPopoverViewModel(new EmptyQueryService(), null, null);
        _ = vm.ReloadAsync(); // 全零 → KPI "—" + 诚实空态
        SetPopoverDemo(vm);
    }

    private void OnPopoverRealDataClick(object? sender, RoutedEventArgs e)
    {
        // 真实管线（IUsageQueryService 今日窗口 + DashboardViewModel 回写订阅）：与生产弹层同源
        var vm = new StatusPopoverViewModel(
            AppServices.Get<IUsageQueryService>(),
            AppServices.Get<DashboardViewModel>(),
            AppServices.Get<DateRangeService>());
        _ = vm.ReloadAsync();
        SetPopoverDemo(vm);
    }

    private void OnLightThemeClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IThemeService>().Apply("light");

    private void OnDarkThemeClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IThemeService>().Apply("dark");

    private void OnSystemThemeClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IThemeService>().Apply("system");

    private void OnToastClick(object? sender, RoutedEventArgs e) =>
        AppServices.Get<IToastService>().Show("Toast 演示：I-027 共享反馈样式", this);

    private void OnReloadChartsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ChartPreviewViewModel vm)
        {
            _ = vm.LoadAsync();
        }
    }

    /// <summary>原型同款 fixture（docs/design/tokenmetry_statusbar_prototype.html 的数值），
    /// 供三态演示与明暗目检；无 dashboard 依赖（速率/异常走注入）。</summary>
    private sealed class FixtureQueryService : IUsageQueryService
    {
        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new UsageKpiResult
            {
                TotalTokens = 42381,
                Cost = new AggregatedCost(12.46m, false),
                SessionCount = 18,
                CacheReadShare = 0.68m,
            });

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DailyUsagePoint>>(
            [
                new DailyUsagePoint
                {
                    Day = DateTime.Today.ToString("yyyy-MM-dd"),
                    InputTokens = 26843,
                    OutputTokens = 13124,
                    CacheReadTokens = 2414,
                    TotalTokens = 42381,
                    HasData = true,
                },
            ]);

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(
            [
                Row("Claude Code", 20624),
                Row("Codex", 13582),
                Row("Cursor", 5261),
                Row("GitHub Copilot", 2914),
            ]);

        private static DimensionUsageRow Row(string key, long tokens) => new()
        {
            Key = key,
            TotalTokens = tokens,
            TokenShare = tokens / 42381m,
        };

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) =>
            Task.FromResult(new SessionListResult
            {
                Rows =
                [
                    new SessionListRow
                    {
                        SessionId = "demo-1",
                        Agent = "Claude Code",
                        LastSeenAt = DateTimeOffset.Now,
                        TotalTokens = 8400,
                    },
                    new SessionListRow
                    {
                        SessionId = "demo-2",
                        Agent = "Codex",
                        LastSeenAt = DateTimeOffset.Now.AddMinutes(-12),
                        TotalTokens = 3100,
                    },
                ],
            });

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult());
    }

    /// <summary>全空 fixture（诚实空态演示）。</summary>
    private sealed class EmptyQueryService : IUsageQueryService
    {
        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult(new UsageKpiResult());
        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DailyUsagePoint>>([]);
        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);
        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);
        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DimensionUsageRow>>([]);
        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default) => Task.FromResult(new SessionListResult());
        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) => Task.FromResult(new HourlyDistributionResult());
    }
}
