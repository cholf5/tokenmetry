using System.Globalization;

namespace CCUsage.Reconciliation.Tests.Comparison;

/// <summary>
/// Q-001 容差比较器：把两条链路（golden=ccusage CLI --json --offline，ours=自研采集+定价）
/// 聚合出的 日期 × 模型 条目逐项比较。
/// - tokens 五个分量（input / output / cache_creation / cache_read / total）要求精确一致，
///   任何不一致都按 <see cref="ReconciliationCause.Parsing"/> 归类（口径或覆盖差异）；
/// - cost 通过判定条件：绝对误差 &lt; $0.01，或（golden 非 0 时）相对误差 ≤ 0.5%；
///   超出按 <see cref="ReconciliationCause.Pricing"/> 归类；
/// - 单侧缺失的条目按 Parsing 归类并列出对方数值；
/// - 任一侧成本为 null（该口径不提供成本，如 codex per-model 输出）时跳过成本比较。
/// </summary>
public static class ReconciliationComparator
{
    public static ReconciliationReport Compare(
        IReadOnlyList<ReconciliationEntry> golden,
        IReadOnlyList<ReconciliationEntry> ours,
        string goldenLabel = "ccusage CLI",
        string oursLabel = "自研链路",
        ReconciliationTolerance? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(golden);
        ArgumentNullException.ThrowIfNull(ours);

        var effectiveTolerance = tolerance ?? ReconciliationTolerance.Default;
        var goldenByKey = Index(golden);
        var oursByKey = Index(ours);

        var differences = new List<ReconciliationDifference>();
        var compared = 0;

        foreach (var key in goldenByKey.Keys.Union(oursByKey.Keys).OrderBy(key => key.Date, StringComparer.Ordinal)
                     .ThenBy(key => key.Model, StringComparer.Ordinal))
        {
            var hasGolden = goldenByKey.TryGetValue(key, out var goldenEntry);
            var hasOurs = oursByKey.TryGetValue(key, out var oursEntry);

            if (!hasGolden || !hasOurs)
            {
                differences.Add(CreateMissingDifference(
                    key, hasGolden, goldenEntry, hasOurs, oursEntry));
                continue;
            }

            compared++;
            differences.AddRange(CompareEntry(key, goldenEntry!, oursEntry!, effectiveTolerance));
        }

        return new ReconciliationReport(
            effectiveTolerance,
            differences,
            compared,
            goldenLabel,
            oursLabel);
    }

    private static Dictionary<EntryKey, ReconciliationEntry> Index(IReadOnlyList<ReconciliationEntry> entries)
    {
        var index = new Dictionary<EntryKey, ReconciliationEntry>();
        foreach (var entry in entries)
        {
            var key = new EntryKey(entry.Date, entry.ModelKey);
            // 同键重复出现是调用方聚合 bug：直接覆盖会掩盖问题，这里让后者在比较中表现为重复键异常。
            if (!index.TryAdd(key, entry))
            {
                throw new InvalidOperationException(
                    $"对账条目键重复：{entry.Date} × {entry.Model ?? "(total)"}；调用方聚合逻辑错误");
            }
        }

        return index;
    }

    private static IEnumerable<ReconciliationDifference> CompareEntry(
        EntryKey key,
        ReconciliationEntry golden,
        ReconciliationEntry ours,
        ReconciliationTolerance tolerance)
    {
        foreach (var (field, goldenValue, oursValue) in TokenFields(golden, ours))
        {
            if (goldenValue != oursValue)
            {
                yield return new ReconciliationDifference(
                    ReconciliationDifferenceKind.TokenMismatch,
                    ReconciliationCause.Parsing,
                    key.Date,
                    key.Model.Length == 0 ? null : key.Model,
                    field,
                    goldenValue.ToString(CultureInfo.InvariantCulture),
                    oursValue.ToString(CultureInfo.InvariantCulture),
                    FormatDelta(oursValue - goldenValue),
                    RelativeDeviation: null);
            }
        }

        if (golden.CostUsd is { } goldenCost && ours.CostUsd is { } oursCost)
        {
            var absolute = Math.Abs(oursCost - goldenCost);
            decimal? relative = goldenCost == 0m ? null : absolute / Math.Abs(goldenCost);
            var withinAbsolute = absolute < tolerance.CostAbsoluteToleranceUsd;
            var withinRelative = relative is { } deviation && deviation <= tolerance.CostRelativeTolerance;

            if (!withinAbsolute && !withinRelative)
            {
                yield return new ReconciliationDifference(
                    ReconciliationDifferenceKind.CostMismatch,
                    ReconciliationCause.Pricing,
                    key.Date,
                    key.Model.Length == 0 ? null : key.Model,
                    "costUSD",
                    goldenCost.ToString(CultureInfo.InvariantCulture),
                    oursCost.ToString(CultureInfo.InvariantCulture),
                    FormatSigned(oursCost - goldenCost),
                    relative);
            }
        }
    }

    private static IEnumerable<(string Field, long Golden, long Ours)> TokenFields(ReconciliationEntry golden, ReconciliationEntry ours)
    {
        yield return ("inputTokens", golden.InputTokens, ours.InputTokens);
        yield return ("outputTokens", golden.OutputTokens, ours.OutputTokens);
        yield return ("cacheCreationTokens", golden.CacheCreationTokens, ours.CacheCreationTokens);
        yield return ("cacheReadTokens", golden.CacheReadTokens, ours.CacheReadTokens);
        yield return ("totalTokens", golden.TotalTokens, ours.TotalTokens);
    }

    private static ReconciliationDifference CreateMissingDifference(
        EntryKey key,
        bool hasGolden,
        ReconciliationEntry? golden,
        bool hasOurs,
        ReconciliationEntry? ours)
    {
        var kind = hasOurs ? ReconciliationDifferenceKind.MissingInGolden : ReconciliationDifferenceKind.MissingInOurs;
        var present = hasGolden ? golden! : ours!;
        return new ReconciliationDifference(
            kind,
            ReconciliationCause.Parsing,
            key.Date,
            key.Model.Length == 0 ? null : key.Model,
            Field: null,
            GoldenValue: hasGolden ? Summarize(golden!) : "(无)",
            OursValue: hasOurs ? Summarize(ours!) : "(无)",
            Delta: string.Empty,
            RelativeDeviation: null);

        static string Summarize(ReconciliationEntry entry)
            => $"tokens={entry.TotalTokens} cost={(entry.CostUsd is { } cost ? cost.ToString(CultureInfo.InvariantCulture) : "n/a")}";
    }

    private static string FormatDelta(long delta)
        => delta == 0 ? string.Empty : FormatSigned(delta);

    private static string FormatSigned(decimal value)
        => (value >= 0 ? "+" : string.Empty) + value.ToString(CultureInfo.InvariantCulture);

    private readonly record struct EntryKey(string Date, string Model);
}
