using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using TokenPulse.App.Controls;
using TokenPulse.Core.Abstractions;
using TokenPulse.Core.Models;

namespace TokenPulse.App.ViewModels;

/// <summary>
/// [D-004/D-005] 图表控件预览 VM（Q-002 定案：随 StylePreviewView dev-only 保留，无生产入口）：
/// 双数据源开关——「示例数据」为内置 fixture（含补零日/未知成本等边界样本，不依赖本机 facts）；
/// 关掉则走 <see cref="IUsageQueryService"/> 真实查询（I-028 日序列 + I-029 小时分布 + Agent 维度），
/// 本机 facts 为空时控件呈诚实空态。热力图取日序列尾部 ≤35 天。
/// </summary>
public partial class ChartPreviewViewModel : ObservableObject
{
    private readonly IUsageQueryService _query;

    public ChartPreviewViewModel(IUsageQueryService query)
    {
        _query = query;
        _ = LoadAsync();
    }

    /// <summary>true = 内置 fixture（默认，保证预览不依赖本机数据量）；false = 真实 IUsageQueryService 查询。</summary>
    [ObservableProperty]
    private bool _useFixtureData = true;

    /// <summary>日序列范围天数（真实路径的 FromDay/ToDay 窗口）。</summary>
    [ObservableProperty]
    private int _rangeDays = 30;

    /// <summary>范围档位（对齐原型 range select 7/30/90）。</summary>
    public IReadOnlyList<int> RangeDayOptions { get; } = new[] { 7, 30, 90 };

    [ObservableProperty]
    private IReadOnlyList<DailyUsagePoint>? _dailyPoints;

    [ObservableProperty]
    private HourlyDistributionResult? _hourly;

    [ObservableProperty]
    private IReadOnlyList<DonutSegment>? _agentSegments;

    [ObservableProperty]
    private IReadOnlyList<HeatmapCell>? _heatmapCells;

    /// <summary>环中心主文字（紧凑总量）。</summary>
    [ObservableProperty]
    private string _totalTokensCompact = string.Empty;

    /// <summary>加载状态一行（数据源 / 天数 / 有数据日数 / 小时分布 unknown 提示）。</summary>
    [ObservableProperty]
    private string _status = "loading";

    partial void OnUseFixtureDataChanged(bool value) => _ = LoadAsync();

    partial void OnRangeDaysChanged(int value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        // 加载序号（审查 P2-5）：每次触发自增；真实查询跨 await 回来时若期间用户又改了
        // 数据源/范围（序号已前移），旧结果直接丢弃，绝不覆盖新选择。fixture 为同步路径，
        // 立即生效并天然使在飞的真实加载失效。
        var sequence = ++_loadSequence;
        if (UseFixtureData)
        {
            ApplyFixture();
            return;
        }

        try
        {
            var to = DateOnly.FromDateTime(DateTime.Today);
            var from = to.AddDays(-(RangeDays - 1));
            var filter = new UsageQueryFilter
            {
                FromDay = UsageQueryFilter.FormatDay(from),
                ToDay = UsageQueryFilter.FormatDay(to),
            };

            // I-028 日历窗口语义：双端有界 → 逐日补零（HasData=false 可辨识），范围 ≤ MaxDailySeriesDays
            var daily = await _query.GetDailySeriesAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            var hourly = await _query.GetHourlyDistributionAsync(filter);
            var agents = await _query.GetAgentBreakdownAsync(filter);
            if (sequence != _loadSequence)
            {
                return;
            }

            DailyPoints = daily;
            Hourly = hourly;
            HeatmapCells = daily
                .TakeLast(35)
                .Select(p => new HeatmapCell { Label = p.Day, Value = p.TotalTokens, HasData = p.HasData })
                .ToList();

            // donut ≤ 7 段：token 量 Top6 + 「其他」聚合（超 7 系列色的原型同款处理）
            var withTokens = agents
                .Where(a => a.TotalTokens > 0)
                .OrderByDescending(a => a.TotalTokens)
                .ToList();
            var top = withTokens.Take(6).ToList();
            var rest = withTokens.Skip(6).ToList();
            var segmentList = top
                .Select(a => new DonutSegment(a.Key, a.TotalTokens))
                .ToList();
            if (rest.Count > 0)
            {
                segmentList.Add(new DonutSegment("其他", rest.Sum(a => a.TotalTokens)));
            }

            AgentSegments = segmentList;
            TotalTokensCompact = TrendChart.FormatCompactTokens((long)daily.Sum(p => p.TotalTokens));
            Status = $"真实数据 · {daily.Count} 天（有数据 {daily.Count(p => p.HasData)}）· "
                     + $"小时分布 unknown 会话 {hourly.UnknownSessionCount:N0} · agents {withTokens.Count}";
        }
        catch (Exception ex)
        {
            if (sequence == _loadSequence)
            {
                Status = $"error: {ex.Message}";
            }
        }
    }

