using System;
using CCUsage.Core.Models;

namespace CCUsage.App.Services;

/// <summary>
/// 全局筛选 → 查询参数的统一映射（D-008 抽出，此前内联在 OverviewViewModel.BuildQueryFilter）：
/// 时间窗口按本地自然日含端点、今天收口（7/30/90 天；All 双端无界）；Agent.Id 直传
/// （<see cref="AgentOption.AllId"/> → null 不过滤，查询服务语义为空白不过滤）。
/// 会话列表（I-029）的时间口径同样按 first_seen_at 的本地自然日比较，共用本映射。
/// internal 供页面 VM 与测试对账。
/// </summary>
internal static class GlobalFilterMapper
{
    /// <summary>全局筛选（时间范围 + 单选 Agent）→ <see cref="UsageQueryFilter"/>。</summary>
    public static UsageQueryFilter Build(TimeRangeOption range, AgentOption agent)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly? fromDay = range.Value switch
        {
            TimeRange.Last7Days => today.AddDays(-6),
            TimeRange.Last30Days => today.AddDays(-29),
            TimeRange.Last90Days => today.AddDays(-89),
            TimeRange.All => null,
            _ => null,
        };
        return new UsageQueryFilter
        {
            FromDay = fromDay is { } from ? UsageQueryFilter.FormatDay(from) : null,
            ToDay = fromDay is null ? null : UsageQueryFilter.FormatDay(today),
            Agent = agent.Id == AgentOption.AllId ? null : agent.Id,
        };
    }
}
