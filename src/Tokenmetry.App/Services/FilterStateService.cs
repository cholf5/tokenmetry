using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Tokenmetry.App.Localization;

namespace Tokenmetry.App.Services;

/// <summary>
/// 时间范围选项（下拉项）。稳定实例 + Label 经 Translations 实时取串（语言切换重译由
/// <see cref="FilterStateService"/> 的语言广播处对其当前清单统一 RefreshLabel——预设实例有
/// 生命周期，不再走静态构造器进程级订阅）；相等性只看 <see cref="Id"/>——SelectedItem 绑定
/// 比较稳定，且语言切换不改变相等性（选项清单重建按 Id 相等保持选中的既有契约不受影响）。
/// 内置档沿用旧 TimeRange 枚举名作为 Id（旧 settings.json 的选中值照常解析）；内置档 =
/// 预定义 <see cref="RelativeRangeSpec"/> 实例（D-020），用户预设经
/// <see cref="TimeRangePresetService"/> 生成（Id = "preset:&lt;guid&gt;"），All/Custom 保持哨兵。
/// </summary>
public sealed class TimeRangeOption : INotifyPropertyChanged, IEquatable<TimeRangeOption>
{
    /// <summary>"自定义"档哨兵 Id（日期由 CustomFromDate/CustomToDate 承载）。</summary>
    public const string CustomId = "Custom";

    /// <summary>"全部时间"档哨兵 Id（双端无界）。</summary>
    public const string AllId = "All";

    /// <summary>用户预设 Id 前缀（后接 guid；删除预设后指向它的持久化选中值回落默认档）。</summary>
    public const string PresetPrefix = "preset:";

    public TimeRangeOption(string id, RelativeRangeSpec? spec)
    {
        Id = id;
        Spec = spec;
    }

    /// <summary>稳定标识：内置档 = 旧枚举名（"Today"/"Last7Days"/…），预设 = "preset:&lt;guid&gt;"。</summary>
    public string Id { get; }

    /// <summary>相对范围词表；null = 哨兵档（All 双端无界 / Custom 按起止日期映射）。</summary>
    public RelativeRangeSpec? Spec { get; }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label => Spec is { } spec
        ? spec.ToLabel()
        : IsCustom
            ? Translations.Instance.TimeRangeCustom
            : Translations.Instance.TimeRangeAll;

    /// <summary>是否为自定义档（放本类：FilterStateService 内有同名 CustomId 常量不遮蔽实例成员）。</summary>
    public bool IsCustom => Id == CustomId;

    /// <summary>是否为"全部时间"哨兵档。</summary>
    public bool IsAll => Id == AllId;

    /// <summary>是否为用户预设档（可删除；内置档/哨兵不可）。</summary>
    public bool IsPreset => Id.StartsWith(PresetPrefix, StringComparison.Ordinal);

    /// <summary>语言切换后刷新展示名（Label 为计算属性，需显式通知）。</summary>
    public void RefreshLabel() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

    // ── 内置稳定实例（应用级生命周期；清单顺序 = 下拉展示顺序：日历档 → 滚动档 → 全部）──
    public static TimeRangeOption Today { get; } = new("Today", RelativeRangeSpec.SingleDay(0));
    public static TimeRangeOption Yesterday { get; } = new("Yesterday", RelativeRangeSpec.SingleDay(-1));
    public static TimeRangeOption ThisMonth { get; } = new("ThisMonth", RelativeRangeSpec.Month(0));
    public static TimeRangeOption LastMonth { get; } = new("LastMonth", RelativeRangeSpec.Month(-1));
    public static TimeRangeOption Last7Days { get; } = new("Last7Days", RelativeRangeSpec.Rolling(7));
    public static TimeRangeOption Last30Days { get; } = new("Last30Days", RelativeRangeSpec.Rolling(30));
    public static TimeRangeOption Last90Days { get; } = new("Last90Days", RelativeRangeSpec.Rolling(90));
    public static TimeRangeOption All { get; } = new(AllId, null);
    public static TimeRangeOption Custom { get; } = new(CustomId, null);

    /// <summary>内置档清单（不含预设与自定义哨兵；完整清单组装见 <see cref="InitialOptions"/>）。</summary>
    public static IReadOnlyList<TimeRangeOption> BuiltIns { get; } =
    [
        Today, Yesterday, ThisMonth, LastMonth, Last7Days, Last30Days, Last90Days, All,
    ];

    /// <summary>初始完整清单（内置 + 自定义哨兵；预设由 TimeRangePresetService 装载后整体替换）。</summary>
    public static IReadOnlyList<TimeRangeOption> InitialOptions { get; } = [.. BuiltIns, Custom];

    /// <summary>按 Id 找内置档（含自定义哨兵）；预设不在内置清单，经 FilterStateService 当前清单查找。</summary>
    public static TimeRangeOption? FindBuiltIn(string id) =>
        id == CustomId ? Custom : BuiltIns.FirstOrDefault(o => o.Id == id);

    /// <summary>
    /// 默认时间范围：最近 7 天（对齐原型 #rangeTop 默认选中与初始渲染 renderTrend(7)；D-003 审查后由 30 天改齐）。
    /// 按值定位而非清单下标，清单增删不改默认档。
    /// </summary>
    public static TimeRangeOption Default { get; } = Last7Days;

    public bool Equals(TimeRangeOption? other) => other is not null && Id == other.Id;

