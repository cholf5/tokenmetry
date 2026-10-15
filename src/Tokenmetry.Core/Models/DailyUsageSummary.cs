namespace Tokenmetry.Core.Models;

public class DailyUsageSummary
{
    public string Date { get; set; } = string.Empty;
    public decimal TotalCostUsd { get; set; }
    public long TotalTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public List<AgentBreakdown> Agents { get; set; } = new();
    public List<ModelBreakdown> Models { get; set; } = new();
    public List<ProjectBreakdown> Projects { get; set; } = new();
}

public class AgentBreakdown
{
    public string Agent { get; set; } = string.Empty;
    public decimal CostUsd { get; set; }
    public long TotalTokens { get; set; }
}

public class ModelBreakdown
{
    public string Model { get; set; } = string.Empty;
    public decimal CostUsd { get; set; }
    public long TotalTokens { get; set; }
}

public class ProjectBreakdown
{
    public string Project { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal CostUsd { get; set; }
    public long TotalTokens { get; set; }
}
