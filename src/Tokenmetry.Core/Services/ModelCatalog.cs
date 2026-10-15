using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Services;

/// <summary>一次身份解析的结果（I-035）：<see cref="Id"/> 是聚合与计价共用的规范身份。</summary>
/// <param name="Id">规范模型 ID；未关联时是别名解析后的原始名（保持"不猜"）。</param>
/// <param name="LookupName">别名解析后的原始拼写（未命中也原样保留）——导入两层价格匹配按它进行：
/// 快照的家族模式是按拼写做包含判定，归一化 ID 会打坏点号条目的命中。</param>
/// <param name="Definition">命中的目录条目；未关联为 null。</param>
/// <param name="Kind">关联方式（别名 / 精确 / 家族 / 未关联）。</param>
public readonly record struct ModelIdentity(
    string Id,
    string LookupName,
    ModelDefinition? Definition,
    ModelMatchKind Kind)
{
    /// <summary>是否已关联到目录条目。</summary>
    public bool IsLinked => Kind != ModelMatchKind.None;
}

/// <summary>
/// 模型目录（I-035）：全应用<b>唯一的模型身份出处</b>——把三层来源合成一份"模型配置表"，
/// 并提供 原始名 → 模型 ID 的解析，使聚合、计价、厂商 / 图标读同一个身份。
/// <list type="bullet">
/// <item><b>三层合成</b>：内置快照（离线兜底 / 对账锚）→ models.dev 远程（按 ID 覆盖快照值，但保留
/// 快照的家族匹配规则）→ 用户条目（逐字段覆盖）。<see cref="Definitions"/> 是合成后的表（UI + 身份解析用）。</item>
/// <item><b>计价分层不合并</b>：<see cref="UserEntries"/> / <see cref="RemoteEntries"/> /
/// <see cref="SnapshotEntries"/> 三份计价条目保持分层与既有回退顺序——合并成一份会丢掉
/// "远程精确 + 快照家族"的分工（对账口径，I-025 锚点纪律）。</item>
/// <item><b>身份解析</b>（<see cref="ResolveIdentity"/>）：用户别名（精确、大小写敏感，优先级最高）
/// → ID / 来源键精确命中 → 归一化 ID（点号 / 斜杠折连字符）→ 候选名（去 provider 前缀、去 -fast 采集标记）
/// 逐个命中。全部收敛都是"对<b>已知</b>条目的判定"，不做拼写近似；未命中原样返回——
/// 这正是"数据里出现的名字都能在配置表里找到关联，找不到就等用户手动关联"的落点。</item>
/// <item>线程模型：更新（设置保存 / 远程刷新）整体换快照，读取侧无锁；与
/// <c>PricingEngine.UpdateOverrides</c>、<c>ModelAliasResolver.UpdateAliases</c> 同纪律。</item>
/// </list>
/// </summary>
public sealed class ModelCatalog : IModelNameResolver
{
    private readonly IRemotePricingSource? _remoteSource;
    private readonly IReadOnlyList<ModelPriceEntry> _snapshotEntries;
    private readonly ModelAliasResolver _aliasResolver = new();

    /// <summary>用户条目原始配置（未装订）；装订后的表在 <see cref="_state"/> 里。</summary>
    private volatile ModelDefinitionOverride[] _userOverrides = [];

    private volatile State _state;

    public ModelCatalog(
        IRemotePricingSource? remoteSource = null,
        IEnumerable<ModelDefinitionOverride>? userDefinitions = null,
        IEnumerable<KeyValuePair<string, string>>? aliases = null,
        IReadOnlyList<ModelPriceEntry>? snapshotEntries = null)
    {
        _remoteSource = remoteSource;
        _snapshotEntries = snapshotEntries ?? BuiltinPricingSnapshot.Entries;
        _aliasResolver.UpdateAliases(aliases ?? []);
        _userOverrides = NormalizeOverrides(userDefinitions);
        _state = Build();
    }

    /// <summary>合成后的模型配置表（按 ID 序数排序；含无价条目——无价 ≠ 不存在）。</summary>
    public IReadOnlyList<ModelDefinition> Definitions
    {
        get
        {
            EnsureCurrent();
            return _state.Definitions;
        }
    }

    /// <summary>用户层计价条目（有价的用户覆盖 / 自建条目），计价第一档。</summary>
    public IReadOnlyList<ModelPriceEntry> UserEntries => _state.UserEntries;

    /// <summary>models.dev 远程条目（计价第二档）。</summary>
    public IReadOnlyList<ModelPriceEntry> RemoteEntries
    {
        get
        {
            EnsureCurrent();
            return _state.RemoteEntries;
        }
    }

    /// <summary>内置快照条目（计价第三档，含家族匹配规则）。</summary>
    public IReadOnlyList<ModelPriceEntry> SnapshotEntries => _state.SnapshotEntries;

    /// <summary>用户条目原始配置（设置页编辑对象的回读；含未生效的无效条目）。</summary>
    public IReadOnlyList<ModelDefinitionOverride> UserOverrides => _userOverrides;

