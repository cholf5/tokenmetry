using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tokenmetry.App.Localization;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Services;

/// <summary>
/// 用户自定义相对时间范围预设的单点管理（D-020）：从 AppSettings 装载进
/// <see cref="FilterStateService"/> 选项清单、增删（校验 + 即存，best-effort 落盘同
/// FilterStatePersistence 语义）、整体替换选项清单。校验为<b>封闭词表</b>：
/// <see cref="RelativeRangeSpec.IsValid"/> 域内 + 与内置档/既有预设去重（<see cref="RelativeRangeSpec.DedupKey"/>）
/// + 数量上限（<see cref="MaxPresets"/>）——标签自动生成（Translations 取串）的前提是词表封闭，
/// 用户不自由命名。装载时未知 Kind 的历史条目如实跳过（不抛、不修写）。
/// 与 FilterStatePersistence 共享同一 AppSettings 单例与 store：两处保存都整文件原子替换，
/// last-writer-wins 且内容收敛（都基于同一内存单例），语义见 FilterStatePersistence 类型说明。
/// </summary>
public sealed class TimeRangePresetService
{
    /// <summary>预设数量上限：下拉（Flyout 档位清单）保持在可扫读规模，超限时添加如实报错。</summary>
    public const int MaxPresets = 6;

    private readonly FilterStateService _filters;
    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public TimeRangePresetService(FilterStateService filters, AppSettings settings, IAppSettingsStore store)
    {
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>启动装载：settings 里的预设投影 → 选项实例，组装「内置档 + 预设 + 自定义哨兵」清单。
    /// 必须先于 FilterStatePersistence.Apply 调用（持久化的选中 Id 可指向预设）。未知 Kind 如实跳过。</summary>
    public void Load()
    {
        RebuildOptions();
    }

    /// <summary>
    /// 添加预设。成功返回 null 并即存 + 刷新选项清单；失败返回错误文案（已本地化，供表单就地显示），
    /// 不产生任何副作用。校验序：词表域 → 数量上限 → 与内置档/既有预设去重。
    /// </summary>
    public string? TryAdd(RelativeRangeKind kind, int offset, int days)
    {
        var spec = new RelativeRangeSpec(kind, offset, days);
        if (!spec.IsValid)
        {
            return kind == RelativeRangeKind.RollingDays
                ? Translations.Instance.TimePresetErrorDays
                : Translations.Instance.TimePresetErrorInvalid;
        }

        if (_settings.CustomTimePresets.Count >= MaxPresets)
        {
            return string.Format(Translations.Instance.TimePresetErrorLimit, MaxPresets);
        }

        if (TimeRangeOption.BuiltIns.Concat(_filters.TimeRangeOptions.Where(o => o.IsPreset))
                .Any(o => o.Spec is { } existing && existing.DedupKey == spec.DedupKey))
        {
            return Translations.Instance.TimePresetErrorDuplicate;
        }

        _settings.CustomTimePresets.Add(new RelativeRangePresetSetting
        {
            Id = $"{TimeRangeOption.PresetPrefix}{Guid.NewGuid():N}",
            Kind = kind.ToString(),
            Offset = spec.Offset,
            Days = spec.Days,
        });
        RebuildOptions();
        _ = PersistAsync();
        return null;
    }

    /// <summary>删除预设（按稳定 Id）。Id 不存在时 no-op（幂等）；删除后选项清单整体替换，
    /// 若被删的是当前选中档，FilterStateService 按其回落契约落到默认档。</summary>
    public void Remove(string presetId)
    {
        if (_settings.CustomTimePresets.RemoveAll(p => p.Id == presetId) == 0)
        {
            return;
        }

        RebuildOptions();
        _ = PersistAsync();
    }

    /// <summary>按当前 settings 投影组装完整清单（内置 + 预设 + 自定义哨兵，顺序即 Flyout 展示序）。</summary>
    private void RebuildOptions()
    {
        var presets = _settings.CustomTimePresets
            .Select(ToOption)
            .Where(o => o is not null)
            .Cast<TimeRangeOption>()
            .ToList();
        _filters.UpdateTimeRangeOptions([.. TimeRangeOption.BuiltIns, .. presets, TimeRangeOption.Custom]);
    }

    /// <summary>投影 → 选项实例；未知 Kind（settings.json 手改/旧版本）如实跳过返回 null。</summary>
    private static TimeRangeOption? ToOption(RelativeRangePresetSetting preset)
    {
        if (string.IsNullOrEmpty(preset.Id) || !preset.Id.StartsWith(TimeRangeOption.PresetPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return preset.Kind switch
        {
            nameof(RelativeRangeKind.RollingDays) => new TimeRangeOption(preset.Id, RelativeRangeSpec.Rolling(preset.Days)),
            nameof(RelativeRangeKind.SingleDay) => new TimeRangeOption(preset.Id, RelativeRangeSpec.SingleDay(preset.Offset)),
            nameof(RelativeRangeKind.Week) => new TimeRangeOption(preset.Id, RelativeRangeSpec.Week(preset.Offset)),
            nameof(RelativeRangeKind.Month) => new TimeRangeOption(preset.Id, RelativeRangeSpec.Month(preset.Offset)),
            _ => null,
        };
    }

    /// <summary>单飞行 gate 串行化的 best-effort 落盘（语义同 FilterStatePersistence：磁盘失败不阻断
    /// UI 已生效的更改，打最小诊断，后续任一更改会继续尝试）。</summary>
    private async Task PersistAsync()
    {
        try
        {
            await _saveGate.WaitAsync();
            try
            {
                await _store.SaveAsync(_settings);
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tokenmetry] 时间预设持久化失败（UI 已生效，待下次更改重试）：{ex.Message}");
        }
    }
}
