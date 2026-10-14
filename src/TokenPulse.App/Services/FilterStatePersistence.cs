using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using TokenPulse.Core.Models;

namespace TokenPulse.App.Services;

/// <summary>
/// 全局时间范围持久化（启动应用 + 变更即存，对齐 Theme/Language 的 settings.json 语义）：
/// <list type="bullet">
/// <item><see cref="Apply"/>：启动时把 <see cref="AppSettings"/> 里的时间档 + 自定义起止应用回
/// <see cref="FilterStateService"/>。日期先于档位设置——自定义档缺起止时 FilterStateService 的
/// setter 预填（最近 7 天）才会生效；档位用 <see cref="TimeRangeOption.Options"/> 的稳定实例赋值，
/// 顶部栏 ComboBox 的 SelectedItem 按引用相等匹配才能正确回显。未知档名/坏日期如实回落默认，不抛。</item>
/// <item>变更即存：顶部栏改档位/起止日期后同步回 AppSettings 并原子落盘（单飞行 gate 串行化，
/// best-effort——磁盘失败不阻断 UI 已生效的更改，打最小诊断，后续任一更改会继续尝试）。
/// 显式按属性名过滤：语言切换的全属性广播（INPC 空串）不触发落盘。</item>
/// </list>
/// Agent 筛选<b>有意不持久化</b>：Agent 清单是采集数据驱动的（AgentCatalogService），上次会话的
/// Agent 可能在本次数据里不存在，持久化会制造静默空结果。
/// 生命周期 = 应用单例（AppServices 物化并 Apply）；测试如需多次挂接同一 FilterStateService，
/// Dispose 退订。所有属性通知保证在 UI 线程（FilterStateService 线程模型），AppSettings 同线程改写，
/// 与 SettingsViewModel 的自动保存操作同一单例且不冲突（整文件原子替换，last-writer-wins 同内容）。
/// </summary>
public sealed class FilterStatePersistence : IDisposable
{
    private readonly FilterStateService _filters;
    private readonly AppSettings _settings;
    private readonly IAppSettingsStore _store;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public FilterStatePersistence(FilterStateService filters, AppSettings settings, IAppSettingsStore store)
    {
        _filters = filters;
        _settings = settings;
        _store = store;
    }

    /// <summary>启动应用上次的时间档与自定义起止（先日期后档位，见类型说明），随后开始监听变更。</summary>
    public void Apply()
    {
        if (DateOnly.TryParseExact(
                _settings.CustomFromDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
        {
            _filters.CustomFromDate = from.ToDateTime(TimeOnly.MinValue);
        }

        if (DateOnly.TryParseExact(
                _settings.CustomToDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
        {
            _filters.CustomToDate = to.ToDateTime(TimeOnly.MinValue);
        }

        if (Enum.TryParse(_settings.TimeRange, out TimeRange parsed))
        {
            var option = TimeRangeOption.Options.FirstOrDefault(o => o.Value == parsed);
            if (option is not null)
            {
                _filters.TimeRange = option;
            }
        }

        // Apply 自身的赋值不回写（订阅在应用完成后挂上）：settings.json 已含这些值。
        _filters.PropertyChanged += OnFilterPropertyChanged;
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 空串 = 语言切换全属性广播，与时间范围持久化无关。
        if (e.PropertyName is not (
                nameof(FilterStateService.TimeRange)
                or nameof(FilterStateService.CustomFromDate)
                or nameof(FilterStateService.CustomToDate)))
        {
            return;
        }

        _settings.TimeRange = _filters.TimeRange.Value.ToString();
        _settings.CustomFromDay = FormatDay(_filters.CustomFromDate);
        _settings.CustomToDay = FormatDay(_filters.CustomToDate);
        _ = PersistAsync();
    }

    /// <summary>单飞行 gate 串行化的 best-effort 落盘（语义同 SettingsViewModel 的自动保存）。</summary>
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
            Console.Error.WriteLine($"[TokenPulse] 时间范围持久化失败（UI 已生效，待下次更改重试）：{ex.Message}");
        }
    }

    private static string? FormatDay(DateTimeOffset? date) =>
        date is { } d ? DateOnly.FromDateTime(d.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    public void Dispose() => _filters.PropertyChanged -= OnFilterPropertyChanged;
}