    public override bool Equals(object? obj) => obj is TimeRangeOption other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);

    public override string ToString() => Label;
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
    private IReadOnlyList<TimeRangeOption> _timeRangeOptions = TimeRangeOption.InitialOptions;
    private AgentOption _agent = AgentOption.All;
    private IReadOnlyList<AgentOption> _agentOptions = [AgentOption.All];
    private ModelFilterOption _model = ModelFilterOption.All;
    private ProjectFilterOption _project = ProjectFilterOption.All;
    private IReadOnlyList<ModelFilterOption> _modelOptions = [ModelFilterOption.All];
    private IReadOnlyList<ProjectFilterOption> _projectOptions = [ProjectFilterOption.All];
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
        || propertyName == nameof(Model)
        || propertyName == nameof(Project)
        || propertyName == nameof(CustomFromDate)
        || propertyName == nameof(CustomToDate);

    /// <summary>时间范围选项（顶栏范围选择器数据源）：初始为内置档 + 自定义哨兵，用户预设由
    /// <see cref="TimeRangePresetService"/> 装载后经 <see cref="UpdateTimeRangeOptions"/> 整体替换。</summary>
    public IReadOnlyList<TimeRangeOption> TimeRangeOptions => _timeRangeOptions;

    /// <summary>替换时间范围选项清单并通知（D-020：预设装载/增删后调用，同 UpdateAgentOptions 套路）。
    /// 选中项按 Id 在新清单中找回（选项稳定实例契约）；当前档不在新清单（预设被删）时如实回落默认档。</summary>
    public void UpdateTimeRangeOptions(IReadOnlyList<TimeRangeOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RunOnUi(() =>
        {
            if (ReferenceEquals(_timeRangeOptions, options))
            {
                return;
            }

            _timeRangeOptions = options;
            OnPropertyChanged(nameof(TimeRangeOptions));

            var current = options.FirstOrDefault(o => o.Equals(_timeRange));
            if (current is null)
            {
                if (ReferenceEquals(_timeRange, TimeRangeOption.Default))
                {
                    return;
                }

                _timeRange = TimeRangeOption.Default;
                OnPropertyChanged(nameof(TimeRange));
                OnPropertyChanged(nameof(IsCustomRange));
                OnPropertyChanged(nameof(Display));
            }
            else if (!ReferenceEquals(current, _timeRange))
            {
                _timeRange = current;
                OnPropertyChanged(nameof(TimeRange));
            }
        });
    }

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

    /// <summary>模型筛选（顶栏全局）。默认全部模型（不过滤）。查询值 = ModelFilterOption.Model（规范名）。</summary>
    public ModelFilterOption Model
    {
        get => _model;
        set => SetOnUi(() => SetProperty(ref _model, value));
    }

    /// <summary>项目筛选（顶栏全局）。默认全部项目（不过滤）。查询值 = ProjectFilterOption.Path（原始路径）。</summary>
    public ProjectFilterOption Project
    {
        get => _project;
        set => SetOnUi(() => SetProperty(ref _project, value));
    }

    /// <summary>模型选项清单（初始仅"全部模型"哨兵——诚实：真实清单未加载前不冒充选项）。</summary>
    public IReadOnlyList<ModelFilterOption> ModelOptions => _modelOptions;

    /// <summary>项目选项清单（初始仅"全部项目"哨兵，语义同 <see cref="ModelOptions"/>）。</summary>
    public IReadOnlyList<ProjectFilterOption> ProjectOptions => _projectOptions;

    /// <summary>筛选汇总展示串（如「最近 7 天 · 所有 Agent · 全部模型 · 全部项目」），任一维度变化都会通知。</summary>
    public string Display => $"{TimeRange.Label} · {Agent.Label} · {Model.Label} · {Project.Label}";

    private void OnTranslationsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Translations.CurrentLanguage))
        {
            // 时间档 Label 为计算属性（含预设的词表自动命名），先逐项显式刷新（选项实例自带 INPC，
            // 绑定项文案即时重译；旧静态构造器订阅随预设的生命周期化退场——D-020）。
            foreach (var option in _timeRangeOptions)
            {
                option.RefreshLabel();
            }

            // 语言切换广播全属性变更（INPC 空串约定）：各页 VM 的 IsNullOrEmpty 分支按「整页重查
            // 重渲染」消费——Token 紧凑数值等单位随语言即时切换（选项 Label/Display 单发不足以驱动整页）。
            OnPropertyChanged(string.Empty);
        }
    }

    /// <summary>在 UI 线程执行 apply（非 UI 线程调用时 marshal，同属性 setter 语义）。</summary>
    private void RunOnUi(Action apply)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            apply();
        }
        else
        {
            Dispatcher.UIThread.Post(apply);
        }
    }

    /// <summary>替换 Agent 选项清单并通知（D-008：AgentCatalogService 拉到真实清单后调用）。
    /// 通知不走 SetProperty（其 CallerMemberName 在方法内会解析成方法名），显式给出属性名。</summary>
    public void UpdateAgentOptions(IReadOnlyList<AgentOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RunOnUi(() =>
        {
            if (ReferenceEquals(_agentOptions, options))
            {
                return;
            }

            _agentOptions = options;
            OnPropertyChanged(nameof(AgentOptions));
        });
    }

    /// <summary>替换模型选项清单并通知（右栏撤除后顶栏 Model 下拉的数据源，同 Agent 装载路径）。</summary>
    public void UpdateModelOptions(IReadOnlyList<ModelFilterOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RunOnUi(() =>
        {
            if (ReferenceEquals(_modelOptions, options))
            {
                return;
            }

            _modelOptions = options;
            OnPropertyChanged(nameof(ModelOptions));
        });
    }

    /// <summary>替换项目选项清单并通知（右栏撤除后顶栏 Project 下拉的数据源，同 Agent 装载路径）。</summary>
    public void UpdateProjectOptions(IReadOnlyList<ProjectFilterOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RunOnUi(() =>
        {
            if (ReferenceEquals(_projectOptions, options))
            {
                return;
            }

            _projectOptions = options;
            OnPropertyChanged(nameof(ProjectOptions));
        });
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
