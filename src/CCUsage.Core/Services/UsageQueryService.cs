using System.Globalization;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.Core.Services;

/// <summary>
/// UI 聚合查询服务实现（I-028 / I-029）：基于 <see cref="IUsageStore"/>（I-002 facts 存储，
/// 唯一 SQL 数据面）做只读聚合，与 <see cref="CacheService"/> 同一消费模式——facts 面向桌面本地库，
/// 全量读取后内存聚合的成本可忽略；不引入第二套 SQL 语义（迁移 / cost_usd 文本解析 / 时区分桶
/// 口径单点留在存储层）。
/// 读取口径：同日混源时 full 粒度行优先（CLI 分解行不重复计数，对齐 <see cref="UsageFactsMapper"/>
/// 的既有守则）；会话侧统一按"开始日 = first_seen_at 的本地自然日"落入查询区间，时区默认本机
/// （与采集侧 <c>UsageFactsMapper</c> 的分桶默认一致）。
/// <b>时区前提（审查 P2 文档化）</b>：day 文本由采集侧写入（<c>UsageFactsMapper</c> 默认本机时区分桶），
/// 本服务默认用本机时区解释会话时间与查询范围，两者需一致（同机部署下成立；DI 可注入统一覆盖）。
/// 存储层 <c>FormatTimestamp</c> 保留原始 offset——测试里 substr(first_seen_at,1,10) 的"UTC 日"口径
/// 仅在 offset 恒为 +00:00 时与本地日等价；该等价性由当前适配器链的时间戳解析保证（解析层
/// AdjustToUniversal 统一归一化为 UTC）。
/// </summary>
public class UsageQueryService : IUsageQueryService
{
    private readonly IUsageStore _store;
    private readonly TimeZoneInfo _timeZone;

    public UsageQueryService(IUsageStore store, TimeZoneInfo? timeZone = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DailyUsagePoint>> GetDailySeriesAsync(UsageQueryFilter filter, CancellationToken ct = default)
    {
        ValidateRange(filter);
        var from = UsageQueryFilter.ParseDay(filter.FromDay);
        var to = UsageQueryFilter.ParseDay(filter.ToDay);
        if (from is { } fromDay && to is { } toDay
            && toDay.DayNumber - fromDay.DayNumber + 1 > UsageQueryFilter.MaxDailySeriesDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(filter),
                $"Daily series range {filter.FromDay}..{filter.ToDay} exceeds the {UsageQueryFilter.MaxDailySeriesDays}-day cap.");
        }

        var byDay = await GetDailyFactsByDayAsync(filter, ct);

        var points = new List<DailyUsagePoint>();
        if (from is { } boundedFrom && to is { } boundedTo)
        {
            // 日历窗口语义（D-002 定案）：双端有界时补零展开每个自然日，空日 HasData=false
            // 供 UI 图层辨识；稀疏数据的长间隔不再画成连续花费。
            for (var day = boundedFrom; day <= boundedTo; day = day.AddDays(1))
            {
                var dayText = UsageQueryFilter.FormatDay(day);
                points.Add(BuildDailyPoint(dayText, byDay.GetValueOrDefault(dayText)));
            }
        }
        else
        {
            // 无界范围无从补零：只返回实际有数据的日期。
            foreach (var day in byDay.Keys.OrderBy(day => day, StringComparer.Ordinal))
            {
                points.Add(BuildDailyPoint(day, byDay[day]));
            }
        }

        return points;
    }

