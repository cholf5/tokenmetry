using System.Globalization;
using CCUsage.Core.Models;

namespace CCUsage.Core.Services;

/// <summary>
/// 统一记录 / 每日汇总 ↔ usage facts 的映射（I-002）。
/// - <see cref="ToUsageFacts"/>：把适配器产出的统一记录聚合为 full 粒度 facts（按本地时区分桶到自然日），
///   并可产出 session 级 facts（<see cref="ToSessionFacts"/>）。
/// - <see cref="FromDailySummaries"/> / <see cref="ToDailySummaries"/>：CLI 数据源的"每日一行"形状与
///   facts 的双向转换。CLI 只提供日合计、model 拆分和 agent 名单（无 token / 成本拆分）、无项目拆分，
///   因此分解为 Day / Agent / Model / Project 四种粒度的行，重建时按粒度路由，信息无损。
/// 消息级去重已在适配器（ClaudeUsageRecordDeduper）完成，facts 层不再做。
/// </summary>
public static class UsageFactsMapper
{
    /// <summary>
    /// 把统一记录聚合为 full 粒度（day × agent × model × project）facts。
    /// cost 取已知 costUSD 之和，全部未知时为 null（缺口由 I-003 定价引擎补全）。
    /// 项目展示名取 <see cref="UsageRecord.ProjectPath"/> 的末段（I-005）。
    /// <paramref name="pricedCosts"/>：定价引擎的补价侧表（按记录引用）——记录实例可能被
    /// 增量扫描器跨刷新复用，补价不得写回记录（否则下次刷新被 LogCost 短路锁死旧价）。
    /// </summary>
    public static List<UsageFact> ToUsageFacts(
        IEnumerable<UsageRecord> records,
        TimeZoneInfo? timeZone = null,
        IReadOnlyDictionary<UsageRecord, decimal?>? pricedCosts = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        return records
            .GroupBy(record => new
            {
                Day = FormatDay(record.Timestamp, tz),
                Agent = record.Agent ?? string.Empty,
                Model = record.Model ?? string.Empty,
                Project = record.ProjectPath ?? string.Empty,
            })
            .Select(group => new UsageFact
            {
                Day = group.Key.Day,
                Grain = UsageFactGrain.Full,
                Agent = group.Key.Agent,
                Model = group.Key.Model,
                Project = group.Key.Project,
                ProjectDisplayName = DeriveProjectDisplayName(group.Key.Project),
                InputTokens = group.Sum(record => record.InputTokens),
                OutputTokens = group.Sum(record => record.OutputTokens),
                CacheCreationTokens = group.Sum(record => record.CacheCreationTokens),
                CacheCreationEphemeral5mTokens = group.Sum(record => record.CacheCreationEphemeral5mTokens),
                CacheCreationEphemeral1hTokens = group.Sum(record => record.CacheCreationEphemeral1hTokens),
                CacheReadTokens = group.Sum(record => record.CacheReadTokens),
                TotalTokens = group.Sum(record => record.TotalTokens),
                CostUsd = SumCosts(group.Select(record => LookupCost(record, pricedCosts))),
            })
            .OrderBy(fact => fact.Day, StringComparer.Ordinal)
            .ThenBy(fact => fact.Agent, StringComparer.Ordinal)
            .ThenBy(fact => fact.Model, StringComparer.Ordinal)
            .ThenBy(fact => fact.Project, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>把统一记录聚合为 session 级 facts（session_id 为主键口径）；项目展示名取 ProjectPath 末段。补价侧表语义同 <see cref="ToUsageFacts"/>。</summary>
    public static List<SessionFact> ToSessionFacts(
        IEnumerable<UsageRecord> records,
        IReadOnlyDictionary<UsageRecord, decimal?>? pricedCosts = null)
    {
        return records
            .GroupBy(record => record.SessionId, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(record => record.Timestamp)
                    .ThenBy(record => record.SessionId, StringComparer.Ordinal)
                    .ToList();
                var first = ordered[0];
                var last = ordered[^1];
                return new SessionFact
                {
                    SessionId = group.Key,
                    Agent = first.Agent ?? string.Empty,
                    Project = first.ProjectPath ?? string.Empty,
                    ProjectDisplayName = DeriveProjectDisplayName(first.ProjectPath ?? string.Empty),
                    DominantModel = SelectDominantModel(group),
                    FirstSeenAt = first.Timestamp,
                    LastSeenAt = last.Timestamp,
                    InputTokens = group.Sum(record => record.InputTokens),
                    OutputTokens = group.Sum(record => record.OutputTokens),
                    CacheCreationTokens = group.Sum(record => record.CacheCreationTokens),
                    CacheCreationEphemeral5mTokens = group.Sum(record => record.CacheCreationEphemeral5mTokens),
                    CacheCreationEphemeral1hTokens = group.Sum(record => record.CacheCreationEphemeral1hTokens),
                    CacheReadTokens = group.Sum(record => record.CacheReadTokens),
                    TotalTokens = group.Sum(record => record.TotalTokens),
                    CostUsd = SumCosts(group.Select(record => LookupCost(record, pricedCosts))),
                };
            })
            .OrderBy(fact => fact.SessionId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>补价侧表命中（按记录引用）用补价结果，否则用记录自带的 costUSD。</summary>
    private static decimal? LookupCost(UsageRecord record, IReadOnlyDictionary<UsageRecord, decimal?>? pricedCosts) =>
        pricedCosts is not null && pricedCosts.TryGetValue(record, out var priced) ? priced : record.CostUsd;

    /// <summary>
    /// 把 CLI 数据源的每日汇总分解为 facts：日合计（Day 粒度）+ agent / model / project 拆分行。
    /// 空白的日期 / agent / model / project 键跳过（与页面对空白键的过滤口径一致）。
    /// </summary>
    public static List<UsageFact> FromDailySummaries(IEnumerable<DailyUsageSummary> rows)
    {
        var facts = new List<UsageFact>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Date))
            {
                continue;
            }

            facts.Add(new UsageFact
            {
                Day = row.Date,
                Grain = UsageFactGrain.Day,
                InputTokens = row.InputTokens,
                OutputTokens = row.OutputTokens,
                CacheCreationTokens = row.CacheCreationTokens,
                CacheReadTokens = row.CacheReadTokens,
                TotalTokens = row.TotalTokens,
                CostUsd = row.TotalCostUsd,
            });

            foreach (var agent in row.Agents)
            {
                if (string.IsNullOrWhiteSpace(agent.Agent))
                {
                    continue;
                }

                facts.Add(new UsageFact
                {
                    Day = row.Date,
                    Grain = UsageFactGrain.Agent,
                    Agent = agent.Agent,
                    TotalTokens = agent.TotalTokens,
                    CostUsd = agent.CostUsd,
                });
            }

            foreach (var model in row.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Model))
                {
                    continue;
                }

                facts.Add(new UsageFact
                {
                    Day = row.Date,
                    Grain = UsageFactGrain.Model,
                    Model = model.Model,
                    TotalTokens = model.TotalTokens,
                    CostUsd = model.CostUsd,
                });
            }

