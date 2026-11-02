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

/// <summary>海报排行表的一行：展示名 + token + 占比（占比分母为范围内总 token；分母为 0 时 null）。</summary>
public sealed record SharePosterRanking(string Name, long Tokens, decimal? Share);

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
    IReadOnlyList<SharePosterRanking> Projects,
    IReadOnlyList<SharePosterRanking> Types);

/// <summary>
/// 把查询结果收成 <see cref="SharePosterData"/>。纯函数、无 I/O：口径与总览页逐位一致
/// （成本走 <see cref="AggregatedCost"/> 未知契约，日序列不补缺口，排行按 token 降序）。
/// </summary>
public static class SharePosterSnapshot
{
    /// <summary>排行表海报上最多展示的行数。</summary>
    public const int TopN = 6;

    /// <summary>Token 类型构成的三段固定顺序：输入 / 输出 / 缓存。</summary>
    public static readonly string[] TypeNames = ["input", "output", "cache"];

    public static SharePosterData Build(
        SharePosterRequest request,
        IReadOnlyList<DailyUsagePoint> daily,
        IReadOnlyList<DimensionUsageRow> agents,
        IReadOnlyList<DimensionUsageRow> models,
        IReadOnlyList<DimensionUsageRow> projects,
        long sessionCount)
    {
        var totalTokens = agents.Sum(a => a.TotalTokens);
        var input = agents.Sum(a => a.InputTokens);
        var output = agents.Sum(a => a.OutputTokens);
        var cacheRead = agents.Sum(a => a.CacheReadTokens);

        // 已知成本求和：任一行有已知值才有合计（null ≠ 0），任一行未知则整体标未知。
        var known = agents.Any(a => a.Cost.KnownCostUsd is not null)
            ? agents.Where(a => a.Cost.KnownCostUsd is not null).Sum(a => a.Cost.KnownCostUsd!.Value)
            : (decimal?)null;
        var cost = new AggregatedCost(known, agents.Any(a => a.Cost.HasUnknownCost));

        var totals = new SharePosterTotals(
            totalTokens, input, output, cacheRead, cost, sessionCount, Share(cacheRead, totalTokens));

        return new SharePosterData(
            request,
            totals,
            daily.ToArray(),
            Rank(agents),
            Rank(models),
            Rank(projects),
            [
                new SharePosterRanking(TypeNames[0], input, Share(input, totalTokens)),
                new SharePosterRanking(TypeNames[1], output, Share(output, totalTokens)),
                new SharePosterRanking(TypeNames[2], cacheRead, Share(cacheRead, totalTokens)),
            ]);
    }

    /// <summary>
    /// 排行：占比分母取<b>该维度自身</b>的合计，而非全范围总 token——与 I-029 的 TokenShare 口径一致
    /// （三个维度的合计互不相等：同一批用量会同时计入 Agent/模型/项目各一行）。
    /// </summary>
    private static IReadOnlyList<SharePosterRanking> Rank(IReadOnlyList<DimensionUsageRow> rows)
    {
        var denominator = rows.Sum(r => r.TotalTokens);
        return rows.OrderByDescending(r => r.TotalTokens)
            .Take(TopN)
            .Select(r => new SharePosterRanking(
                r.DisplayName ?? r.Key,
                r.TotalTokens,
                Share(r.TotalTokens, denominator)))
            .ToArray();
    }

    private static decimal? Share(long part, long whole) =>
        whole == 0 ? null : (decimal)part / whole;
}
