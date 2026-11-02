namespace Tokenmetry.Core.Models;

/// <summary>海报上的总量指标（四张 KPI 卡 + 单日三格共用）。</summary>
/// <param name="CacheShare">缓存读取 / 总 token（0..1）；总量为 0 时 null——不伪造 0%。</param>
public sealed record SharePosterTotals(
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    AggregatedCost Cost,
    long SessionCount,
    decimal? CacheShare);

/// <summary>
/// 海报排行表的一行：展示名 + token + 占比（占比分母为该维度自身合计；分母为 0 时 null）+ 成本。
/// 成本是单日托盘基底「主要 Agent / 模型用量」行的第二行文案所需（§3.1 的信息窗口径按成本排序展示）；
/// 汇总表不用它。未知成本保持 <see cref="AggregatedCost"/> 四态，不折 0。
/// </summary>
public sealed record SharePosterRanking(string Name, long Tokens, decimal? Share, AggregatedCost? Cost = null);

/// <summary>
/// 海报渲染所需的全部数据，一次性收成不可变切片。页面 VM 导航即 Dispose，海报不得持有它——
/// 快照是唯一允许跨到渲染上下文的数据形态。
/// </summary>
public sealed record SharePosterData(
    SharePosterRequest Request,
    SharePosterTotals Totals,
    IReadOnlyList<DailyUsagePoint> Daily,
    IReadOnlyList<SharePosterRanking> Agents,
    IReadOnlyList<SharePosterRanking> Models,
    IReadOnlyList<SharePosterRanking> Projects);

/// <summary>
/// 把查询结果收成 <see cref="SharePosterData"/>。纯函数、无 I/O：口径与总览页逐位一致
/// （成本走 <see cref="AggregatedCost"/> 未知契约，日序列不补缺口，排行按 token 降序）。
/// </summary>
public static class SharePosterSnapshot
{
    /// <summary>排行表海报上最多展示的行数。</summary>
    public const int TopN = 6;

    /// <summary>
    /// 把查询结果收成快照。
    /// <para>
    /// <paramref name="kpi"/> 非空时总量取总览页同一份 KPI 聚合（<see cref="UsageKpiResult"/>）——
    /// 维度行会跳过空键、且按天只取 full 或本维度分解行，行合计不一定等于 KPI 总数；海报是给外部看的
    /// 印刷品，"与应用里同一筛选的数字对不上"是设计列出的反目标第一条，所以总量必须以 KPI 为准。
    /// 为 null（纯单测 / 只有维度行）时退回按维度行求和，口径与 P2 初版一致。
    /// </para>
    /// </summary>
    public static SharePosterData Build(
        SharePosterRequest request,
        IReadOnlyList<DailyUsagePoint> daily,
        IReadOnlyList<DimensionUsageRow> agents,
        IReadOnlyList<DimensionUsageRow> models,
        IReadOnlyList<DimensionUsageRow> projects,
        long sessionCount,
        UsageKpiResult? kpi = null)
    {
        // 有日序列时以日序列为准（它带 I/O/缓存细分，且与 KPI 同源聚合）；否则退回维度行求和。
        var hasDaily = daily.Count > 0;
        var input = hasDaily ? daily.Sum(d => d.InputTokens) : agents.Sum(a => a.InputTokens);
        var output = hasDaily ? daily.Sum(d => d.OutputTokens) : agents.Sum(a => a.OutputTokens);
        var cacheRead = hasDaily ? daily.Sum(d => d.CacheReadTokens) : agents.Sum(a => a.CacheReadTokens);
        var totalTokens = hasDaily ? daily.Sum(d => d.TotalTokens) : agents.Sum(a => a.TotalTokens);

        SharePosterTotals totals;
        if (kpi is { } k)
        {
            totals = new SharePosterTotals(
                k.TotalTokens, input, output, k.CacheReadTokens, k.Cost, k.SessionCount, k.CacheReadShare);
        }
        else
        {
            // 已知成本求和：任一行有已知值才有合计（null ≠ 0），任一行未知则整体标未知。
            var known = agents.Any(a => a.Cost.KnownCostUsd is not null)
                ? agents.Where(a => a.Cost.KnownCostUsd is not null).Sum(a => a.Cost.KnownCostUsd!.Value)
                : (decimal?)null;
            var cost = new AggregatedCost(known, agents.Any(a => a.Cost.HasUnknownCost));
            totals = new SharePosterTotals(
                totalTokens, input, output, cacheRead, cost, sessionCount, Share(cacheRead, totalTokens));
        }

        return new SharePosterData(
            request,
            totals,
            daily.ToArray(),
            // Agent 排行不截断：环形图要「Top6 + 其他」，截断会让聚合项消失（行数上限只约束表格维度）
            Rank(agents, takeTop: false),
            Rank(models),
            Rank(projects));
    }

    /// <summary>
    /// 排行：占比分母取<b>该维度自身</b>的合计，而非全范围总 token——与 I-029 的 TokenShare 口径一致
    /// （三个维度的合计互不相等：同一批用量会同时计入 Agent/模型/项目各一行）。
    /// </summary>
    private static IReadOnlyList<SharePosterRanking> Rank(
        IReadOnlyList<DimensionUsageRow> rows, bool takeTop = true)
    {
        var denominator = rows.Sum(r => r.TotalTokens);
        var ordered = rows.OrderByDescending(r => r.TotalTokens);
        return (takeTop ? ordered.Take(TopN) : ordered)
            .Select(r => new SharePosterRanking(
                r.DisplayName ?? r.Key,
                r.TotalTokens,
                Share(r.TotalTokens, denominator),
                r.Cost))
            .ToArray();
    }

    private static decimal? Share(long part, long whole) =>
        whole == 0 ? null : (decimal)part / whole;
}
