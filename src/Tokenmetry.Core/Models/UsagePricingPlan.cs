namespace Tokenmetry.Core.Models;

/// <summary>
/// 候选链的匹配走向（I-024，对应蓝本各源计价调用点的查找顺序）。
/// </summary>
public enum PricingCandidateWalk
{
    /// <summary>
    /// 逐候选走完整匹配（用户覆盖 → 远程精确 → 快照精确 + 家族模式），首个命中生效——
    /// 蓝本 calculate_cost_for_usage_at(Some(candidate)) 逐候选尝试的口径
    /// （OpenCode / Goose / Hermes / Kilo / Codebuff / pi-agent / OpenClaw 裸名 / Antigravity 非 flash）。
    /// </summary>
    PerCandidateFull,

    /// <summary>
    /// 先全候选精确匹配（按候选序），全未命中再全候选走家族模式兜底——蓝本 grok
    /// calculate_grok_cost_at 的"exact 全候选优先、再 fuzzy"（GK-1：防首候选模糊命中遮蔽后续候选的
    /// 精确条目）；OpenClaw OC-1 的"装饰名 find_exact → 裸名 find"两级链同构（装饰名仅精确参与）。
    /// </summary>
    ExactThenFamilyAcrossCandidates,

    /// <summary>
    /// 仅全候选精确匹配（按候选序），不落家族模式——蓝本 antigravity gemini flash 变体的
    /// find_exact_pricing_with_alias 口径（exact-with-fallback，逐候选首个命中生效）。
    /// </summary>
    ExactOnlyAcrossCandidates,
}

/// <summary>
/// 统一记录的"计价用侧数据"（I-024，方案 A）：候选链只影响成本计算，不影响分组 / 显示 / 落库
/// （<see cref="UsageRecord.Model"/> 继续承载展示口径，facts 映射不读本计划）。
/// 各源适配器在映射时按蓝本候选链（open_code_model_candidates 等）填充；为 null 时按
/// <see cref="UsageRecord.Model"/> 单候选计价（接线前现状）。
/// </summary>
/// <param name="ModelCandidates">有序候选链（去重保序）；空列表视为无候选（按显示模型名单候选）。</param>
/// <param name="Walk">候选链匹配走向（蓝本各源查找顺序，见 <see cref="PricingCandidateWalk"/>）。</param>
/// <param name="OutputTokenOverride">
/// 计价 output 分量覆盖（AG-1：Antigravity 蓝本计价 output 取 total_output 含 reasoning，而统一记录
/// OutputTokens 为 visible 口径）；null = 按 token 明细的 OutputTokens 计。
/// </param>
public sealed record UsagePricingPlan(
    IReadOnlyList<string> ModelCandidates,
    PricingCandidateWalk Walk = PricingCandidateWalk.PerCandidateFull,
    long? OutputTokenOverride = null)
{
    /// <summary>单候选计划（显示模型名自身，现状口径）。</summary>
    public static UsagePricingPlan Single(string model)
    {
        return new UsagePricingPlan([model]);
    }
}