    /// <summary>替换用户条目（设置保存后调用；线程安全）。无效条目（ID 不合约定）剔除。</summary>
    public void UpdateUserDefinitions(IEnumerable<ModelDefinitionOverride>? userDefinitions)
    {
        _userOverrides = NormalizeOverrides(userDefinitions);
        _state = Build();
    }

    /// <summary>替换别名映射（原始名 → 模型 ID；设置保存后调用；线程安全）。</summary>
    public void UpdateAliases(IEnumerable<KeyValuePair<string, string>>? aliases)
    {
        _aliasResolver.UpdateAliases(aliases ?? []);
        _state = Build();
    }

    /// <summary>按 ID 取条目（含大小写 / 归一化折叠）；未登记返回 null。</summary>
    public ModelDefinition? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        EnsureCurrent();
        return _state.Lookup.TryGetValue(id.Trim(), out var definition)
            ? definition
            : _state.Lookup.TryGetValue(ModelId.Normalize(id), out var normalized)
                ? normalized
                : null;
    }

    /// <summary>原始模型名 → 身份（<see cref="IModelNameResolver"/> 实现：未关联时即别名解析结果）。</summary>
    public string Resolve(string rawModelName) => ResolveIdentity(rawModelName).Id;

    /// <summary>
    /// 原始模型名 → 身份（含命中条目与关联方式）。解析次序见类型说明；
    /// 未关联时 <see cref="ModelIdentity.Id"/> 为别名解析后的名字，不变更原始名的拼写。
    /// </summary>
    public ModelIdentity ResolveIdentity(string? rawModelName)
    {
        if (string.IsNullOrWhiteSpace(rawModelName))
        {
            return new ModelIdentity(rawModelName ?? string.Empty, rawModelName ?? string.Empty, null, ModelMatchKind.None);
        }

        EnsureCurrent();
        var state = _state;

        // 1) 用户别名规则优先（精确、大小写敏感；链式跟进由 ModelAliasResolver 承担）。
        var aliased = _aliasResolver.Resolve(rawModelName);
        if (!string.Equals(aliased, rawModelName.Trim(), StringComparison.Ordinal))
        {
            // 规则命中即用户显式指派：无论目录里有没有对应条目，都按规则结果聚合（保持既有语义）；
            // 目录里恰有该名字时把条目一并带出（价格 / 图标跟着走），关联方式仍如实记为「别名」。
            var aliasHit = LookupByName(state, aliased);
            return new ModelIdentity(aliasHit?.Id ?? aliased, aliased, aliasHit, ModelMatchKind.Alias);
        }

        var hit = LookupByName(state, aliased);
        if (hit is not null)
        {
            return new ModelIdentity(hit.Id, aliased, hit, ModelMatchKind.Exact);
        }

        // 2) 家族模式（仅快照规则）：候选名包含 pattern 即命中——带日期后缀的历史模型名走这里。
        var identity = PricingModelMatcher.GetCandidates(aliased);
        foreach (var candidate in identity)
        {
            var familyHit = LookupFamily(state, candidate);
            if (familyHit is not null)
            {
                return new ModelIdentity(familyHit.Id, aliased, familyHit, ModelMatchKind.Family);
            }
        }

        return new ModelIdentity(aliased, aliased, null, ModelMatchKind.None);
    }

    /// <summary>按名字（原文 / 归一化 / 候选名）查条目：三条路都只认"已知条目"，不猜。</summary>
    private static ModelDefinition? LookupByName(State state, string name)
    {
        if (state.Lookup.TryGetValue(name.Trim(), out var exact))
        {
            return exact;
        }

        var normalized = ModelId.Normalize(name);
        if (normalized.Length > 0 && state.Lookup.TryGetValue(normalized, out var byNormalized))
        {
            return byNormalized;
        }

        // 候选名（去 provider 前缀 / 去 "[pi] " 装饰 / 去 -fast 采集标记）逐个试——
        // 与计价候选链同源口径，保证"能计价的名字"与"能聚合的名字"是同一套判定。
        foreach (var candidate in PricingModelMatcher.GetCandidates(name))
        {
            if (state.Lookup.TryGetValue(candidate, out var byCandidate))
            {
                return byCandidate;
            }

            var candidateNormalized = ModelId.Normalize(candidate);
            if (candidateNormalized.Length > 0 && state.Lookup.TryGetValue(candidateNormalized, out var byCandidateNormalized))
            {
                return byCandidateNormalized;
            }
        }

        return null;
    }

    /// <summary>家族模式命中（仅带 <c>MatchPattern</c> 的条目），取最长模式（与既有计价口径一致）。</summary>
    private static ModelDefinition? LookupFamily(State state, string candidate)
    {
        ModelDefinition? best = null;
        var bestLength = -1;
        foreach (var definition in state.Definitions)
        {
            var pattern = definition.MatchPattern;
            if (string.IsNullOrEmpty(pattern)
                || pattern.Length <= bestLength
                || !candidate.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            best = definition;
            bestLength = pattern.Length;
        }

        return best;
    }

    /// <summary>远程快照换过对象（后台刷新）时重建合成表；未换时 O(1) 直读。</summary>
    private void EnsureCurrent()
    {
        if (_remoteSource is null)
        {
            return;
        }

        var state = _state;
        if (!ReferenceEquals(state.Remote, _remoteSource.Current))
        {
            _state = Build();
        }
    }

    private static ModelDefinitionOverride[] NormalizeOverrides(IEnumerable<ModelDefinitionOverride>? userDefinitions)
    {
        return (userDefinitions ?? [])
            .Where(definition => definition is not null && definition.IsValid)
            .ToArray();
    }

    private State Build()
    {
        var remote = _remoteSource?.Current;
        var remoteEntries = remote?.Entries ?? Array.Empty<ModelPriceEntry>();
        var byId = new Dictionary<string, ModelDefinition>(StringComparer.OrdinalIgnoreCase);

        // 第一层：内置快照。
        foreach (var entry in _snapshotEntries)
        {
            var definition = ModelDefinition.FromPriceEntry(entry, ModelDefinitionOrigin.Snapshot, sourceKey: null);
            if (definition.Id.Length > 0)
            {
                byId[definition.Id] = definition;
            }
        }

        // 第二层：远程（按 ID 覆盖价格 / 显示名，但保留快照的家族匹配规则——那是历史名命中的唯一依据）。
        foreach (var entry in remoteEntries)
        {
            var definition = ModelDefinition.FromPriceEntry(entry, ModelDefinitionOrigin.Remote, sourceKey: null);
            if (definition.Id.Length == 0)
            {
                continue;
            }

            if (byId.TryGetValue(definition.Id, out var snapshot) && snapshot.MatchPattern is { } pattern)
            {
                definition = CloneWithMatchPattern(definition, pattern);
            }

            byId[definition.Id] = definition;
        }

        // 第三层：用户条目（逐字段覆盖；ID 不在导入层即用户自建条目）。
        foreach (var definition in _userOverrides)
        {
            var id = ModelId.Normalize(definition.Id);
            byId.TryGetValue(id, out var imported);
            byId[id] = definition.Bind(imported);
        }

        var definitions = byId.Values
            .OrderBy(definition => definition.Id, StringComparer.Ordinal)
            .ToList();

        // 名字索引：ID、来源键、以及导入层的原始模型 id（点号 / 大小写变体的收敛入口）。
        var lookup = new Dictionary<string, ModelDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            lookup[definition.Id] = definition;
            if (!string.IsNullOrWhiteSpace(definition.SourceKey))
            {
                lookup[definition.SourceKey!] = definition;
            }
        }

        foreach (var entry in _snapshotEntries.Concat(remoteEntries))
        {
            if (byId.TryGetValue(ModelId.Normalize(entry.ModelId), out var definition))
            {
                lookup[entry.ModelId] = definition;
            }
        }

        // 用户计价条目：取装订后的最终定义（含用户价与合并后的元数据），但清掉家族模式——
        // 用户条目的语义是"这个 ID 就是这价"，必须走精确档。
        var userEntries = _userOverrides
            .Select(definition => byId.GetValueOrDefault(ModelId.Normalize(definition.Id)))
            .Where(definition => definition is not null)
            .Select(definition => definition!.ToPriceEntry(includeMatchPattern: false))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToList();

        return new State(definitions, lookup, userEntries, remoteEntries.ToList(), _snapshotEntries, remote);
    }

    private static ModelDefinition CloneWithMatchPattern(ModelDefinition definition, string pattern)
    {
        return new ModelDefinition
        {
            Id = definition.Id,
            Vendor = definition.Vendor,
            DisplayName = definition.DisplayName,
            LogoVendor = definition.LogoVendor,
            SourceKey = definition.SourceKey,
            Origin = definition.Origin,
            IsPriceOverridden = definition.IsPriceOverridden,
            IsMetadataOverridden = definition.IsMetadataOverridden,
            HasPrice = definition.HasPrice,
            InputPerMillion = definition.InputPerMillion,
            OutputPerMillion = definition.OutputPerMillion,
            CacheWritePerMillion = definition.CacheWritePerMillion,
            CacheReadPerMillion = definition.CacheReadPerMillion,
            LongContextThreshold = definition.LongContextThreshold,
            InputAboveThresholdPerMillion = definition.InputAboveThresholdPerMillion,
            OutputAboveThresholdPerMillion = definition.OutputAboveThresholdPerMillion,
            CacheWriteAboveThresholdPerMillion = definition.CacheWriteAboveThresholdPerMillion,
            CacheReadAboveThresholdPerMillion = definition.CacheReadAboveThresholdPerMillion,
            MatchPattern = pattern,
        };
    }

    /// <summary>一次合成的不变快照（整体替换，读取侧无锁）。</summary>
    private sealed record State(
        IReadOnlyList<ModelDefinition> Definitions,
        Dictionary<string, ModelDefinition> Lookup,
        IReadOnlyList<ModelPriceEntry> UserEntries,
        IReadOnlyList<ModelPriceEntry> RemoteEntries,
        IReadOnlyList<ModelPriceEntry> SnapshotEntries,
        RemotePricingSnapshot? Remote);
}
