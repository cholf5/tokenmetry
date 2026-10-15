namespace Tokenmetry.Core.Models;

/// <summary>
/// 日粒度用量点（I-028 日序列的一行）：按日聚合的 in/out/cacheRead/cacheWrite/total token 与成本。
/// 日历窗口语义（D-002 定案，见 I-028 Development Log 2026-10-13）：范围内无数据的日子<b>补零</b>
/// 而不是断线 / 只画实际日——稀疏日期（如 9 个数据日横跨 8 个月）在"仅实际日"下会把长间隔画成
/// 连续花费，补零让 x 轴回归真实日历；补零点用 <see cref="HasData"/>=false 标记，UI 图层
/// （淡化 marker / 空心点）可辨识，不会与真实 0 用量日混淆。
/// </summary>
public sealed class DailyUsagePoint
{
    /// <summary>自然日（yyyy-MM-dd，本地时区）。</summary>
    public string Day { get; init; } = string.Empty;

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CacheCreationTokens { get; init; }

    public long CacheReadTokens { get; init; }

    /// <summary>总 token（来源口径，与 ccusage / facts total_tokens 一致）。</summary>
    public long TotalTokens { get; init; }

    /// <summary>当日成本（未知成本契约见 <see cref="AggregatedCost"/>：null ≠ 0，未知不折 0）。</summary>
    public AggregatedCost Cost { get; init; } = AggregatedCost.Empty;

    /// <summary>
    /// 当日是否有 facts 数据。false = 范围内补零的空日（token 为 0、成本为 <see cref="AggregatedCost.Empty"/>）；
    /// true = 有真实 facts（token 数值可为 0——例如只有 0 token 记录的日子，仍按有数据处理）。
    /// </summary>
    public bool HasData { get; init; }
}
