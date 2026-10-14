using System;
using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using TokenPulse.App.Services;
using TokenPulse.Core.Models;
using Xunit;

namespace TokenPulse.App.Headless.Tests.Shell;

/// <summary>
/// FilterStateService 属性写入路径的 headless 验证：SetOnUi 依赖 Dispatcher.UIThread 判定，
/// 纯 xunit 进程里 UI 线程被并行测试类抢先钉住时写入会被 Post 悬空——依赖 setter 的用例
/// 必须在 [AvaloniaFact]（测试体即 UI 线程）下执行；不触 setter 的纯逻辑用例留在 TokenPulse.App.Tests。
/// 覆盖：自定义档首次切入预填最近 7 天、已显式设置时不覆盖、自定义日期变更/档位切换的通知契约
/// （页面 VM 按 AffectsQueries 重查）、筛选状态整体经 GlobalFilterMapper.Build(FilterStateService) 的透传。
/// </summary>
public class FilterStateServiceTests
{
    [AvaloniaFact]
    public void Switching_to_custom_prefills_last_seven_days_window()
    {
        var filters = new FilterStateService();

        filters.TimeRange = new TimeRangeOption(TimeRange.Custom);

        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(today.AddDays(-6).ToDateTime(TimeOnly.MinValue), filters.CustomFromDate);
        Assert.Equal(today.ToDateTime(TimeOnly.MinValue), filters.CustomToDate);
        Assert.True(filters.IsCustomRange);
    }

    [AvaloniaFact]
    public void Switching_to_custom_keeps_explicitly_set_dates()
    {
        var from = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);
        var filters = new FilterStateService
        {
            CustomFromDate = from,
            CustomToDate = to,
        };

        filters.TimeRange = new TimeRangeOption(TimeRange.Custom);

        Assert.Equal(from, filters.CustomFromDate);
        Assert.Equal(to, filters.CustomToDate);
    }

    [AvaloniaFact]
    public void Custom_date_change_notifies_for_query_reload()
    {
        var filters = new FilterStateService();
        var raised = new List<string?>();
        filters.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        filters.CustomFromDate = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Contains(nameof(FilterStateService.CustomFromDate), raised);
    }

    [AvaloniaFact]
    public void IsCustomRange_follows_time_range_and_notifies()
    {
        var filters = new FilterStateService();
        var raised = new List<string?>();
        filters.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(filters.IsCustomRange);

        filters.TimeRange = new TimeRangeOption(TimeRange.Custom);
        Assert.Contains(nameof(FilterStateService.IsCustomRange), raised);
        Assert.True(filters.IsCustomRange);

        raised.Clear();
        filters.TimeRange = new TimeRangeOption(TimeRange.Last7Days);
        Assert.Contains(nameof(FilterStateService.IsCustomRange), raised);
        Assert.False(filters.IsCustomRange);
    }

    [AvaloniaFact]
    public void Mapper_from_filter_state_service_passes_range_agent_and_custom_dates_through()
    {
        var filters = new FilterStateService
        {
            CustomFromDate = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            CustomToDate = new DateTimeOffset(2026, 2, 8, 0, 0, 0, TimeSpan.Zero),
            TimeRange = new TimeRangeOption(TimeRange.Custom),
            Agent = new AgentOption("Claude Code"),
        };

        var filter = GlobalFilterMapper.Build(filters);

        Assert.Equal("2026-02-01", filter.FromDay);
        Assert.Equal("2026-02-08", filter.ToDay);
        Assert.Equal("Claude Code", filter.Agent);
        Assert.True(filter.IsRangeValid);
    }
}
