using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Tokenmetry.App.Localization;
using Tokenmetry.App.Services;
using Tokenmetry.Core.Models;
using Xunit;

namespace Tokenmetry.App.Headless.Tests.Shell;

/// <summary>
/// 时间范围持久化（FilterStatePersistence）的 headless 验证：启动应用上次档位/自定义起止 +
/// 顶部栏变更即存。依赖 FilterStateService setter（Dispatcher 判定），须在 [AvaloniaFact] 下执行；
/// fake store 内存捕获落盘（fake 的 SaveAsync 返回已完成 Task，fire-and-forget 持久化同步跑完，
/// 断言无需等待）。
/// </summary>
public class FilterStatePersistenceTests
{
    private sealed class FakeStore : IAppSettingsStore
    {
        public int SaveCount { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private static (FilterStateService Filters, AppSettings Settings, FakeStore Store, FilterStatePersistence Persistence) Create(
        AppSettings? settings = null)
    {
        var store = new FakeStore();
        var appSettings = settings ?? new AppSettings();
        var filters = new FilterStateService();
        var persistence = new FilterStatePersistence(filters, appSettings, store);
        return (filters, appSettings, store, persistence);
    }

    [AvaloniaFact]
    public void Apply_populates_range_and_custom_days_from_settings()
    {
        var (filters, _, _, persistence) = Create(new AppSettings
        {
            TimeRange = "Last30Days",
            CustomFromDay = "2026-01-01",
            CustomToDay = "2026-01-10",
        });

        try
        {
            persistence.Apply();

            // 档位必须是稳定实例：顶部栏选择控件 SelectedItem 按相等回显
            Assert.Same(
                TimeRangeOption.Last30Days,
                filters.TimeRange);
            // 应用为本地自然日 0 点（映射侧只按日历日比较，断言同样按日历日）
            Assert.Equal(new DateOnly(2026, 1, 1), DateOnly.FromDateTime(filters.CustomFromDate!.Value.Date));
            Assert.Equal(new DateOnly(2026, 1, 10), DateOnly.FromDateTime(filters.CustomToDate!.Value.Date));
        }
        finally
        {
            persistence.Dispose();
        }
    }

    [AvaloniaFact]
    public void Apply_unknown_range_and_bad_days_fall_back_without_throwing()
    {
        var (filters, _, _, persistence) = Create(new AppSettings
        {
            TimeRange = "Bogus",
            CustomFromDay = "not-a-date",
            CustomToDay = "2026-13-99",
        });

        try
        {
            persistence.Apply();

            Assert.Same(TimeRangeOption.Default, filters.TimeRange);
            Assert.Null(filters.CustomFromDate);
            Assert.Null(filters.CustomToDate);
        }
        finally
        {
            persistence.Dispose();
        }
    }

    [AvaloniaFact]
    public void Apply_custom_without_days_prefills_last_seven_days()
    {
        var (filters, _, _, persistence) = Create(new AppSettings { TimeRange = "Custom" });

        try
        {
            persistence.Apply();

            Assert.True(filters.IsCustomRange);
            var today = DateOnly.FromDateTime(DateTime.Today);
            Assert.Equal(today.AddDays(-6).ToDateTime(TimeOnly.MinValue), filters.CustomFromDate);
            Assert.Equal(today.ToDateTime(TimeOnly.MinValue), filters.CustomToDate);
        }
        finally
        {
            persistence.Dispose();
        }
    }

    [AvaloniaFact]
    public void Range_change_updates_settings_and_persists()
    {
        var (filters, settings, store, persistence) = Create();
        persistence.Apply();

        filters.TimeRange = TimeRangeOption.Last30Days;

        Assert.Equal("Last30Days", settings.TimeRange);
        Assert.Equal(1, store.SaveCount);
    }

    [AvaloniaFact]
    public void Custom_date_change_persists_invariant_day_strings()
    {
        var (filters, settings, store, persistence) = Create();
        persistence.Apply();

        filters.CustomFromDate = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("2026-02-01", settings.CustomFromDay);
        Assert.Null(settings.CustomToDay);
        Assert.Equal(1, store.SaveCount);
    }

    [AvaloniaFact]
    public void Language_broadcast_does_not_trigger_save()
    {
        var (filters, _, store, persistence) = Create();
        persistence.Apply();

        // 语言切换 → FilterStateService 广播 INPC 空串：不属于时间范围变更，不落盘
        Localization.Translations.Instance.SetLanguage(
            Localization.Translations.Instance.CurrentLanguage == AppLanguage.English
                ? AppLanguage.SimplifiedChinese
                : AppLanguage.English);

        Assert.Equal(0, store.SaveCount);
    }

    // ── D-020：用户预设 Id 的选中持久化（装载序：TimeRangePresetService.Load → Apply）──

    [AvaloniaFact]
    public void Apply_resolves_persisted_preset_id_after_presets_loaded()
    {
        var (filters, settings, store, persistence) = Create(new AppSettings
        {
            TimeRange = "preset:w1",
            CustomTimePresets =
            [
                new RelativeRangePresetSetting { Id = "preset:w1", Kind = "Week", Offset = 0, Days = 7 },
            ],
        });
        var presets = new TimeRangePresetService(filters, settings, store);

        try
        {
            presets.Load();
            persistence.Apply();

            Assert.Equal("preset:w1", filters.TimeRange.Id);
            Assert.True(filters.TimeRange.IsPreset);
        }
        finally
        {
            persistence.Dispose();
        }
    }

    [AvaloniaFact]
    public void Apply_preset_id_without_loaded_preset_falls_back_to_default()
    {
        // 预设被删后指向它的选中值如实回落默认档（最近 7 天），不静默悬空也不抛
        var (filters, settings, store, persistence) = Create(new AppSettings
        {
            TimeRange = "preset:gone",
            CustomTimePresets =
            [
                new RelativeRangePresetSetting { Id = "preset:other", Kind = "Month", Offset = -1 },
            ],
        });
        var presets = new TimeRangePresetService(filters, settings, store);

        try
        {
            presets.Load();
            persistence.Apply();

            Assert.Same(TimeRangeOption.Default, filters.TimeRange);
        }
        finally
        {
            persistence.Dispose();
        }
    }

    [AvaloniaFact]
    public void Selecting_preset_persists_its_stable_id()
    {
        var (filters, settings, store, persistence) = Create(new AppSettings
        {
            CustomTimePresets =
            [
                new RelativeRangePresetSetting { Id = "preset:w1", Kind = "Week", Offset = 0 },
            ],
        });
        var presets = new TimeRangePresetService(filters, settings, store);
        presets.Load();
        persistence.Apply();

        var preset = filters.TimeRangeOptions.Single(o => o.IsPreset);
        filters.TimeRange = preset;

        Assert.Equal("preset:w1", settings.TimeRange);
        Assert.Equal(1, store.SaveCount);
    }
}
