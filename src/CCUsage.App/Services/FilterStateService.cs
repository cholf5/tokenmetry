using System;
using System.Collections.Generic;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CCUsage.App.Services;

/// <summary>全局时间范围（D-003 顶部栏筛选，对应原型 #rangeTop 的 7/30/90/全部四档）。</summary>
public enum TimeRange
{
    Last7Days,
    Last30Days,
    Last90Days,
    All,
}

/// <summary>时间范围选项（ComboBox 项；Label 对齐原型选项文案）。值相等性为记录值相等，供 SelectedItem 绑定比较。</summary>
public sealed record TimeRangeOption(TimeRange Value, string Label)
{
    public static IReadOnlyList<TimeRangeOption> Options { get; } =
    [
        new(TimeRange.Last7Days, "最近 7 天"),
        new(TimeRange.Last30Days, "最近 30 天"),
        new(TimeRange.Last90Days, "最近 90 天"),
        new(TimeRange.All, "全部时间"),
    ];

    /// <summary>默认时间范围：最近 7 天（对齐原型 #rangeTop 默认选中与初始渲染 renderTrend(7)；D-003 审查后由 30 天改齐）。</summary>
    public static TimeRangeOption Default { get; } = Options[0];

    public override string ToString() => Label;
}

/// <summary>Agent 筛选选项。选项清单目前为占位（对齐原型 #agentTop），真实 Agent 清单由 D-008 接入。</summary>
public sealed record AgentOption(string Id, string Label)
{
    /// <summary>"所有 Agent"（不过滤）的固定标识。</summary>
    public const string AllId = "all";

    public static IReadOnlyList<AgentOption> Options { get; } =
    [
        new(AllId, "所有 Agent"),
        new("Claude Code", "Claude Code"),
        new("Codex", "Codex"),
        new("Cursor", "Cursor"),
        new("GitHub Copilot", "GitHub Copilot"),
        new("Cline", "Cline"),
    ];

    public static AgentOption All { get; } = Options[0];

    public override string ToString() => Label;
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

    /// <summary>时间范围选项（顶部栏 ComboBox 数据源）。</summary>
    public IReadOnlyList<TimeRangeOption> TimeRangeOptions => TimeRangeOption.Options;

    /// <summary>Agent 选项（顶部栏 ComboBox 数据源，占位清单）。</summary>
    public IReadOnlyList<AgentOption> AgentOptions => AgentOption.Options;

    /// <summary>时间范围。默认最近 7 天。</summary>
    public TimeRangeOption TimeRange
    {
        get => _timeRange;
        set => SetOnUi(() => SetProperty(ref _timeRange, value));
    }

    /// <summary>Agent 筛选。默认所有 Agent（不过滤）。</summary>
    public AgentOption Agent
    {
        get => _agent;
        set => SetOnUi(() => SetProperty(ref _agent, value));
    }

    /// <summary>筛选汇总展示串（如「最近 7 天 · 所有 Agent」），任一维度变化都会通知。</summary>
    public string Display => $"{TimeRange.Label} · {Agent.Label}";

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
