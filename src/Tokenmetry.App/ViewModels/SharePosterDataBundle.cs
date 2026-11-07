using System;
using System.Collections.Generic;
using Tokenmetry.Core.Models;

namespace Tokenmetry.App.ViewModels;

/// <summary>
/// 海报对话框的一轮数据：快照输入 + 两项补查 + 解析出的时间窗口，落地即不可变。
/// 对话框内数据范围筛选可变后，一个对话框生命周期会有多轮 Bundle——每轮由
/// <c>MainWindowViewModel.LoadPosterBundleAsync</c> 按<b>当时</b>的全局筛选现查产出；
/// 范围（首末含端点，「全部时间」等无界窗口由日序列有数据端收口）是这轮数据的产物，
/// 不是对话框选项——选项（样式/版式/主题/雾化）只决定版面，不改这轮数据。
/// </summary>
public sealed record SharePosterDataBundle(
    DateOnly RangeStart,
    DateOnly RangeEnd,
    IReadOnlyList<DailyUsagePoint> Daily,
    IReadOnlyList<DimensionUsageRow> Agents,
    IReadOnlyList<DimensionUsageRow> Models,
    IReadOnlyList<DimensionUsageRow> Projects,
    long SessionCount,
    UsageKpiResult? Kpi,
    HourlyDistributionResult? Hourly,
    int? HourlyCutoffHour,
    WeeklyRhythmResult? WeeklyRhythm)
{
    /// <summary>按当前请求组合出渲染数据（纯函数，直通 <see cref="SharePosterSnapshot.Build"/>，
    /// 口径与总览页逐位一致——总量以 KPI 为准，见其注释）。</summary>
    public SharePosterData Compose(SharePosterRequest request) =>
        SharePosterSnapshot.Build(
            request, Daily, Agents, Models, Projects, SessionCount, Kpi,
            Hourly, HourlyCutoffHour, WeeklyRhythm);
}