    /// <inheritdoc />
    public async Task<UsageKpiResult> GetKpiAsync(UsageQueryFilter filter, CancellationToken ct = default)
    {
        ValidateRange(filter);
        var current = await BuildKpiAggregateAsync(filter, ct);

        KpiPeriodComparison? previous = null;
        if (filter.FromDay is { } && filter.ToDay is { })
        {
            // 上一周期 = 等长回看窗口：[FromDay − N, FromDay − 1]，N 为当前窗口自然日天数（含端点）。
            var from = UsageQueryFilter.ParseDay(filter.FromDay)!.Value;
            var to = UsageQueryFilter.ParseDay(filter.ToDay)!.Value;
            var length = to.DayNumber - from.DayNumber + 1;
            var previousFilter = new UsageQueryFilter
            {
                FromDay = UsageQueryFilter.FormatDay(from.AddDays(-length)),
                ToDay = UsageQueryFilter.FormatDay(from.AddDays(-1)),
                Agent = filter.Agent,
            };
            var previousAggregate = await BuildKpiAggregateAsync(previousFilter, ct);
            previous = new KpiPeriodComparison
            {
                FromDay = previousFilter.FromDay!,
                ToDay = previousFilter.ToDay!,
                TotalTokens = previousAggregate.TotalTokens,
                Cost = previousAggregate.Cost,
                SessionCount = previousAggregate.SessionCount,
                CacheReadShare = previousAggregate.CacheReadShare,
                TotalTokensChange = MetricChange.From(current.TotalTokens, previousAggregate.TotalTokens),
                CostChange = MetricChange.From(current.Cost.KnownCostUsd, previousAggregate.Cost.KnownCostUsd),
                SessionCountChange = MetricChange.From(current.SessionCount, previousAggregate.SessionCount),
                CacheReadShareChange = MetricChange.From(current.CacheReadShare, previousAggregate.CacheReadShare),
            };
        }

        return new UsageKpiResult
        {
            TotalTokens = current.TotalTokens,
            CacheReadTokens = current.CacheReadTokens,
            Cost = current.Cost,
            SessionCount = current.SessionCount,
            CacheReadShare = current.CacheReadShare,
            PreviousPeriod = previous,
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DimensionUsageRow>> GetAgentBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
        GetDimensionBreakdownAsync(filter, UsageFactGrain.Agent, fact => fact.Agent, session => session.Agent, withProjectDisplayName: false, withDominantAgent: false, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<DimensionUsageRow>> GetModelBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
        GetDimensionBreakdownAsync(filter, UsageFactGrain.Model, fact => fact.Model, session => session.DominantModel, withProjectDisplayName: false, withDominantAgent: true, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<DimensionUsageRow>> GetProjectBreakdownAsync(UsageQueryFilter filter, CancellationToken ct = default) =>
        GetDimensionBreakdownAsync(filter, UsageFactGrain.Project, fact => fact.Project, session => session.Project, withProjectDisplayName: true, withDominantAgent: false, ct);

    /// <inheritdoc />
    public async Task<SessionListResult> GetSessionsAsync(SessionListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = query.Filter ?? new UsageQueryFilter();
        ValidateRange(filter);
        if (query.PageSize is { } requestedPageSize && requestedPageSize <= 0)
        {
            throw new ArgumentException(
                $"PageSize must be a positive number (got {requestedPageSize}); use null for no paging.", nameof(query));
        }

        var rows = (await GetScopedSessionsAsync(filter, ct)).Select(ToRow);

        var project = query.Project;
        if (!string.IsNullOrEmpty(project))
        {
            rows = rows.Where(row => string.Equals(row.Project, project, StringComparison.Ordinal));
        }

        var model = query.Model;
        if (!string.IsNullOrEmpty(model))
        {
            rows = rows.Where(row => string.Equals(row.DominantModel, model, StringComparison.Ordinal));
        }

        var term = query.SearchTerm?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            rows = rows.Where(row => MatchesSearch(row, term));
        }

        var ordered = SortRows(rows.ToList(), query.SortBy, query.SortDescending);
        var sorted = ordered.ToList();
        var totalCount = sorted.Count;

        var page = Math.Max(1, query.Page);
        List<SessionListRow> pageRows;
        if (query.PageSize is { } pageSize)
        {
            // skip 用 long 防 (page-1)*pageSize 溢出（审查 P2）；skip 越过总数返回空页而非环绕。
            var skip = (long)(page - 1) * pageSize;
            pageRows = skip >= sorted.Count
                ? new List<SessionListRow>()
                : sorted.Skip((int)skip).Take(pageSize).ToList();
        }
        else
        {
            pageRows = sorted;
        }

        return new SessionListResult
        {
            Rows = pageRows,
            TotalCount = totalCount,
            Page = page,
            PageSize = query.PageSize,
        };
    }

    /// <inheritdoc />
    public async Task<HourlyDistributionResult> GetHourlyDistributionAsync(UsageQueryFilter filter, CancellationToken ct = default)
    {
        ValidateRange(filter);
        var sessions = await GetScopedSessionsAsync(filter, ct);

        var buckets = Enumerable.Range(0, 24).Select(hour => new HourBucketAccumulator(hour)).ToArray();
        long unknownSessionCount = 0;
        long unknownTotalTokens = 0;
        foreach (var session in sessions)
        {
            if (!HasMinuteLevelTimestamp(session.FirstSeenAt, _timeZone))
            {
                unknownSessionCount++;
                unknownTotalTokens += session.TotalTokens;
                continue;
            }

            buckets[TimeZoneInfo.ConvertTime(session.FirstSeenAt, _timeZone).Hour].Add(session);
        }

        return new HourlyDistributionResult
        {
            Hours = buckets.Select(bucket => bucket.ToBucket()).ToList(),
            UnknownSessionCount = unknownSessionCount,
            UnknownTotalTokens = unknownTotalTokens,
            TimeZoneId = _timeZone.Id,
        };
    }

    // ---- 读取与过滤 ----

    private async Task<List<UsageFact>> GetScopedUsageFactsAsync(UsageQueryFilter filter, CancellationToken ct)
    {
        var facts = await _store.GetUsageFactsAsync(ct);
        var scoped = new List<UsageFact>();
        foreach (var fact in facts)
        {
            if (string.IsNullOrWhiteSpace(fact.Day)
                || (filter.FromDay is { } from && string.CompareOrdinal(fact.Day, from) < 0)
                || (filter.ToDay is { } to && string.CompareOrdinal(fact.Day, to) > 0))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(filter.Agent)
                && !string.Equals(fact.Agent, filter.Agent, StringComparison.Ordinal))
            {
                continue;
            }

            scoped.Add(fact);
        }

        return scoped;
    }

    private async Task<List<SessionFact>> GetScopedSessionsAsync(UsageQueryFilter filter, CancellationToken ct)
    {
        var sessions = await _store.GetSessionFactsAsync(ct);
        var scoped = new List<SessionFact>();
        foreach (var session in sessions)
        {
            if (!string.IsNullOrWhiteSpace(filter.Agent)
                && !string.Equals(session.Agent, filter.Agent, StringComparison.Ordinal))
            {
                continue;
            }

            // 会话归日口径：first_seen_at 换算查询时区的自然日后与范围比较（facts 不存本地日）。
            if (filter.FromDay is { } || filter.ToDay is { })
            {
                var day = LocalDay(session.FirstSeenAt);
                if (filter.FromDay is { } from && string.CompareOrdinal(day, from) < 0)
                {
                    continue;
                }

                if (filter.ToDay is { } to && string.CompareOrdinal(day, to) > 0)
                {
                    continue;
                }
            }

            scoped.Add(session);
        }

        return scoped;
    }

    private async Task<Dictionary<string, List<UsageFact>>> GetDailyFactsByDayAsync(UsageQueryFilter filter, CancellationToken ct)
    {
        var byDay = new Dictionary<string, List<UsageFact>>(StringComparer.Ordinal);
        foreach (var fact in await GetScopedUsageFactsAsync(filter, ct))
        {
            if (!byDay.TryGetValue(fact.Day, out var list))
            {
                list = new List<UsageFact>();
                byDay[fact.Day] = list;
            }

            list.Add(fact);
        }

        return byDay;
    }

    // ---- 日序列与 KPI ----

    /// <summary>
    /// 单日行解析：有 full 粒度行只用 full 行（统一记录聚合，维度齐全）；否则用 Day 粒度行
    /// （CLI 源的日合计）。两者皆无返回空列表（该日在日合计口径下无数据；同日的其他分解行
    /// 不折算进日合计，对齐 <see cref="UsageFactsMapper.ToDailySummaries"/>）。
    /// </summary>
    private static List<UsageFact> ResolveDailyRows(IReadOnlyList<UsageFact> dayRows)
    {
        var full = new List<UsageFact>();
        var dayGrain = new List<UsageFact>();
        foreach (var fact in dayRows)
        {
            if (fact.Grain == UsageFactGrain.Full)
            {
                full.Add(fact);
            }
            else if (fact.Grain == UsageFactGrain.Day)
            {
                dayGrain.Add(fact);
            }
        }

        return full.Count > 0 ? full : dayGrain;
    }

    private static DailyUsagePoint BuildDailyPoint(string day, List<UsageFact>? rows)
    {
        if (rows is not { Count: > 0 })
        {
            return new DailyUsagePoint { Day = day, Cost = AggregatedCost.Empty, HasData = false };
        }

        var resolved = ResolveDailyRows(rows);
        return new DailyUsagePoint
        {
            Day = day,
            InputTokens = resolved.Sum(fact => fact.InputTokens),
            OutputTokens = resolved.Sum(fact => fact.OutputTokens),
            CacheCreationTokens = resolved.Sum(fact => fact.CacheCreationTokens),
            CacheReadTokens = resolved.Sum(fact => fact.CacheReadTokens),
            TotalTokens = resolved.Sum(fact => fact.TotalTokens),
            Cost = AggregatedCost.FromRowCosts(resolved.Select(fact => fact.CostUsd)),
            HasData = true,
        };
    }

    private async Task<KpiAggregate> BuildKpiAggregateAsync(UsageQueryFilter filter, CancellationToken ct)
    {
        long totalTokens = 0;
        long cacheReadTokens = 0;
        var cost = AggregatedCost.Empty;
        foreach (var dayRows in (await GetDailyFactsByDayAsync(filter, ct)).Values)
        {
            var resolved = ResolveDailyRows(dayRows);
            totalTokens += resolved.Sum(fact => fact.TotalTokens);
            cacheReadTokens += resolved.Sum(fact => fact.CacheReadTokens);
            cost = cost.Add(AggregatedCost.FromRowCosts(resolved.Select(fact => fact.CostUsd)));
        }

        var sessionCount = (await GetScopedSessionsAsync(filter, ct)).Count;
        var cacheReadShare = totalTokens > 0 ? (decimal?)Math.Round((decimal)cacheReadTokens / totalTokens, 4) : null;
        return new KpiAggregate(totalTokens, cacheReadTokens, cost, sessionCount, cacheReadShare);
    }

    private sealed record KpiAggregate(
        long TotalTokens,
        long CacheReadTokens,
        AggregatedCost Cost,
        long SessionCount,
        decimal? CacheReadShare);

    // ---- 维度聚合 ----

    private async Task<IReadOnlyList<DimensionUsageRow>> GetDimensionBreakdownAsync(
        UsageQueryFilter filter,
        string decompositionGrain,
        Func<UsageFact, string> factKey,
        Func<SessionFact, string> sessionKey,
        bool withProjectDisplayName,
        bool withDominantAgent,
        CancellationToken ct)
    {
        var factRowsByKey = new Dictionary<string, List<UsageFact>>(StringComparer.Ordinal);
        foreach (var dayGroup in (await GetScopedUsageFactsAsync(filter, ct)).GroupBy(fact => fact.Day, StringComparer.Ordinal))
        {
            var dayRows = dayGroup.ToList();
            var full = dayRows.Where(fact => fact.Grain == UsageFactGrain.Full).ToList();
            // 单日内 full 优先；CLI 分解日只取本维度的分解行（Agent/Model/Project 粒度各有总 token 与成本，
            // 无 I/O 细分是 I-002 对 CLI 源的既有语义）。
            var usable = full.Count > 0 ? full : dayRows.Where(fact => fact.Grain == decompositionGrain).ToList();
            foreach (var fact in usable)
            {
                var key = factKey(fact);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!factRowsByKey.TryGetValue(key, out var list))
                {
                    list = new List<UsageFact>();
                    factRowsByKey[key] = list;
                }

                list.Add(fact);
            }
        }

        var sessions = await GetScopedSessionsAsync(filter, ct);
        var sessionCountByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var sessionDisplayByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        // D-010 关联 Agent 列（契约演进定案）：与 SessionCount 同一批会话、同一个 sessionKey 分组，
        // 顺带按 Agent 计数——每模型每 Agent 一个 long，成本可忽略；其余维度不累计。
        var agentSessionsByKey = withDominantAgent
            ? new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal)
            : null;
        foreach (var session in sessions)
        {
            var key = sessionKey(session);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            sessionCountByKey[key] = sessionCountByKey.GetValueOrDefault(key) + 1;
            if (withProjectDisplayName
                && !sessionDisplayByKey.ContainsKey(key)
                && !string.IsNullOrWhiteSpace(session.ProjectDisplayName))
            {
                sessionDisplayByKey[key] = session.ProjectDisplayName;
            }

            if (agentSessionsByKey is { } agentMap && !string.IsNullOrWhiteSpace(session.Agent))
            {
                if (!agentMap.TryGetValue(key, out var agents))
                {
                    agents = new Dictionary<string, long>(StringComparer.Ordinal);
                    agentMap[key] = agents;
                }

                agents[session.Agent] = agents.GetValueOrDefault(session.Agent) + 1;
            }
        }

        // 键取并集：只有会话（无 facts 行）与只有 facts 行（无会话，如 CLI 源）都成行。
        var keys = factRowsByKey.Keys.Union(sessionCountByKey.Keys, StringComparer.Ordinal);
        var displayNames = withProjectDisplayName
            ? ResolveProjectDisplayNames(keys, factRowsByKey, sessionDisplayByKey)
            : null;

        var rows = new List<DimensionUsageRow>();
        foreach (var key in keys)
        {
            factRowsByKey.TryGetValue(key, out var list);
            var resolvedCost = list is null
                ? AggregatedCost.Empty
                : AggregatedCost.FromRowCosts(list.Select(fact => fact.CostUsd));

            // 主用 Agent：会话数最多的 Agent（并列取键序数最小，保证确定性）；无会话数据 → null（诚实）。
            string? dominantAgent = null;
            int? agentCount = null;
            if (agentSessionsByKey is { } map && map.TryGetValue(key, out var agents) && agents.Count > 0)
            {
                agentCount = agents.Count;
                dominantAgent = agents
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .First().Key;
            }

            rows.Add(new DimensionUsageRow
            {
                Key = key,
                // 仅项目维度填充展示名（歧义守卫后，恒非空）；Agent / 模型维度为 null（Key 即展示名）。
                DisplayName = displayNames?.GetValueOrDefault(key),
                SessionCount = sessionCountByKey.GetValueOrDefault(key),
                InputTokens = list?.Sum(fact => fact.InputTokens) ?? 0,
                OutputTokens = list?.Sum(fact => fact.OutputTokens) ?? 0,
                CacheCreationTokens = list?.Sum(fact => fact.CacheCreationTokens) ?? 0,
                CacheReadTokens = list?.Sum(fact => fact.CacheReadTokens) ?? 0,
                TotalTokens = list?.Sum(fact => fact.TotalTokens) ?? 0,
                Cost = resolvedCost,
                DominantAgent = dominantAgent,
                AgentCount = agentCount,
            });
        }

        // 占比分母取同范围全维度行（分母为 0 时 null，不伪造 0）；成本占比只在已知成本口径上比较。
        var scopeTotalTokens = rows.Sum(row => (decimal)row.TotalTokens);
        decimal? scopeKnownCost = null;
        foreach (var row in rows)
        {
            if (row.Cost.KnownCostUsd is { } known)
            {
                scopeKnownCost = (scopeKnownCost ?? 0m) + known;
            }
        }

        return rows
            .Select(row => new DimensionUsageRow
            {
                Key = row.Key,
                DisplayName = row.DisplayName,
                SessionCount = row.SessionCount,
                InputTokens = row.InputTokens,
                OutputTokens = row.OutputTokens,
                CacheCreationTokens = row.CacheCreationTokens,
                CacheReadTokens = row.CacheReadTokens,
                TotalTokens = row.TotalTokens,
                Cost = row.Cost,
                DominantAgent = row.DominantAgent,
                AgentCount = row.AgentCount,
                TokenShare = scopeTotalTokens > 0 ? (decimal?)Math.Round(row.TotalTokens / scopeTotalTokens, 4) : null,
                CostShare = scopeKnownCost is { } denominator and > 0
                             && !row.Cost.HasUnknownCost
                             && row.Cost.KnownCostUsd is { } known
                    ? (decimal?)Math.Round(known / denominator, 4)
                    : null,
            })
            .OrderByDescending(row => row.TotalTokens)
            .ThenBy(row => row.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 项目展示名归一化（路径末段，采集层同式）+ 歧义守卫（对齐 I-025"宁可显示原始也不猜"）：
    /// 同一结果集内 ≥2 个不同原始路径归一化出相同展示名时，这些路径全部退回显示原始路径，
    /// 绝不把不同项目合并成一行；无候选展示名的路径同样显示原始路径。展示名候选优先取
    /// usage_facts 的 project_display_name（存储读取序确定），缺失时取 session_facts 的值。
    /// </summary>
    private static Dictionary<string, string> ResolveProjectDisplayNames(
        IEnumerable<string> keys,
        Dictionary<string, List<UsageFact>> factRowsByKey,
        Dictionary<string, string> sessionDisplayByKey)
    {
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var fromFacts = factRowsByKey.TryGetValue(key, out var list)
                ? list.Select(fact => fact.ProjectDisplayName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                : null;
            var candidate = fromFacts
                ?? (sessionDisplayByKey.TryGetValue(key, out var fromSession) ? fromSession : null);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                candidates[key] = candidate;
            }
        }

        // 碰撞检测用 ordinal-ignore-case（审查 P2）：/work/Tools 与 /work/tools 在大小写不敏感的
        // 本地文件系统上是同一路径，仅 ordinal 逐位比较会漏判歧义。语义保持"碰撞即全部退回原始
        // 路径，绝不合并"——与 I-025 模型名归一化守卫（ordinal，归一化已折叠大小写）相比，这里的
        // 检测范围更宽，因为路径末段不做归一化折叠。
        var ambiguous = candidates
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return keys.ToDictionary(
            key => key,
            key => candidates.TryGetValue(key, out var name) && !ambiguous.Contains(name) ? name : key,
            StringComparer.Ordinal);
    }

    // ---- 会话列表 ----

    private static SessionListRow ToRow(SessionFact session) => new()
    {
        SessionId = session.SessionId,
        Agent = session.Agent,
        Project = session.Project,
        ProjectDisplayName = session.ProjectDisplayName,
        DominantModel = session.DominantModel,
        FirstSeenAt = session.FirstSeenAt,
        LastSeenAt = session.LastSeenAt,
        InputTokens = session.InputTokens,
        OutputTokens = session.OutputTokens,
        CacheCreationTokens = session.CacheCreationTokens,
        CacheReadTokens = session.CacheReadTokens,
        TotalTokens = session.TotalTokens,
        CostUsd = session.CostUsd,
    };

    private static bool MatchesSearch(SessionListRow row, string term) =>
        row.SessionId.Contains(term, StringComparison.OrdinalIgnoreCase)
        || row.Agent.Contains(term, StringComparison.OrdinalIgnoreCase)
        || row.Project.Contains(term, StringComparison.OrdinalIgnoreCase)
        || row.ProjectDisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || row.DominantModel.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>排序：主键按查询声明，平局键恒为 SessionId（序数）保证分页稳定。</summary>
    private static IOrderedEnumerable<SessionListRow> SortRows(List<SessionListRow> rows, SessionSortField sortBy, bool descending)
    {
        IOrderedEnumerable<SessionListRow> ordered = sortBy switch
        {
            SessionSortField.TotalTokens => descending
                ? rows.OrderByDescending(row => row.TotalTokens)
                : rows.OrderBy(row => row.TotalTokens),
            // 未知成本（null）视为最小值：降序时排在已知成本之后，不折 0 参与比较。
            SessionSortField.Cost => descending
                ? rows.OrderByDescending(row => row.CostUsd ?? decimal.MinValue)
                : rows.OrderBy(row => row.CostUsd ?? decimal.MinValue),
            _ => descending
                ? rows.OrderByDescending(row => row.LastSeenAt)
                : rows.OrderBy(row => row.LastSeenAt),
        };
        return ordered.ThenBy(row => row.SessionId, StringComparer.Ordinal);
    }

    // ---- 时间口径 ----

    /// <summary>一天的毫秒数（UTC 午夜锚定判定用）。</summary>
    private const long MillisecondsPerDay = 86_400_000;

    private string LocalDay(DateTimeOffset timestamp) =>
        TimeZoneInfo.ConvertTime(timestamp, _timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 分钟级时间精度判定（I-029 小时分布，规则与已知边界见 <see cref="HourlyDistributionResult"/>）。
    /// 两条"日锚定"形态都判为"无分钟级精度"，宁排除不伪造：
    /// ① UTC 午夜锚定（<c>ToUnixTimeMilliseconds()</c> 恰为一天毫秒数的整数倍，含纪元 0）——日期级
    ///    来源（Goose 解析 "{date}T00:00:00Z"）与各 loader 的纪元兜底（Kimi / OpenClaw / Grok 返回
    ///    <c>StrictRfc3339Timestamp.Epoch</c>，Gemini / Amp / Droid 的严格 RFC3339 失败回退）都锚定
    ///    UTC 午夜。该判定与查询时区无关：非 UTC 机器上只查本地 TimeOfDay 会把 UTC 午夜锚定误判成
    ///    "有精度"并伪造进本地小时桶（审查 P1，UTC+8 实测复现）。
    /// ② 查询时区下 00:00:00.000（本地日锚定）——本地午夜锚定的来源同样无法证明携带分钟级精度。
    /// 代价：真实发生在 UTC / 本地午夜的会话被保守排除并计入 unknown（可见而非丢失）。
    /// </summary>
    internal static bool HasMinuteLevelTimestamp(DateTimeOffset timestamp, TimeZoneInfo timeZone)
    {
        if (timestamp.ToUnixTimeMilliseconds() % MillisecondsPerDay == 0)
        {
            return false;
        }

        return TimeZoneInfo.ConvertTime(timestamp, timeZone).TimeOfDay != TimeSpan.Zero;
    }

    private static void ValidateRange(UsageQueryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!filter.IsRangeValid)
        {
            throw new ArgumentException(
                $"FromDay ({filter.FromDay}) must not be after ToDay ({filter.ToDay}).", nameof(filter));
        }

        if (filter.FromDay is { } from && UsageQueryFilter.ParseDay(from) is null)
        {
            throw new ArgumentException($"FromDay '{from}' is not a yyyy-MM-dd date.", nameof(filter));
        }

        if (filter.ToDay is { } to && UsageQueryFilter.ParseDay(to) is null)
        {
            throw new ArgumentException($"ToDay '{to}' is not a yyyy-MM-dd date.", nameof(filter));
        }
    }

    /// <summary>小时分布的单桶累加器（成本按 <see cref="AggregatedCost"/> 契约合并）。</summary>
    private sealed class HourBucketAccumulator
    {
        private readonly int _hour;
        private decimal? _knownCost;
        private bool _hasUnknownCost;

        public HourBucketAccumulator(int hour)
        {
            _hour = hour;
        }

        public long SessionCount { get; private set; }

        public long InputTokens { get; private set; }

        public long OutputTokens { get; private set; }

        public long CacheReadTokens { get; private set; }

        public long TotalTokens { get; private set; }

        public void Add(SessionFact session)
        {
            SessionCount++;
            InputTokens += session.InputTokens;
            OutputTokens += session.OutputTokens;
            CacheReadTokens += session.CacheReadTokens;
            TotalTokens += session.TotalTokens;
            if (session.CostUsd is { } cost)
            {
                _knownCost = (_knownCost ?? 0m) + cost;
            }
            else
            {
                // 会话成本 null = 会话内全部记录成本未知（I-002 行语义），按未知标记并入。
                _hasUnknownCost = true;
            }
        }

        public HourlyUsageBucket ToBucket() => new()
        {
            Hour = _hour,
            SessionCount = SessionCount,
            InputTokens = InputTokens,
            OutputTokens = OutputTokens,
            CacheReadTokens = CacheReadTokens,
            TotalTokens = TotalTokens,
            Cost = new AggregatedCost(_knownCost, _hasUnknownCost),
        };
    }
}
