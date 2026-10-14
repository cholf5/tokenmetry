using System.Globalization;
using System.Text.Json;
using TokenPulse.Reconciliation.Tests.CCUsageCli;
using TokenPulse.Reconciliation.Tests.Comparison;

namespace TokenPulse.Reconciliation.Tests.Support;

/// <summary>
/// dsh-usage-stats 蓝本 golden（Support/DshBlueprintGolden.mjs 的 stdout JSON）解析：
/// 生成 per-model 对账条目、每日总计条目与全期总计条目（哨兵日期
/// <see cref="GoldenReport.GrandTotalDate"/>，与 CLI golden 口径一致），另携带
/// 蓝本侧 fail-closed 成本统计（pricedCost / incompleteSamples）供成本口径比对。
/// </summary>
public static class DshBlueprintGolden
{
    public static DshGoldenReport Load(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var dayEntries = new List<ReconciliationEntry>();
        var modelEntries = new List<ReconciliationEntry>();
        foreach (var day in root.GetProperty("days").EnumerateObject())
        {
            dayEntries.Add(Entry(day.Name, null, day.Value));
        }

        foreach (var model in root.GetProperty("byModel").EnumerateObject())
        {
            modelEntries.Add(Entry("(all-days)", model.Name, model.Value));
        }

        var total = root.GetProperty("total");
        var grand = new ReconciliationEntry(
            GoldenReport.GrandTotalDate,
            Model: null,
            InputTokens: total.GetProperty("input").GetInt64(),
            OutputTokens: total.GetProperty("output").GetInt64(),
            CacheCreationTokens: total.GetProperty("cacheWrite").GetInt64(),
            CacheReadTokens: total.GetProperty("cacheRead").GetInt64(),
            TotalTokens: total.GetProperty("tokens").GetInt64(),
            CostUsd: null);

        var dayModelEntries = root.GetProperty("byDayModel").EnumerateArray()
            .Select(entry => new ReconciliationEntry(
                entry.GetProperty("date").GetString()!,
                entry.GetProperty("model").GetString(),
                entry.GetProperty("input").GetInt64(),
                entry.GetProperty("output").GetInt64(),
                entry.GetProperty("cacheWrite").GetInt64(),
                entry.GetProperty("cacheRead").GetInt64(),
                entry.GetProperty("tokens").GetInt64(),
                CostUsd: null))
            .ToList();

        var cost = root.GetProperty("cost");
        return new DshGoldenReport(
            Files: root.GetProperty("files").GetInt32(),
            FailedFiles: root.GetProperty("failedFiles").GetInt32(),
            TotalFrames: root.GetProperty("totalFrames").GetInt32(),
            TornFiles: root.GetProperty("tornFiles").GetInt32(),
            Events: root.GetProperty("events").GetInt32(),
            Samples: root.GetProperty("samples").GetInt32(),
            Providers: root.GetProperty("providers").EnumerateArray().Select(item => item.GetString()!).ToList(),
            Models: root.GetProperty("models").EnumerateArray().Select(item => item.GetString()!).ToList(),
            ModelEntries: modelEntries,
            DayEntries: dayEntries,
            DayModelEntries: dayModelEntries,
            GrandTotal: grand,
            PricedCost: cost.GetProperty("pricedCost").GetDecimal(),
            PricedSamples: cost.GetProperty("pricedSamples").GetInt32(),
            IncompleteSamples: cost.GetProperty("incompleteSamples").GetInt32());
    }

    private static ReconciliationEntry Entry(string date, string? model, JsonElement buckets)
        => new(
            date,
            model,
            buckets.GetProperty("input").GetInt64(),
            buckets.GetProperty("output").GetInt64(),
            buckets.GetProperty("cacheWrite").GetInt64(),
            buckets.GetProperty("cacheRead").GetInt64(),
            buckets.GetProperty("tokens").GetInt64(),
            CostUsd: null);

    /// <summary>按 (日期 × 模型) 与 (日期 × null) 索引条目，键重复视为 golden 生成错误。</summary>
    public static IReadOnlyList<ReconciliationEntry> BuildTotalEntries(DshGoldenReport golden)
        => [.. golden.DayEntries, golden.GrandTotal];
}

/// <summary>蓝本 golden 的一次完整产出（I-007）。</summary>
public sealed record DshGoldenReport(
    int Files,
    int FailedFiles,
    int TotalFrames,
    int TornFiles,
    int Events,
    int Samples,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> Models,
    IReadOnlyList<ReconciliationEntry> ModelEntries,
    IReadOnlyList<ReconciliationEntry> DayEntries,
    IReadOnlyList<ReconciliationEntry> DayModelEntries,
    ReconciliationEntry GrandTotal,
    decimal PricedCost,
    int PricedSamples,
    int IncompleteSamples)
{
    public string Summarize()
        => $"files={Files} sessions={Files - FailedFiles} frames={TotalFrames} torn={TornFiles} "
           + $"events={Events} samples={Samples} "
           + $"input={GrandTotal.InputTokens} output={GrandTotal.OutputTokens} "
           + $"cacheWrite={GrandTotal.CacheCreationTokens} cacheRead={GrandTotal.CacheReadTokens} "
           + $"total={GrandTotal.TotalTokens} "
           + $"pricedCost={PricedCost.ToString(CultureInfo.InvariantCulture)} pricedSamples={PricedSamples} "
           + $"incompleteSamples={IncompleteSamples}";
}
