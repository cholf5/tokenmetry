using System;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.Services;

/// <summary>
/// 全局筛选 → 查询参数的统一映射（D-008 抽出，此前内联在 OverviewViewModel.BuildQueryFilter）：
/// 时间窗口按本地自然日含端点、今天收口（今日；7/30/90 天；All 双端无界；Custom 按起止日期，
/// 未选侧不设界限、起止倒置自动换序、跨度超日序列物化上限时收口上限——见 Build 注释）；
/// Agent.Id 直传（<see cref="AgentOption.AllId"/> → null 不过滤，查询服务语义为空白不过滤）。
/// 会话列表（I-029）的时间口径同样按 first_seen_at 的本地自然日比较，共用本映射。
/// internal 供页面 VM 与测试对账。
/// </summary>
internal static class GlobalFilterMapper
{
    /// <summary>全局筛选状态 → <see cref="UsageQueryFilter"/>（页面 VM 统一入口；自定义档从状态读起止日期）。</summary>
    public static UsageQueryFilter Build(FilterStateService filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        return Build(filters.TimeRange, filters.Agent, filters.CustomFromDate, filters.CustomToDate);
    }

    /// <summary>全局筛选（时间范围 + 单选 Agent + 自定义起止）→ <see cref="UsageQueryFilter"/>（纯函数，internal 供测试对账）。</summary>
    public static UsageQueryFilter Build(
        TimeRangeOption range,
        AgentOption agent,
        DateTimeOffset? customFrom = null,
        DateTimeOffset? customTo = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly? fromDay;
        DateOnly? toDay;
        switch (range.Value)
        {
            case TimeRange.Today:
                fromDay = today;
                toDay = today;
                break;
            case TimeRange.Custom:
                // 未选侧不设界限（语义如实：没填 = 不限），两端都缺时等价 All；倒置换序保证 IsRangeValid，
                // 跨度收口到日序列物化上限（保留最近窗口），避免 GetDailySeriesAsync 抛 ArgumentOutOfRange。
                (fromDay, toDay) = NormalizeCustomRange(customFrom, customTo, today);
                break;
            case TimeRange.Last7Days:
                fromDay = today.AddDays(-6);
                toDay = today;
                break;
            case TimeRange.Last30Days:
                fromDay = today.AddDays(-29);
                toDay = today;
                break;
            case TimeRange.Last90Days:
                fromDay = today.AddDays(-89);
                toDay = today;
                break;
            case TimeRange.All:
            default:
                fromDay = null;
                toDay = null;
                break;
        }

        return new UsageQueryFilter
        {
            FromDay = fromDay is { } from ? UsageQueryFilter.FormatDay(from) : null,
            ToDay = toDay is { } to ? UsageQueryFilter.FormatDay(to) : null,
            Agent = agent.Id == AgentOption.AllId ? null : agent.Id,
        };
    }

    /// <summary>
    /// 自定义起止 → 有序自然日对：空侧不设界限（null 透传）；两端齐且倒置时换序（用户先点晚日期再补早日期
    /// 不应得到空结果）；跨度超过 <see cref="UsageQueryFilter.MaxDailySeriesDays"/> 时起点收口到
    /// 「终点 - 上限 + 1」（保留最近窗口，日序列补零物化才有硬上限可依）。
    /// </summary>
    internal static (DateOnly? From, DateOnly? To) NormalizeCustomRange(
        DateTimeOffset? customFrom, DateTimeOffset? customTo, DateOnly today)
    {
        var from = customFrom is { } f ? DateOnly.FromDateTime(f.Date) : (DateOnly?)null;
        var to = customTo is { } t ? DateOnly.FromDateTime(t.Date) : (DateOnly?)null;

        if (from is { } a && to is { } b)
        {
            if (a > b)
            {
                (from, to) = (b, a);
            }

            var spanDays = to!.Value.DayNumber - from!.Value.DayNumber + 1;
            if (spanDays > UsageQueryFilter.MaxDailySeriesDays)
            {
                from = to.Value.AddDays(-(UsageQueryFilter.MaxDailySeriesDays - 1));
            }
        }
        else if (from is { } onlyFrom && onlyFrom > today)
        {
            // 只选了起点且晚于今天：换序语义下等价把界限放到今天（未来无数据，不产生空窗误导）。
            from = today;
        }

        return (from, to);
    }
}
