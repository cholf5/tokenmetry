using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Infrastructure.CCUsageCli;

/// <summary>
/// 匹配 ccusage CLI 真实 JSON 输出格式的 DTO
/// </summary>
internal class CliDailyResponse
{
    public List<CliDailyItem>? Daily { get; set; }
}

internal class CliDailyItem
{
    public string? Period { get; set; }
    public decimal TotalCost { get; set; }
    public long TotalTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public List<CliModelBreakdown>? ModelBreakdowns { get; set; }
    public CliMetadata? Metadata { get; set; }
}

internal class CliModelBreakdown
{
    public string? ModelName { get; set; }
    public decimal Cost { get; set; }
    public long TotalTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public long CacheReadTokens { get; set; }
}

internal class CliMetadata
{
    public List<string>? Agents { get; set; }
}

public class CCUsageCliParser
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public List<DailyUsageSummary> ParseDailyJson(string json)
    {
        json = json.Trim();

        // 处理空数组的情况
        if (json == "[]")
        {
            return new List<DailyUsageSummary>();
        }

        var response = JsonSerializer.Deserialize<CliDailyResponse>(json, _options)
            ?? throw new InvalidOperationException("Failed to parse daily JSON: result was null");

        if (response.Daily == null)
        {
            return new List<DailyUsageSummary>();
        }

        return response.Daily.Select(item => new DailyUsageSummary
        {
            Date = item.Period ?? string.Empty,
            TotalCostUsd = item.TotalCost,
            TotalTokens = item.TotalTokens,
            InputTokens = item.InputTokens,
            OutputTokens = item.OutputTokens,
            CacheCreationTokens = item.CacheCreationTokens,
            CacheReadTokens = item.CacheReadTokens,
            Agents = (item.Metadata?.Agents ?? Enumerable.Empty<string>())
                .Select(agent => new AgentBreakdown
                {
                    Agent = agent,
                    // 注意：真实 CLI 不提供 agent 级别的成本拆分，这里只做标记
                    CostUsd = 0,
                    TotalTokens = 0
                }).ToList(),
            Models = (item.ModelBreakdowns ?? Enumerable.Empty<CliModelBreakdown>())
                .Select(m => new ModelBreakdown
                {
                    Model = m.ModelName ?? string.Empty,
                    CostUsd = m.Cost,
                    TotalTokens = GetModelTotalTokens(m)
                }).ToList(),
            Projects = new List<ProjectBreakdown>() // ccusage CLI 暂不提供项目拆分
        }).ToList();
    }

    private static long GetModelTotalTokens(CliModelBreakdown model)
    {
        if (model.TotalTokens != 0)
        {
            return model.TotalTokens;
        }

        return model.InputTokens + model.OutputTokens + model.CacheCreationTokens + model.CacheReadTokens;
    }
}
