using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Tokenmetry.App.Localization;

namespace Tokenmetry.App.Services;

/// <summary>全局时间范围（D-003 顶部栏筛选；在原型 #rangeTop 的 7/30/90/全部四档上追加 今日 与 自定义 两档）。</summary>
public enum TimeRange
{
    Today,
    Last7Days,
    Last30Days,
    Last90Days,
    All,
    Custom,
}

/// <summary>
/// 时间范围选项（ComboBox 项）。稳定实例 + Label 经 Translations 实时取串（语言切换即时重译，
/// 进程级订阅见静态构造器）；相等性只看 <see cref="Value"/>——SelectedItem 绑定比较稳定，
/// 且语言切换不改变相等性（选项清单重建按值相等保持选中的既有契约不受影响）。
/// </summary>
public sealed class TimeRangeOption : INotifyPropertyChanged
{
    public TimeRangeOption(TimeRange value) => Value = value;

    public TimeRange Value { get; }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label => Value switch
    {
        TimeRange.Today => Translations.Instance.TimeRangeToday,
        TimeRange.Last7Days => Translations.Instance.TimeRange7,
        TimeRange.Last30Days => Translations.Instance.TimeRange30,
        TimeRange.Last90Days => Translations.Instance.TimeRange90,
        TimeRange.All => Translations.Instance.TimeRangeAll,
        TimeRange.Custom => Translations.Instance.TimeRangeCustom,
        _ => Translations.Instance.TimeRangeAll,
    };

    /// <summary>是否为自定义档（放在本类：FilterStateService 内有同名 TimeRange 属性会遮蔽枚举名）。</summary>
    public bool IsCustom => Value == TimeRange.Custom;

    /// <summary>语言切换后刷新展示名（Label 为计算属性，需显式通知）。</summary>
    public void RefreshLabel() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

    public static IReadOnlyList<TimeRangeOption> Options { get; } =
    [
        new(TimeRange.Today),
        new(TimeRange.Last7Days),
        new(TimeRange.Last30Days),
        new(TimeRange.Last90Days),
        new(TimeRange.All),
        new(TimeRange.Custom),
    ];

    /// <summary>
    /// 默认时间范围：最近 7 天（对齐原型 #rangeTop 默认选中与初始渲染 renderTrend(7)；D-003 审查后由 30 天改齐）。
    /// 按值定位而非 Options 下标，选项清单增删不改默认档。
    /// </summary>
    public static TimeRangeOption Default { get; } = Options.First(o => o.Value == TimeRange.Last7Days);

    public override string ToString() => Label;

    static TimeRangeOption()
    {
        // 选项为进程级稳定实例（应用级生命周期），直订阅语言切换信号统一刷新 Label，无需退订。
        Translations.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Translations.CurrentLanguage))
            {
                foreach (var option in Options)
                {
                    option.RefreshLabel();
                }
            }
        };
    }
}

/// <summary>
/// Agent 筛选选项。"所有 Agent" 是不过滤的固定 UI 语义项（Label 实时取串）；真实 Agent 清单由
/// D-008 的 AgentCatalogService 从 I-029 维度查询拉取并经 <see cref="FilterStateService.UpdateAgentOptions"/>
/// 注入（单一真相源，全局下拉与右栏复选组共用）——不再使用原型占位清单。
/// 相等性只看 <see cref="Id"/>（即原 record 值相等的键部分：选项清单重建 / 外部跳转判重补入的
/// 幂等契约不变，语言切换亦不改变相等性）。
/// </summary>
public sealed class AgentOption : INotifyPropertyChanged, IEquatable<AgentOption>
{
    /// <summary>"所有 Agent"（不过滤）的固定标识。</summary>
    public const string AllId = "all";

    public AgentOption(string id, string? label = null)
    {
        Id = id;
        _label = label;
    }

    public string Id { get; }

    private readonly string? _label;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>展示名：真实 Agent 为目录键（数据，非 UI 文案）；"所有 Agent" 哨兵经 Translations 实时取串。</summary>
    public string Label => Id == AllId ? Translations.Instance.AllAgents : (_label ?? Id);

    /// <summary>语言切换后刷新哨兵展示名。</summary>
    public void RefreshLabel() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

    /// <summary>"所有 Agent"（不过滤）选项（运行时默认值，也是选项清单首项）。</summary>
    public static AgentOption All { get; } = new(AllId);

    public override string ToString() => Label;

    public bool Equals(AgentOption? other) => other is not null && Id == other.Id;

    public override bool Equals(object? obj) => obj is AgentOption other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);

    static AgentOption()
    {
        // All 为进程级稳定实例，直订阅语言切换信号刷新哨兵 Label，无需退订。
        Translations.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Translations.CurrentLanguage))
            {
                All.RefreshLabel();
            }
        };
    }
}

/// <summary>
/// 全局筛选状态（D-003）：时间范围 + Agent。顶部栏是主要编辑入口，各页 VM 经
/// <see cref="INotifyPropertyChanged"/>（含 <see cref="Display"/> 汇总串）订阅变更。
/// 线程模型：属性写入若发生在非 UI 线程，整个赋值 + 通知被 marshal 到 UI 线程执行，
/// 因此所有 PropertyChanged 通知都保证在 UI 线程发出，绑定与订阅方无需自行调度。
/// </summary>
public sealed class FilterStateService : ObservableObject
{
    private TimeRangeOption _timeRange = TimeRangeOption.Default;
    private AgentOption _agent = AgentOption.All;
    private IReadOnlyList<AgentOption> _agentOptions = [AgentOption.All];
    private DateTimeOffset? _customFromDate;
    private DateTimeOffset? _customToDate;

