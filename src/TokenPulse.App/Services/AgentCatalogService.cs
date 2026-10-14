using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;
using TokenPulse.App.Localization;

namespace TokenPulse.App.Services;

/// <summary>项目筛选下拉的单个选项：查询值 = 项目原始路径（I-029 SessionListQuery.Project 精确匹配），
/// 展示名 = 归一化项目名（歧义守卫下可能是原始路径，见 DimensionUsageRow.DisplayName）。null 路径 = 全部项目。
/// 稳定实例 + 实时 Label（"全部项目"哨兵为 UI 文案经 Translations 取串，真实项目名为数据）；
/// 相等性只看 <see cref="Path"/>——ComboBox 重建选项清单按值相等保持选中态的契约不变，语言切换亦不改变相等性。</summary>
public sealed class ProjectFilterOption : INotifyPropertyChanged, IEquatable<ProjectFilterOption>
{
    public ProjectFilterOption(string? path, string? label = null)
    {
        Path = path;
        _label = label;
    }

    public string? Path { get; }

    private readonly string? _label;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label => Path is null ? Translations.Instance.AllProjects : (_label ?? Path);

    /// <summary>语言切换后刷新哨兵展示名。</summary>
    public void RefreshLabel() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

    /// <summary>"全部项目"（不过滤）。单例复用：ComboBox 重建选项清单时按 Path 值相等保持选中态。</summary>
    public static ProjectFilterOption All { get; } = new(null);

    public override string ToString() => Label;

    public bool Equals(ProjectFilterOption? other) => other is not null && Path == other.Path;

    public override bool Equals(object? obj) => obj is ProjectFilterOption other && Equals(other);

    public override int GetHashCode() => Path?.GetHashCode(StringComparison.Ordinal) ?? 0;

    static ProjectFilterOption()
    {
        Translations.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Translations.CurrentLanguage))
            {
                All.RefreshLabel();
            }
        };
    }
}

/// <summary>模型筛选下拉的单个选项：查询值 = 会话主模型（dominant_model 精确匹配）。null = 全部模型。
/// 机制同 <see cref="ProjectFilterOption"/>（哨兵 Label 实时取串，相等性只看 <see cref="Model"/>）。</summary>
public sealed class ModelFilterOption : INotifyPropertyChanged, IEquatable<ModelFilterOption>
{
    public ModelFilterOption(string? model, string? label = null)
    {
        Model = model;
        _label = label;
    }

    public string? Model { get; }

    private readonly string? _label;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label => Model is null ? Translations.Instance.AllModels : (_label ?? Model);

    /// <summary>语言切换后刷新哨兵展示名。</summary>
    public void RefreshLabel() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

    public static ModelFilterOption All { get; } = new(null);

    public override string ToString() => Label;

    public bool Equals(ModelFilterOption? other) => other is not null && Model == other.Model;

    public override bool Equals(object? obj) => obj is ModelFilterOption other && Equals(other);

    public override int GetHashCode() => Model?.GetHashCode(StringComparison.Ordinal) ?? 0;

    static ModelFilterOption()
    {
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
/// 筛选选项目录服务（D-008 定案：Agent 清单接线）：从 I-029 维度聚合查询拉取真实的
/// Agent / 模型 / 项目清单，供全局 Agent 下拉（FilterStateService.UpdateAgentOptions 单一真相源）
/// 与右栏快速筛选（复选组 / 模型 / 项目下拉）共用。
/// 缓存策略：首次请求时查询并缓存整个应用生命周期——页面/右栏每次导航初始化都调用，
/// 但只有第一次真正打查询服务（避免每次导航重拉；facts 变化由后台刷新后的下次启动体现，
/// 选项清单陈旧一个会话周期是可接受取舍，记录于 D-008 Dev Log）。
/// 查询用无界 <see cref="UsageQueryFilter"/>（全部时间、全部 Agent）——选项清单描述数据全集，
/// 不随当前筛选收窄；排序按 token 降序（防御性重排，不依赖服务排序契约）。
/// <b>无界超集语义是契约的一部分（审查修复轮 P2 锁定，勿收窄）</b>：D-009/D-011 外部跳转
/// （OpenSessionsForAgent / ApplyExternalProjectFilter）对"快照缺项"的判重补入，依赖本清单
/// 恒为无界严格超集 + 选项 record 值相等重绑才保持幂等——目录查询一旦随筛选收窄，补入兜底即失效。
/// 加载失败抛出给调用方（外壳把 Agent 清单失败处理为保持 [所有 Agent]，诚实不伪造）。
/// </summary>
public sealed class AgentCatalogService
{
    private readonly IUsageQueryService _query;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<AgentOption>? _agentOptions;
    private IReadOnlyList<ModelFilterOption>? _modelOptions;
    private IReadOnlyList<ProjectFilterOption>? _projectOptions;

    public AgentCatalogService(IUsageQueryService query)
    {
        _query = query;
    }

    /// <summary>
    /// Agent 选项清单：[所有 Agent, ...真实 Agent（token 降序）]。"所有 Agent"（不过滤）是
    /// UI 语义选项（<see cref="AgentOption.AllId"/>），真实 Agent 键来自
    /// <see cref="IUsageQueryService.GetAgentBreakdownAsync"/>。零 token 的 Agent 也保留
    /// （facts 中存在即真实来源），排序键 token 降序、平局按 Key 序数。
    /// </summary>
    public async Task<IReadOnlyList<AgentOption>> GetAgentOptionsAsync(CancellationToken ct = default)
    {
        if (_agentOptions is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_agentOptions is { } again)
            {
                return again;
            }

            var rows = await _query.GetAgentBreakdownAsync(new UsageQueryFilter(), ct);
            var agents = rows
                .OrderByDescending(r => r.TotalTokens)
                .ThenBy(r => r.Key, StringComparer.Ordinal)
                .Select(r => new AgentOption(r.Key, r.Key))
                .ToList();
            _agentOptions = [AgentOption.All, .. agents];
            return _agentOptions;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>模型选项清单：[全部模型, ...会话主模型（token 降序）]。</summary>
    public async Task<IReadOnlyList<ModelFilterOption>> GetModelOptionsAsync(CancellationToken ct = default)
    {
        if (_modelOptions is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_modelOptions is { } again)
            {
                return again;
            }

            var rows = await _query.GetModelBreakdownAsync(new UsageQueryFilter(), ct);
            _modelOptions =
            [
                ModelFilterOption.All,
                .. rows
                    .OrderByDescending(r => r.TotalTokens)
                    .ThenBy(r => r.Key, StringComparer.Ordinal)
                    .Select(r => new ModelFilterOption(r.Key, r.Key)),
            ];
            return _modelOptions;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>项目选项清单：[全部项目, ...项目（token 降序；展示归一化名、查询用原始路径）]。</summary>
    public async Task<IReadOnlyList<ProjectFilterOption>> GetProjectOptionsAsync(CancellationToken ct = default)
    {
        if (_projectOptions is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_projectOptions is { } again)
            {
                return again;
            }

            var rows = await _query.GetProjectBreakdownAsync(new UsageQueryFilter(), ct);
            _projectOptions =
            [
                ProjectFilterOption.All,
                .. rows
                    .OrderByDescending(r => r.TotalTokens)
                    .ThenBy(r => r.Key, StringComparer.Ordinal)
                    .Select(r => new ProjectFilterOption(r.Key, r.DisplayName ?? r.Key)),
            ];
            return _projectOptions;
        }
        finally
        {
            _gate.Release();
        }
    }
}