    private int _loadSequence;

    /// <summary>
    /// fixture（对齐原型样本形态）：30 天窗口、稀疏有数据日（检验补零日辨识）、最后一天
    /// 已知成本 + 未知成本混合（检验 tooltip 成本口径）、24 小时桶双峰、Agent donut 五段。
    /// </summary>
    private void ApplyFixture()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var realDayOffsets = new[] { 2, 5, 6, 11, 17, 23, 29 }; // 相对窗口首日
        var points = new List<DailyUsagePoint>();
        for (var i = 0; i < 30; i++)
        {
            var day = today.AddDays(-(29 - i));
            var real = realDayOffsets.Contains(i);
            long input = real ? 320_000 + i * 41_000 : 0;
            long output = real ? 120_000 + i * 17_000 : 0;
            points.Add(new DailyUsagePoint
            {
                Day = UsageQueryFilter.FormatDay(day),
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = real ? input / 2 : 0,
                CacheCreationTokens = 0,
                TotalTokens = input + output,
                Cost = real
                    ? new AggregatedCost(knownCostUsd: 1.20m + i * 0.31m, hasUnknownCost: i == 29)
                    : AggregatedCost.Empty,
                HasData = real,
            });
        }

        var hours = Enumerable.Range(0, 24)
            .Select(h => new HourlyUsageBucket
            {
                Hour = h,
                SessionCount = h is 10 or 14 or 18 ? 6 + h / 4 : h is < 6 or > 23 ? 0 : 2,
                TotalTokens = h switch
                {
                    10 => 950_000,
                    14 => 920_000,
                    18 => 900_000,
                    < 6 => 0,
                    _ => 200_000 + h * 12_000,
                },
                InputTokens = 0,
                OutputTokens = 0,
                CacheReadTokens = 0,
                Cost = AggregatedCost.Empty,
            })
            .ToList();

        DailyPoints = points;
        Hourly = new HourlyDistributionResult
        {
            Hours = hours,
            UnknownSessionCount = 3,
            UnknownTotalTokens = 86_400,
            TimeZoneId = TimeZoneInfo.Local.Id,
        };
        HeatmapCells = points
            .TakeLast(35)
            .Select(p => new HeatmapCell { Label = p.Day, Value = p.TotalTokens, HasData = p.HasData })
            .ToList();
        // 环中心总量与 Agent 段同源（审查 P2-4）：段值 = 日序列合计 × 原型占比，
        // 余量入末段，合计恒等于日序列总量——环心 8.47M 与图例合计不再两处数字各自为政
        var totalTokens = points.Sum(p => (double)p.TotalTokens);
        var agentNames = new[] { "Claude Code", "Codex", "Cursor", "GitHub Copilot" };
        var agentShares = new[] { 0.41, 0.26, 0.16, 0.09 };
        var agentList = new List<DonutSegment>();
        double allocated = 0;
        for (var i = 0; i < agentNames.Length; i++)
        {
            var value = totalTokens * agentShares[i];
            allocated += value;
            agentList.Add(new DonutSegment(agentNames[i], value));
        }

        agentList.Add(new DonutSegment("Cline", totalTokens - allocated));
        AgentSegments = agentList;
        TotalTokensCompact = TrendChart.FormatCompactTokens(totalTokens);
        Status = $"示例数据 · 30 天（有数据 {points.Count(p => p.HasData)}，含补零日与未知成本日）";
    }
}