    public FilterStateService()
    {
        // 语言切换重译：选项 Label 由各选项自行刷新，此处补发 Display 汇总串。
        Translations.Instance.PropertyChanged += OnTranslationsPropertyChanged;
    }

    /// <summary>
    /// 属性名是否会影响查询结果（各页 VM OnFilterPropertyChanged 的统一判据）：
    /// 空 = 语言切换全属性广播（整页重查重渲染），其余为筛选的四个可变维度。
    /// Display 是派生串，不在此列（避免一次变更触发两次重查）。
    /// </summary>
    public static bool AffectsQueries(string? propertyName) =>
        string.IsNullOrEmpty(propertyName)
        || propertyName == nameof(TimeRange)
        || propertyName == nameof(Agent)
        || propertyName == nameof(CustomFromDate)
        || propertyName == nameof(CustomToDate);

    /// <summary>时间范围选项（顶部栏 ComboBox 数据源）。</summary>
    public IReadOnlyList<TimeRangeOption> TimeRangeOptions => TimeRangeOption.Options;

    /// <summary>
    /// Agent 选项（顶部栏 ComboBox 数据源）。初始仅 [所有 Agent]（诚实：真实清单未加载前
    /// 不冒充选项），D-008 外壳启动时经 AgentCatalogService 异步拉取后由
    /// <see cref="UpdateAgentOptions"/> 替换并通知（加载失败/无数据保持本值）。
    /// </summary>
    public IReadOnlyList<AgentOption> AgentOptions => _agentOptions;

    /// <summary>时间范围。默认最近 7 天。</summary>
    public TimeRangeOption TimeRange
    {
        get => _timeRange;
        set => SetOnUi(() =>
        {
            if (!SetProperty(ref _timeRange, value))
            {
                return false;
            }

            OnPropertyChanged(nameof(IsCustomRange));
            if (value.IsCustom && _customFromDate is null && _customToDate is null)
            {
                // 首次切到自定义：预填与默认档一致的最近 7 天窗口（本地自然日含端点），保证起止两端有值可直接查询。
                var today = DateOnly.FromDateTime(DateTime.Today);
                _customFromDate = today.AddDays(-6).ToDateTime(TimeOnly.MinValue);
                _customToDate = today.ToDateTime(TimeOnly.MinValue);
                OnPropertyChanged(nameof(CustomFromDate));
                OnPropertyChanged(nameof(CustomToDate));
            }

            return true;
        });
    }

    /// <summary>
    /// 自定义范围起始日（含；DatePicker SelectedDate 绑定源，取本地自然日的 0 点）。
    /// 仅 <see cref="TimeRange.Custom"/> 档参与查询映射（未选侧 = 不设该端界限）。
    /// </summary>
    public DateTimeOffset? CustomFromDate
    {
        get => _customFromDate;
        set => SetOnUi(() => SetProperty(ref _customFromDate, value));
    }

    /// <summary>自定义范围结束日（含；语义同 <see cref="CustomFromDate"/>）。</summary>
    public DateTimeOffset? CustomToDate
    {
        get => _customToDate;
        set => SetOnUi(() => SetProperty(ref _customToDate, value));
    }

    /// <summary>当前是否为自定义档（顶部栏据此显隐起止日期选择器）。</summary>
    public bool IsCustomRange => _timeRange.IsCustom;

    /// <summary>Agent 筛选。默认所有 Agent（不过滤）。</summary>
    public AgentOption Agent
    {
        get => _agent;
        set => SetOnUi(() => SetProperty(ref _agent, value));
    }

    /// <summary>筛选汇总展示串（如「最近 7 天 · 所有 Agent」），任一维度变化都会通知。</summary>
    public string Display => $"{TimeRange.Label} · {Agent.Label}";

    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage))
        {
            // 语言切换广播全属性变更（INPC 空串约定）：各页 VM 的 IsNullOrEmpty 分支按「整页重查
            // 重渲染」消费——Token 紧凑数值等单位随语言即时切换（选项 Label/Display 单发不足以驱动整页）。
            OnPropertyChanged(string.Empty);
        }
    }

    /// <summary>
    /// 替换 Agent 选项清单并通知（D-008：AgentCatalogService 从 I-029 维度查询拉到真实清单后调用，
    /// 顶部栏 ComboBox 经 PropertyChanged(AgentOptions) 刷新；非 UI 线程调用时 marshal，同属性 setter 语义）。
    /// 当前选中项（默认"所有 Agent"）不受清单替换影响。Display 不依赖本清单，不发 Display 通知。
    /// 注意不走 SetProperty（其 CallerMemberName 在方法内会解析成方法名），显式给出属性名。
    /// </summary>
    public void UpdateAgentOptions(IReadOnlyList<AgentOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        void Apply()
        {
            if (ReferenceEquals(_agentOptions, options))
            {
                return;
            }

            _agentOptions = options;
            OnPropertyChanged(nameof(AgentOptions));
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply);
        }
    }

    private void SetOnUi(Func<bool> apply)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyAndNotify(apply);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyAndNotify(apply));
        }
    }

    private void ApplyAndNotify(Func<bool> apply)
    {
        if (apply())
        {
            OnPropertyChanged(nameof(Display));
        }
    }
}
