namespace Tokenmetry.Core.Models;

/// <summary>
/// 维度聚合行（I-029，Agent / 模型 / 项目三维度共用形状）：会话数、输入/输出/总 token、成本、占比。
/// token 与成本来自 usage_facts（按日 full 粒度优先，见 <see cref="UsageFactGrain"/>）；
/// 会话数来自 session_facts（会话去重口径 = Agent + Session ID；模型维度按 dominant_model 归属）。
/// </summary>
public sealed class DimensionUsageRow
{
    /// <summary>维度键：Agent 名 / 模型名 / 项目<b>原始路径</b>（项目维度始终保留原始路径作为稳定键）。</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// 归一化展示名。仅项目维度填充：路径末段（与采集层 DeriveProjectDisplayName 同式）；
    /// 归一化歧义守卫（对齐 I-025）：同一结果集内多个不同原始路径归一化出相同展示名时，
    /// 相关行退回显示<b>原始路径</b>（宁可原始也不猜，绝不合并不同项目）。
    /// Agent / 模型维度为 null（Key 即展示名）。
    /// </summary>
    public string? DisplayName { get; init; }

    public long SessionCount { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CacheCreationTokens { get; init; }

    public long CacheReadTokens { get; init; }

    /// <summary>总 token（来源口径，与 ccusage / facts total_tokens 一致）。</summary>
    public long TotalTokens { get; init; }

    /// <summary>成本（未知成本契约同 I-028：<see cref="AggregatedCost"/>，null ≠ 0）。</summary>
    public AggregatedCost Cost { get; init; } = AggregatedCost.Empty;

    /// <summary>token 占比 = 本行 TotalTokens / 范围内全部维度行之和（0..1）；分母为 0 时 null。</summary>
    public decimal? TokenShare { get; init; }

    /// <summary>
    /// 主用 Agent（仅模型维度填充，D-010）：把该模型作为会话主模型（dominant_model）的会话按 Agent
    /// 分组取会话数最多者，并列取键序数最小。数据源与 <see cref="SessionCount"/> 同一批 session_facts
    /// （口径一致）。为 null 的三种情形：无会话数据（如纯 CLI 源只有 facts 行）；或该模型的会话中
    /// 无有效 Agent 归属（空白 Agent 键被跳过）；或非模型维度。Agent / 项目维度恒为 null。
    /// </summary>
    public string? DominantAgent { get; init; }

    /// <summary>
    /// 使用该模型的 Agent 数（仅模型维度填充，与 <see cref="DominantAgent"/> 同源）：1 = 唯一 Agent；
    /// &gt;1 = 多 Agent 共用（展示层据此决定「主用 Agent」或聚合文案）；无会话数据为 null。
    /// Agent / 项目维度恒为 null。
    /// </summary>
    public int? AgentCount { get; init; }

    /// <summary>
    /// 该维度行用到的 provider（仅模型维度填充，I-036）：唯一值时才非空，多个不同 provider 时为空串
    /// （展示层按 <see cref="ProviderCount"/> 出「N 个」）。空串 = 数据里没有 provider 观测（未检测，
    /// 界面显示"—"）。Agent / 项目维度恒为空串。
    /// </summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>该维度行涉及的 provider 个数（仅模型维度填充；0 = 无 provider 观测）。</summary>
    public int ProviderCount { get; init; }

    /// <summary>
    /// 成本占比 = 本行已知成本 / 范围已知成本合计（0..1）。本行含未知成本（<see cref="AggregatedCost.HasUnknownCost"/>）
    /// 或范围已知成本合计为 0 / 无已知成本时为 null——未知部分不可比，不伪造占比。
    /// </summary>
    public decimal? CostShare { get; init; }
}
