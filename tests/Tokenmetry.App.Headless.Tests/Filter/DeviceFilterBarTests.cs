namespace Tokenmetry.App.Headless.Tests.Filter;

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
using Avalonia.VisualTree;
using Tokenmetry.App.DependencyInjection;
using Tokenmetry.App.Services;
using Tokenmetry.App.Update;
using Tokenmetry.App.ViewModels;
using Tokenmetry.App.Views;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Core.Sync;
using Xunit;

/// <summary>
/// 顶栏设备下拉（relay 计划 Task 13 / 设计 U5）：仅同步开启时渲染（关 = 不打扰单机用户），
/// 选中设备经 AffectsQueries 触发各页重查（查询收口在 GlobalFilterMapper，Task 11）。
/// </summary>
public sealed class DeviceFilterBarTests
{
    private sealed class StubToast : IToastService
    {
        public void Show(string message, Visual? host = null)
        {
        }
    }

    /// <summary>会话查询记账桩（其余成员与本用例无关，走空实现）。</summary>
    private sealed class RecordingQueryService : IUsageQueryService
    {
        public List<SessionListQuery> SessionQueries { get; } = new();

        public Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DailyUsagePoint>>(new List<DailyUsagePoint>());

        public Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new UsageKpiResult());

        public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(new List<DimensionUsageRow>());

        public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(new List<DimensionUsageRow>());

        public Task<IReadOnlyList<string>> GetRawModelNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(new List<string>());

        public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DimensionUsageRow>>(new List<DimensionUsageRow>());

        public Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default)
        {
            SessionQueries.Add(query);
            return Task.FromResult(new SessionListResult
            {
                Rows = new List<SessionListRow>(),
                TotalCount = 0,
                Page = 1,
                PageSize = query.PageSize,
            });
        }

        public Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new HourlyDistributionResult
            {
                Hours = new List<HourlyUsageBucket>(),
                TimeZoneId = TimeZoneInfo.Local.Id,
            });

        public Task<WeeklyRhythmResult> GetWeeklyRhythmAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new WeeklyRhythmResult
            {
                Cells = new List<WeeklyRhythmCell>(),
                TimeZoneId = TimeZoneInfo.Local.Id,
            });

        public Task<OverviewPageSnapshot> GetOverviewSnapshotAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
            Task.FromResult(new OverviewPageSnapshot());
    }

    private static ComboBox? FindDeviceComboBox(Window window, FilterStateService filters) =>
        window.GetVisualDescendants().OfType<ComboBox>()
            .SingleOrDefault(combo => ReferenceEquals(combo.ItemsSource, filters.DeviceOptions));

    [AvaloniaFact]
    public void FilterBar_DeviceDropdown_HiddenWhenSyncOff_VisibleWhenOn()
    {
        var filters = AppServices.Get<FilterStateService>();
        try
        {
            filters.IsSyncEnabled = false;
            var shell = new MainWindowViewModel(
                filters,
                AppServices.Get<SettingsViewModel>(),
                AppServices.Get<DataPricingViewModel>(),
                new RecordingQueryService(),
                new AgentCatalogService(new RecordingQueryService()),
                new StubToast(),
                new PricingEngine());
            var window = new MainWindow { DataContext = shell };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                var combo = FindDeviceComboBox(window, filters);
                Assert.NotNull(combo);
                var panel = combo!.GetVisualParent() as StackPanel;
                Assert.NotNull(panel);
                Assert.False(panel!.IsVisible, "同步关闭时设备下拉不渲染（U5）");

                filters.IsSyncEnabled = true;
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.True(panel.IsVisible, "同步开启时设备下拉出现");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            filters.IsSyncEnabled = false; // DI 单例 Filters 状态复位，不污染其他用例
        }
    }

    [AvaloniaFact]
    public async Task DeviceSelection_RerunsQueries()
    {
        var filters = new FilterStateService();
        var stub = new RecordingQueryService();
        var vm = new SessionsViewModel(ShellPages.For(PageId.Sessions), filters, stub);
        Dispatcher.UIThread.RunJobs(); // 页面首查

        filters.IsSyncEnabled = true;
        filters.Device = new DeviceFilterOption("devB", "Dev B");
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50); // 重查是 async（Task.Run 链）
        Dispatcher.UIThread.RunJobs();

        var last = stub.SessionQueries.LastOrDefault();
        Assert.NotNull(last);
        Assert.Equal("devB", last!.Filter.Device);
    }
}
