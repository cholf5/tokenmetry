using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.Core.Services;

/// <summary>
/// 开发模式下使用的模拟数据源，提供测试数据
/// </summary>
public class MockUsageDataSource : IUsageDataSource
{
    private readonly bool _generateAnomalies;

    public MockUsageDataSource(bool generateAnomalies = false)
    {
        _generateAnomalies = generateAnomalies;
    }

    public Task<List<DailyUsageSummary>> GetDailyUsageAsync(CancellationToken ct = default)
    {
        var today = DateTime.Now;
        var rows = new List<DailyUsageSummary>();

        // 生成过去 14 天的测试数据
        for (int i = 0; i < 14; i++)
        {
            var date = today.AddDays(-i).ToString("yyyy-MM-dd");
            var isToday = i == 0;

            // 今日成本异常高（用于测试异常检测）
            var randomValue = (decimal)new Random(i).NextDouble() * 0.5m;
            var cost = isToday && _generateAnomalies ? 5.0m : 0.5m + randomValue;

            rows.Add(new DailyUsageSummary
            {
                Date = date,
                TotalCostUsd = cost,
                TotalTokens = (long)(cost * 10000),
                InputTokens = (long)(cost * 6000),
                OutputTokens = (long)(cost * 3000),
                CacheCreationTokens = (long)(cost * 500),
                CacheReadTokens = (long)(cost * 500),
                Agents = new List<AgentBreakdown>
                {
                    new() { Agent = "claude-sonnet-4", CostUsd = cost * 0.7m, TotalTokens = (long)(cost * 7000) },
                    new() { Agent = "claude-opus-4", CostUsd = cost * 0.3m, TotalTokens = (long)(cost * 3000) }
                },
                Models = new List<ModelBreakdown>
                {
                    new() { Model = "claude-sonnet-4-20250514", CostUsd = cost * 0.7m, TotalTokens = (long)(cost * 7000) },
                    new() { Model = "claude-opus-4-20250514", CostUsd = cost * 0.3m, TotalTokens = (long)(cost * 3000) }
                },
                Projects = new List<ProjectBreakdown>
                {
                    new() { Project = "project-alpha", DisplayName = "项目 Alpha", CostUsd = cost * 0.5m, TotalTokens = (long)(cost * 5000) },
                    new() { Project = "project-beta", DisplayName = "项目 Beta", CostUsd = cost * 0.5m, TotalTokens = (long)(cost * 5000) }
                }
            });
        }

        return Task.FromResult(rows);
    }
}
