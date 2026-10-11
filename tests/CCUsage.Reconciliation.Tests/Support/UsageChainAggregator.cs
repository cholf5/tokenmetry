using System.Globalization;
using CCUsage.Core.Models;
using CCUsage.Core.Services;
using CCUsage.Infrastructure.ClaudeCodeJsonl;
using CCUsage.Infrastructure.CodexJsonl;
using CCUsage.Reconciliation.Tests.CCUsageCli;
using CCUsage.Reconciliation.Tests.Comparison;

namespace CCUsage.Reconciliation.Tests.Support;

/// <summary>
/// 自研链路（fixture → 适配器 → PricingEngine → 按日聚合）一次运行的产物。
/// </summary>
public sealed class ChainAggregation
{
    public required JsonlScanDiagnostics Diagnostics { get; init; }

    public int RecordCount { get; init; }

    public required IReadOnlyList<ReconciliationEntry> ModelEntries { get; init; }

    /// <summary>每日总计 + 全期总计（哨兵日期 <see cref="GoldenReport.GrandTotalDate"/>）。</summary>
    public required IReadOnlyList<ReconciliationEntry> TotalEntries { get; init; }

    public required IReadOnlyList<UnpricedModelInfo> UnpricedModels { get; init; }

    public string Summarize()
    {
        var grand = TotalEntries.Single(entry => entry.Date == GoldenReport.GrandTotalDate);
        return $"records={RecordCount} input={grand.InputTokens} output={grand.OutputTokens} "
            + $"cacheCreation={grand.CacheCreationTokens} cacheRead={grand.CacheReadTokens} "
            + $"total={grand.TotalTokens} cost={grand.CostUsd?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}";
    }
}

/// <summary>
/// 对账测试的自研链路执行器：扫描 fixture（或真实目录）→ 统一记录 → 内置快照定价 → 按日聚合。
/// 定价只用 <see cref="PricingEngine"/> 的内置快照（不接远程源、无用户覆盖），保证离线确定性；
/// 与 golden 录制时 CLI 的 --offline 口径对应。日期按 UTC 分桶，与 golden 录制命令的 --timezone UTC 对应。
/// </summary>
public static class UsageChainAggregator
{
    public static async Task<ChainAggregation> AggregateClaudeAsync(string configDir, CancellationToken ct = default)
    {
        var adapter = new ClaudeCodeJsonlAdapter(configuredRoot: configDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredRoot：走 CLAUDE_CONFIG_DIR / 默认目录自动探测（本机真实数据对账用）。</summary>
    public static async Task<ChainAggregation> AggregateClaudeRootsAsync(CancellationToken ct = default)
    {
        var adapter = new ClaudeCodeJsonlAdapter();
        return Aggregate(await adapter.ScanAsync(ct));
    }

    public static async Task<ChainAggregation> AggregateCodexAsync(string? codexHome = null, CancellationToken ct = default)
    {
        var adapter = new CodexJsonlAdapter(configuredHome: codexHome);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    private static ChainAggregation Aggregate(JsonlScanResult scan)
    {
        // 每次新建引擎：未命中清单随本次聚合隔离，不受测试间共享状态影响。
        var engine = new PricingEngine();

        var modelEntries = scan.Records
            .Where(record => record.Model is not null)
            .GroupBy(record => (Date: UtcDay(record.Timestamp), Model: record.Model!))
            .OrderBy(group => group.Key.Date, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Model, StringComparer.Ordinal)
            .Select(group => BuildEntry(group.Key.Date, group.Key.Model, group, engine))
            .ToList();

        var dayEntries = scan.Records
            .GroupBy(record => UtcDay(record.Timestamp), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => BuildEntry(group.Key, null, group, engine))
            .ToList();

        // 无模型记录（<synthetic>）不计入 per-model 口径（CLI 同样不给出该模型的 breakdown），
        // 其 token 已包含在每日总计中，由总计口径覆盖。
        var grand = new ReconciliationEntry(
            GoldenReport.GrandTotalDate,
            Model: null,
            dayEntries.Sum(entry => entry.InputTokens),
            dayEntries.Sum(entry => entry.OutputTokens),
            dayEntries.Sum(entry => entry.CacheCreationTokens),
            dayEntries.Sum(entry => entry.CacheReadTokens),
            dayEntries.Sum(entry => entry.TotalTokens),
            dayEntries.Sum(entry => entry.CostUsd ?? 0m));

        var totalEntries = new List<ReconciliationEntry>(dayEntries) { grand };

        return new ChainAggregation
        {
            Diagnostics = scan.Diagnostics,
            RecordCount = scan.Records.Count,
            ModelEntries = modelEntries,
            TotalEntries = totalEntries,
            UnpricedModels = engine.GetUnpricedModels(),
        };
    }

    private static ReconciliationEntry BuildEntry(
        string date,
        string? model,
        IEnumerable<UsageRecord> records,
        PricingEngine engine)
    {
        long input = 0;
        long output = 0;
        long cacheCreation = 0;
        long cacheRead = 0;
        long total = 0;
        decimal cost = 0m;

        foreach (var record in records)
        {
            input += record.InputTokens;
            output += record.OutputTokens;
            cacheCreation += record.CacheCreationTokens;
            cacheRead += record.CacheReadTokens;
            total += record.TotalTokens;
            // 未命中定价（Missing）的记录成本记 0：与 CLI 对未知模型记 0 的口径一致；
            // 未命中清单随聚合结果返回，供差异报告定位"价格表覆盖"类差异。
            cost += engine.PriceRecord(record).CostUsd ?? 0m;
        }

        return new ReconciliationEntry(date, model, input, output, cacheCreation, cacheRead, total, cost);
    }

    /// <summary>UTC 自然日（golden 录制命令带 --timezone UTC，两侧分桶口径一致）。</summary>
    private static string UtcDay(DateTimeOffset timestamp)
        => timestamp.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
