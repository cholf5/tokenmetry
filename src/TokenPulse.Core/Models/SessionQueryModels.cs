namespace TokenPulse.Core.Models;

/// <summary>会话列表排序字段（I-029）。</summary>
public enum SessionSortField
{
    /// <summary>最近活动（last_seen_at）；默认排序。</summary>
    LastSeenAt,

    /// <summary>总 token（total_tokens）。</summary>
    TotalTokens,

    /// <summary>成本（cost_usd，已知成本口径；未知成本会话视为最小值——降序时排在已知成本之后，不折 0 参与比较）。</summary>
    Cost,
}

/// <summary>
/// 会话明细列表查询（I-029）：全局过滤（时间范围 + Agent）+ 搜索 + 组合筛选 + 排序 + 分页。
/// </summary>
public sealed class SessionListQuery
{
    /// <summary>全局过滤（时间范围按会话开始日 first_seen_at 的本地自然日；Agent 过滤）。</summary>
    public UsageQueryFilter Filter { get; init; } = new();

    /// <summary>
    /// 搜索词（大小写不敏感的包含匹配），命中任一字段即返回：SessionId、Agent、项目原始路径、
    /// 项目展示名、会话主模型。<b>标题口径说明</b>：session_facts 无标题字段（I-002），标题搜索
    /// 落在 SessionId（会话标识）上；标题数据源引入后（D-008）在此扩展，契约不变。
    /// </summary>
    public string? SearchTerm { get; init; }

    /// <summary>项目筛选：原始路径精确匹配。</summary>
    public string? Project { get; init; }

    /// <summary>模型筛选：会话主模型（dominant_model）精确匹配。</summary>
    public string? Model { get; init; }

    public SessionSortField SortBy { get; init; } = SessionSortField.LastSeenAt;

    public bool SortDescending { get; init; } = true;

    /// <summary>页码（1 起；小于 1 视为 1）。</summary>
    public int Page { get; init; } = 1;

    /// <summary>
    /// 页大小；null = 不分页（返回全部）；&lt;= 0 非法——调用 <see cref="TokenPulse.Core.Abstractions.IUsageQueryService.GetSessionsAsync"/>
    /// 时抛 <see cref="ArgumentException"/>（审查 P2 契约化：静默当作"不分页"会掩盖调用方错误）。
    /// </summary>
    public int? PageSize { get; init; } = 50;
}

/// <summary>会话明细列表的一行（session_facts 行的查询投影；成本契约同 I-028）。</summary>
public sealed class SessionListRow
{
    /// <summary>会话 id（去重口径 = Agent + Session ID 的一半；facts 主键 session_id 与该口径等价，见 I-029 Development Log）。</summary>
    public string SessionId { get; init; } = string.Empty;

    public string Agent { get; init; } = string.Empty;

    /// <summary>项目原始路径。</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>项目展示名（路径末段，采集层归一化；可能为空串）。</summary>
    public string ProjectDisplayName { get; init; } = string.Empty;

    /// <summary>会话主模型（token 占比最高）。</summary>
    public string DominantModel { get; init; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastSeenAt { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CacheCreationTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long TotalTokens { get; init; }

    /// <summary>
    /// 会话成本。null = 会话内全部记录成本未知（facts 行语义：cost_usd 仅在全未知时为 null）；
    /// 非 null = 已知成本之和——会话内混合已知/未知时 facts 层无法再分辨未知残留（I-002 存储语义）。
    /// </summary>
    public decimal? CostUsd { get; init; }

    /// <summary><see cref="CostUsd"/> 为 null（全部成本未知）时的便捷标记；与 I-028 的 null ≠ 0 契约同源。</summary>
    public bool CostIsUnknown => CostUsd is null;
}

/// <summary>会话列表结果：当前页 + 过滤后的总数（供分页控件）。</summary>
public sealed class SessionListResult
{
    public IReadOnlyList<SessionListRow> Rows { get; init; } = Array.Empty<SessionListRow>();

    /// <summary>过滤（搜索 / 筛选 / 时间范围）后的总会话数，与分页无关。</summary>
    public long TotalCount { get; init; }

    public int Page { get; init; }

    /// <summary>生效页大小（null = 未分页）。</summary>
    public int? PageSize { get; init; }
}
