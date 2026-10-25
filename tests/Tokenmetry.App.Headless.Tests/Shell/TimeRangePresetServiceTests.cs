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
/// 用户自定义相对时间预设服务（D-020）的 headless 验证：装载（未知 Kind 如实跳过）、
/// 封闭词表校验（域内/去重/上限，错误文案已本地化）、删除后选中档回落默认档、即存落盘。
/// UpdateTimeRangeOptions 经 Dispatcher 通知，须在 [AvaloniaFact] 下执行（同 FilterStateServiceTests）。
/// </summary>
public class TimeRangePresetServiceTests
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

    private static (FilterStateService Filters, AppSettings Settings, FakeStore Store, TimeRangePresetService Service) Create(
        AppSettings? settings = null)
    {
        var store = new FakeStore();
        var appSettings = settings ?? new AppSettings();
        var filters = new FilterStateService();
        var service = new TimeRangePresetService(filters, appSettings, store);
        return (filters, appSettings, store, service);
    }

    [AvaloniaFact]
    public void Load_builds_options_builtins_presets_custom_and_skips_invalid_entries()
    {
        // 装载只按 Kind 词表与 Id 形状过滤：未知 Kind / 无 preset: 前缀的历史条目如实跳过（不抛、不修写）
        var (filters, _, _, service) = Create(new AppSettings
        {
            CustomTimePresets =
            [
                new RelativeRangePresetSetting { Id = "preset:w1", Kind = "Week", Offset = 0 },
                new RelativeRangePresetSetting { Id = "preset:bad", Kind = "Quarter", Offset = -1 },
                new RelativeRangePresetSetting { Id = "bogus", Kind = "Month", Offset = -1 },
            ],
        });

        service.Load();

        Assert.Equal(8 + 1 + 1, filters.TimeRangeOptions.Count); // 内置 8 + 合法预设 1 + 自定义哨兵
        var preset = Assert.Single(filters.TimeRangeOptions, o => o.IsPreset);
        Assert.Equal("preset:w1", preset.Id);
        Assert.Equal(RelativeRangeSpec.Week(0), preset.Spec);
        Assert.Same(TimeRangeOption.Custom, filters.TimeRangeOptions[^1]);
    }

    [AvaloniaFact]
    public void TryAdd_persists_and_appends_option()
    {
        var (filters, settings, store, service) = Create();
        service.Load();

        var error = service.TryAdd(RelativeRangeKind.Week, 0, 7);

        Assert.Null(error);
        var preset = filters.TimeRangeOptions.Single(o => o.IsPreset);
        Assert.Equal(RelativeRangeSpec.Week(0), preset.Spec);
        Assert.Single(settings.CustomTimePresets);
        Assert.Equal(1, store.SaveCount);
    }

    [AvaloniaFact]
    public void TryAdd_rejects_out_of_domain_days()
    {
        var (filters, settings, _, service) = Create();
        service.Load();

        Assert.NotNull(service.TryAdd(RelativeRangeKind.RollingDays, 0, 1));
        Assert.NotNull(service.TryAdd(RelativeRangeKind.RollingDays, 0, 366));
        Assert.Empty(settings.CustomTimePresets);
        Assert.DoesNotContain(filters.TimeRangeOptions, o => o.IsPreset);
    }

    [AvaloniaFact]
    public void TryAdd_rejects_duplicates_against_builtins_and_existing_presets()
    {
        var (_, settings, _, service) = Create();
        service.Load();

        // 与内置档同语义（Month(0)=本月）拒绝；与既有预设 Week(0) 重复也拒绝（去重键忽略 Days）
        Assert.NotNull(service.TryAdd(RelativeRangeKind.Month, 0, 7));
        Assert.Null(service.TryAdd(RelativeRangeKind.Week, 0, 7));
        Assert.NotNull(service.TryAdd(RelativeRangeKind.Week, 0, 99));

        Assert.Single(settings.CustomTimePresets); // 仅 Week(0) 落库，其余两次均被拒
    }

    [AvaloniaFact]
    public void TryAdd_enforces_preset_limit()
    {
        var (filters, settings, _, service) = Create();
        service.Load();

        // 造满 6 个（直接写 settings + 重装载，绕过 TryAdd 的逐个流程）
        for (var i = 0; i < TimeRangePresetService.MaxPresets; i++)
        {
            settings.CustomTimePresets.Add(new RelativeRangePresetSetting
            {
                Id = $"preset:p{i}",
                Kind = nameof(RelativeRangeKind.RollingDays),
                Days = 10 + i,
            });
        }
        service.Load();

        var error = service.TryAdd(RelativeRangeKind.SingleDay, -1, 7);

        Assert.NotNull(error);
        Assert.Equal(TimeRangePresetService.MaxPresets, settings.CustomTimePresets.Count);
        Assert.Equal(TimeRangePresetService.MaxPresets, filters.TimeRangeOptions.Count(o => o.IsPreset));
    }

    [AvaloniaFact]
    public void Remove_deletes_option_and_falls_back_selection_to_default()
    {
        var (filters, settings, _, service) = Create();
        service.Load();
        Assert.Null(service.TryAdd(RelativeRangeKind.Week, 0, 7));
        var preset = filters.TimeRangeOptions.Single(o => o.IsPreset);
        filters.TimeRange = preset;

        service.Remove(preset.Id);

        Assert.Empty(settings.CustomTimePresets);
        Assert.DoesNotContain(filters.TimeRangeOptions, o => o.IsPreset);
        Assert.Same(TimeRangeOption.Default, filters.TimeRange); // 删除选中档 → 回落契约
    }

    [AvaloniaFact]
    public void Remove_unknown_id_is_idempotent_noop()
    {
        var (filters, settings, _, service) = Create();
        service.Load();
        Assert.Null(service.TryAdd(RelativeRangeKind.Week, 0, 7));

        service.Remove("preset:none");

        Assert.Single(settings.CustomTimePresets);
        Assert.Single(filters.TimeRangeOptions, o => o.IsPreset);
    }
}