            foreach (var project in row.Projects)
            {
                if (string.IsNullOrWhiteSpace(project.Project))
                {
                    continue;
                }

                facts.Add(new UsageFact
                {
                    Day = row.Date,
                    Grain = UsageFactGrain.Project,
                    Project = project.Project,
                    ProjectDisplayName = project.DisplayName,
                    TotalTokens = project.TotalTokens,
                    CostUsd = project.CostUsd,
                });
            }
        }

        return facts;
    }

    /// <summary>
    /// 把 facts 按日聚合回 <see cref="DailyUsageSummary"/> 形状（页面数据面）。
    /// 同一天同时存在 full 粒度行（统一记录聚合）与分解粒度行（CLI 源）时，以 full 粒度为准，
    /// 避免两种来源混算导致重复计数（数据源切换的清理是 I-005 的职责）。
    /// </summary>
    public static List<DailyUsageSummary> ToDailySummaries(IReadOnlyList<UsageFact> facts)
    {
        return facts
            .Where(fact => !string.IsNullOrWhiteSpace(fact.Day))
            .GroupBy(fact => fact.Day, StringComparer.Ordinal)
            .Select(group => BuildDailySummary(group.Key, group.ToList()))
            .OrderByDescending(summary => summary.Date, StringComparer.Ordinal)
            .ToList();
    }

    private static DailyUsageSummary BuildDailySummary(string day, List<UsageFact> facts)
    {
        var summary = new DailyUsageSummary { Date = day };
        var fullGrain = facts.Where(fact => fact.Grain == UsageFactGrain.Full).ToList();

        if (fullGrain.Count > 0)
        {
            summary.InputTokens = fullGrain.Sum(fact => fact.InputTokens);
            summary.OutputTokens = fullGrain.Sum(fact => fact.OutputTokens);
            summary.CacheCreationTokens = fullGrain.Sum(fact => fact.CacheCreationTokens);
            summary.CacheReadTokens = fullGrain.Sum(fact => fact.CacheReadTokens);
            summary.TotalTokens = fullGrain.Sum(fact => fact.TotalTokens);
            summary.TotalCostUsd = SumCosts(fullGrain.Select(fact => fact.CostUsd)) ?? 0m;

            summary.Agents = fullGrain
                .GroupBy(fact => fact.Agent, StringComparer.Ordinal)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .Select(group => new AgentBreakdown
                {
                    Agent = group.Key,
                    CostUsd = SumCosts(group.Select(fact => fact.CostUsd)) ?? 0m,
                    TotalTokens = group.Sum(fact => fact.TotalTokens),
                })
                .OrderBy(breakdown => breakdown.Agent, StringComparer.Ordinal)
                .ToList();

            summary.Models = fullGrain
                .GroupBy(fact => fact.Model, StringComparer.Ordinal)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .Select(group => new ModelBreakdown
                {
                    Model = group.Key,
                    CostUsd = SumCosts(group.Select(fact => fact.CostUsd)) ?? 0m,
                    TotalTokens = group.Sum(fact => fact.TotalTokens),
                })
                .OrderBy(breakdown => breakdown.Model, StringComparer.Ordinal)
                .ToList();

            summary.Projects = fullGrain
                .GroupBy(fact => fact.Project, StringComparer.Ordinal)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .Select(group => new ProjectBreakdown
                {
                    Project = group.Key,
                    DisplayName = SelectDisplayName(group),
                    CostUsd = SumCosts(group.Select(fact => fact.CostUsd)) ?? 0m,
                    TotalTokens = group.Sum(fact => fact.TotalTokens),
                })
                .OrderBy(breakdown => breakdown.Project, StringComparer.Ordinal)
                .ToList();

            return summary;
        }

        var dayRow = facts.FirstOrDefault(fact => fact.Grain == UsageFactGrain.Day);
        summary.InputTokens = dayRow?.InputTokens ?? 0;
        summary.OutputTokens = dayRow?.OutputTokens ?? 0;
        summary.CacheCreationTokens = dayRow?.CacheCreationTokens ?? 0;
        summary.CacheReadTokens = dayRow?.CacheReadTokens ?? 0;
        summary.TotalTokens = dayRow?.TotalTokens ?? 0;
        summary.TotalCostUsd = dayRow?.CostUsd ?? 0m;

        summary.Agents = facts
            .Where(fact => fact.Grain == UsageFactGrain.Agent && !string.IsNullOrWhiteSpace(fact.Agent))
            .Select(fact => new AgentBreakdown
            {
                Agent = fact.Agent,
                CostUsd = fact.CostUsd ?? 0m,
                TotalTokens = fact.TotalTokens,
            })
            .OrderBy(breakdown => breakdown.Agent, StringComparer.Ordinal)
            .ToList();

        summary.Models = facts
            .Where(fact => fact.Grain == UsageFactGrain.Model && !string.IsNullOrWhiteSpace(fact.Model))
            .Select(fact => new ModelBreakdown
            {
                Model = fact.Model,
                CostUsd = fact.CostUsd ?? 0m,
                TotalTokens = fact.TotalTokens,
            })
            .OrderBy(breakdown => breakdown.Model, StringComparer.Ordinal)
            .ToList();

        summary.Projects = facts
            .Where(fact => fact.Grain == UsageFactGrain.Project && !string.IsNullOrWhiteSpace(fact.Project))
            .Select(fact => new ProjectBreakdown
            {
                Project = fact.Project,
                DisplayName = fact.ProjectDisplayName,
                CostUsd = fact.CostUsd ?? 0m,
                TotalTokens = fact.TotalTokens,
            })
            .OrderBy(breakdown => breakdown.Project, StringComparer.Ordinal)
            .ToList();

        return summary;
    }

    private static string SelectDominantModel(IEnumerable<UsageRecord> records)
    {
        return records
            .GroupBy(record => record.Model ?? string.Empty, StringComparer.Ordinal)
            .OrderByDescending(group => group.Sum(record => record.TotalTokens))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .First().Key;
    }

    /// <summary>
    /// 项目展示名：ProjectPath 的最后一个非空路径段（'/' 与 '\' 均作分隔）。
    /// 与蓝本口径的关系：ccusage-tauri claude.rs 不单独计算展示名（project_name 即日志目录推导的
    /// project_path，Claude 源恒为 projects/ 下单个目录段，取末段与蓝本值一致）；Codex 源的
    /// ProjectPath 是会话 cwd 绝对路径，取末段展示项目目录名（蓝本展示完整 cwd，此处取更短的末段）。
    /// </summary>
    private static string DeriveProjectDisplayName(string projectPath)
    {
        // 显式 char[]：`Split('/', '\\', options)` 会重载解析为 Split(char, int, StringSplitOptions)，
        // 第二个分隔符被隐式转成 int 当作最大段数（隐蔽的行为bug）。
        var segments = projectPath.Split(
            new[] { '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length > 0 ? segments[^1] : string.Empty;
    }

    private static string SelectDisplayName(IEnumerable<UsageFact> facts)
    {
        return facts
            .Select(fact => fact.ProjectDisplayName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty;
    }

    /// <summary>已知成本之和；全部未知时为 null。</summary>
    private static decimal? SumCosts(IEnumerable<decimal?> costs)
    {
        decimal? total = null;
        foreach (var cost in costs)
        {
            if (cost is { } value)
            {
                total = (total ?? 0m) + value;
            }
        }

        return total;
    }

    private static string FormatDay(DateTimeOffset timestamp, TimeZoneInfo timeZone)
    {
        return TimeZoneInfo.ConvertTime(timestamp, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
