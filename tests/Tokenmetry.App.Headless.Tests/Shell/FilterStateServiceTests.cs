using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Headless.XUnit;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// FilterStateService 属性写入路径的 headless 验证：SetOnUi 依赖 Dispatcher.UIThread 判定，
/// 纯 xunit 进程里 UI 线程被并行测试类抢先钉住时写入会被 Post 悬空——依赖 setter 的用例
/// 必须在 [AvaloniaFact]（测试体即 UI 线程）下执行；不触 setter 的纯逻辑用例留在 Tokenmetry.App.Tests。
/// 覆盖：自定义档首次切入预填最近 7 天、已显式设置时不覆盖、自定义日期变更/档位切换的通知契约
/// （页面 VM 按 AffectsQueries 重查）、筛选状态整体经 GlobalFilterMapper.Build(FilterStateService) 的透传。
/// </summary>
public class FilterStateServiceTests
{
    [AvaloniaFact]
    public void Switching_to_custom_prefills_last_seven_days_window()
    {
        var filters = new FilterStateService();

        filters.TimeRange = TimeRangeOption.Custom;

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

        filters.TimeRange = TimeRangeOption.Custom;

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

        filters.TimeRange = TimeRangeOption.Custom;
        Assert.Contains(nameof(FilterStateService.IsCustomRange), raised);
        Assert.True(filters.IsCustomRange);

        raised.Clear();
        filters.TimeRange = TimeRangeOption.Last7Days;
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
            TimeRange = TimeRangeOption.Custom,
            Agent = new AgentOption("Claude Code"),
        };

        var filter = GlobalFilterMapper.Build(filters);

        Assert.Equal("2026-02-01", filter.FromDay);
        Assert.Equal("2026-02-08", filter.ToDay);
        Assert.Equal("Claude Code", filter.Agent);
        Assert.True(filter.IsRangeValid);
    }

    [AvaloniaFact]
    public void Build_maps_model_and_project_from_filter_state()
    {
        var filters = new FilterStateService
        {
            Model = new ModelFilterOption("claude-sonnet-4"),
            Project = new ProjectFilterOption("/work/alpha"),
        };

        var filter = GlobalFilterMapper.Build(filters);

        Assert.Equal("claude-sonnet-4", filter.Model);
        Assert.Equal("/work/alpha", filter.Project);
    }

    [AvaloniaFact]
    public void Model_change_raises_affects_queries()
    {
        var filters = new FilterStateService();
        var affected = new List<string?>();
        filters.PropertyChanged += (_, e) => affected.Add(e.PropertyName);

        filters.Model = new ModelFilterOption("claude-sonnet-4");

        Assert.True(FilterStateService.AffectsQueries(nameof(FilterStateService.Model)));
        Assert.Equal(1, affected.Count(p => p == nameof(FilterStateService.Model)));
        Assert.Contains("claude-sonnet-4", filters.Display);
    }

    [AvaloniaFact]
    public void Update_model_options_replaces_list_and_keeps_selection()
    {
        var filters = new FilterStateService();
        var selected = new ModelFilterOption("gpt-x");
        filters.Model = selected;

        filters.UpdateModelOptions([ModelFilterOption.All, new ModelFilterOption("claude-sonnet-4"), selected]);

        Assert.Equal(3, filters.ModelOptions.Count);
        Assert.Same(selected, filters.Model);
    }

    [AvaloniaFact]
    public void Update_time_range_options_reselects_by_id_and_notifies()
    {
        // D-020：预设装载（清单整体替换）后，选中档按 Id 在新清单中找回（选项重建的稳定契约）
        var filters = new FilterStateService();
        filters.TimeRange = TimeRangeOption.Last30Days;
        var raised = new List<string?>();
        filters.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var preset = new TimeRangeOption("preset:w1", RelativeRangeSpec.Week(0));
        filters.UpdateTimeRangeOptions([.. TimeRangeOption.BuiltIns, preset, TimeRangeOption.Custom]);

        Assert.Equal(10, filters.TimeRangeOptions.Count);
        Assert.Equal("Last30Days", filters.TimeRange.Id);
        Assert.Contains(nameof(FilterStateService.TimeRangeOptions), raised);
    }

    [AvaloniaFact]
    public void Update_time_range_options_falls_back_to_default_when_selection_removed()
    {
        // 预设被删：指向它的选中值如实回落默认档（最近 7 天），不静默悬空
        var filters = new FilterStateService();
        var preset = new TimeRangeOption("preset:w1", RelativeRangeSpec.Week(0));
        filters.UpdateTimeRangeOptions([.. TimeRangeOption.BuiltIns, preset, TimeRangeOption.Custom]);
        filters.TimeRange = preset;
        Assert.Equal("preset:w1", filters.TimeRange.Id);

        var raised = new List<string?>();
        filters.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        filters.UpdateTimeRangeOptions(TimeRangeOption.InitialOptions);

        Assert.Same(TimeRangeOption.Default, filters.TimeRange);
        Assert.False(filters.IsCustomRange);
        Assert.Contains(nameof(FilterStateService.TimeRange), raised);
    }

    [AvaloniaFact]
    public void Language_switch_refreshes_labels_of_current_options()
    {
        // 语言切换重译：静态构造器订阅已随预设生命周期化退场，重译由 FilterStateService 广播处
        // 对当前清单逐项 RefreshLabel（含用户预设的词表自动命名）
        var filters = new FilterStateService();
        var preset = new TimeRangeOption("preset:w1", RelativeRangeSpec.Week(0));
        filters.UpdateTimeRangeOptions([.. TimeRangeOption.BuiltIns, preset, TimeRangeOption.Custom]);
        var original = Localization.Translations.Instance.CurrentLanguage;

        try
        {
            var labelBefore = preset.Label;
            Localization.Translations.Instance.SetLanguage(
                original == Localization.AppLanguage.English
                    ? Localization.AppLanguage.SimplifiedChinese
                    : Localization.AppLanguage.English);

            Assert.NotEqual(labelBefore, preset.Label);
        }
        finally
        {
            Localization.Translations.Instance.SetLanguage(original);
        }
    }
}
