using System.Globalization;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.Antigravity;
using Tokenmetry.Infrastructure.Amp;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.CodexJsonl;
using Tokenmetry.Infrastructure.CopilotSession;
using Tokenmetry.Infrastructure.DshSession;
using Tokenmetry.Infrastructure.Droid;
using Tokenmetry.Infrastructure.GeminiCli;
using Tokenmetry.Infrastructure.Grok;
using Tokenmetry.Infrastructure.GooseSqlite;
using Tokenmetry.Infrastructure.HermesSqlite;
using Tokenmetry.Infrastructure.KiloSqlite;
using Tokenmetry.Infrastructure.OpenClaw;
using Tokenmetry.Infrastructure.OpenCodeSqlite;
using Tokenmetry.Infrastructure.KimiCode;
using Tokenmetry.Infrastructure.Codebuff;
using Tokenmetry.Infrastructure.PiAgent;
using Tokenmetry.Infrastructure.QwenCode;
using Tokenmetry.Infrastructure.ZCodeSqlite;
using Tokenmetry.Reconciliation.Tests.CCUsageCli;
using Tokenmetry.Reconciliation.Tests.Comparison;

namespace Tokenmetry.Reconciliation.Tests.Support;

/// <summary>
/// 自研链路（fixture → 适配器 → PricingEngine → 按日聚合）一次运行的产物。
/// </summary>
public sealed class ChainAggregation
{
    public required JsonlScanDiagnostics Diagnostics { get; init; }

    public int RecordCount { get; init; }

    /// <summary>统一记录全集（场景级断言用；聚合粒度数字见 ModelEntries / TotalEntries）。</summary>
    public required IReadOnlyList<UsageRecord> Records { get; init; }

    public required IReadOnlyList<ReconciliationEntry> ModelEntries { get; init; }

    /// <summary>每日总计 + 全期总计（哨兵日期 <see cref="GoldenReport.GrandTotalDate"/>）。</summary>
    public required IReadOnlyList<ReconciliationEntry> TotalEntries { get; init; }

    public required IReadOnlyList<UnpricedModelInfo> UnpricedModels { get; init; }

    /// <summary>CostUsd 显式为 0 的记录数（I-007 DSH：目录外路由 fail-closed 记 0；I-006 ZCode：自定义 provider 行）。</summary>
    public int ZeroCostRecordCount { get; init; }

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

    /// <summary>无 configuredHome：走 ZCODE_HOME / ~/.zcode 自动探测（本机真实数据对账用，I-006）。</summary>
    public static async Task<ChainAggregation> AggregateZcodeAsync(string? configuredHome = null, CancellationToken ct = default)
    {
        var adapter = new ZCodeSqliteAdapter(configuredHome: configuredHome);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>
    /// 无 configuredHome：走 DSH_HOME / ~/.dsh 自动探测（本机真实数据对账用，I-007）。
    /// 日期按本地日历日分桶——DSH 蓝本（lib/usage.js dayKey）把事件时间归到本地日历日，
    /// 与 CLI 对账口径（--timezone UTC）不同，故 DSH 链路不走 UTC 分桶。
    /// </summary>
    public static async Task<ChainAggregation> AggregateDshAsync(string? configuredHome = null, CancellationToken ct = default)
    {
        var adapter = new DshSessionAdapter(configuredHome: configuredHome);
        return Aggregate(await adapter.ScanAsync(ct), LocalDay);
    }

    /// <summary>无 configuredHome：走 COPILOT_HOME / ~/.copilot 自动探测（本机真实数据对账用，I-008）。</summary>
    public static async Task<ChainAggregation> AggregateCopilotAsync(string? configuredHome = null, CancellationToken ct = default)
    {
        var adapter = new CopilotSessionAdapter(configuredHome: configuredHome);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 OPENCODE_DATA_DIR / XDG 默认根自动探测（本机真实数据对账用，I-009）。</summary>
    public static async Task<ChainAggregation> AggregateOpenCodeAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new OpenCodeSqliteAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredRoot：走 GOOSE_PATH_ROOT / 双默认根自动探测（本机真实数据对账用，I-012）。</summary>
    public static async Task<ChainAggregation> AggregateGooseAsync(string? configuredRoot = null, CancellationToken ct = default)
    {
        var adapter = new GooseSqliteAdapter(configuredRoot: configuredRoot);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredHome：走 HERMES_HOME / ~/.hermes 自动探测（本机真实数据对账用，I-016）。</summary>
    public static async Task<ChainAggregation> AggregateHermesAsync(string? configuredHome = null, CancellationToken ct = default)
    {
        var adapter = new HermesSqliteAdapter(configuredHome: configuredHome);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 KILO_DATA_DIR / 默认根自动探测（本机真实数据对账用，I-017）。</summary>
    public static async Task<ChainAggregation> AggregateKiloAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new KiloSqliteAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 GEMINI_DATA_DIR / ~/.gemini/tmp 自动探测（本机真实数据对账用，I-010）。</summary>
    public static async Task<ChainAggregation> AggregateGeminiAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new GeminiCliAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 AMP_DATA_DIR / ~/.local/share/amp 自动探测（本机真实数据对账用，I-014）。</summary>
    public static async Task<ChainAggregation> AggregateAmpAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new AmpAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 DROID_SESSIONS_DIR / ~/.factory/sessions 自动探测（本机真实数据对账用，I-015）。</summary>
    public static async Task<ChainAggregation> AggregateDroidAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new DroidAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 QWEN_DATA_DIR / ~/.qwen 自动探测（本机真实数据对账用，I-011）。</summary>
    public static async Task<ChainAggregation> AggregateQwenAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new QwenCodeAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 KIMI_DATA_DIR / ~/.kimi 与 ~/.kimi-code 自动探测（本机真实数据对账用，I-013）。</summary>
    public static async Task<ChainAggregation> AggregateKimiAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new KimiCodeAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 CODEBUFF_DATA_DIR / ~/.config/&lt;channel&gt; 自动探测（本机真实数据对账用，I-018）。</summary>
    public static async Task<ChainAggregation> AggregateCodebuffAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new CodebuffAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 PI_AGENT_DIR / ~/.pi/agent/sessions 自动探测（本机真实数据对账用，I-019）。</summary>
    public static async Task<ChainAggregation> AggregatePiAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new PiAgentAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 GROK_HOME / ~/.grok 自动探测（本机真实数据对账用，I-022）。</summary>
    public static async Task<ChainAggregation> AggregateGrokAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new GrokAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 OPENCLAW_DIR / 主目录四默认根自动探测（本机真实数据对账用，I-020）。</summary>
    public static async Task<ChainAggregation> AggregateOpenClawAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new OpenClawAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    /// <summary>无 configuredDataDir：走 ANTIGRAVITY_DATA_DIR / 六默认根自动探测（本机真实数据对账用，I-021）。</summary>
    public static async Task<ChainAggregation> AggregateAntigravityAsync(string? configuredDataDir = null, CancellationToken ct = default)
    {
        var adapter = new AntigravityAdapter(configuredDataDir: configuredDataDir);
        return Aggregate(await adapter.ScanAsync(ct));
    }

    private static ChainAggregation Aggregate(JsonlScanResult scan)
        => Aggregate(scan, UtcDay);

    private static ChainAggregation Aggregate(JsonlScanResult scan, Func<DateTimeOffset, string> daySelector)
    {
        // 每次新建引擎：未命中清单随本次聚合隔离，不受测试间共享状态影响。
        var engine = new PricingEngine();

        var modelEntries = scan.Records
            .Where(record => record.Model is not null)
            .GroupBy(record => (Date: daySelector(record.Timestamp), Model: record.Model!))
            .OrderBy(group => group.Key.Date, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Model, StringComparer.Ordinal)
            .Select(group => BuildEntry(group.Key.Date, group.Key.Model, group, engine))
            .ToList();

        var dayEntries = scan.Records
            .GroupBy(record => daySelector(record.Timestamp), StringComparer.Ordinal)
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
            Records = scan.Records,
            ModelEntries = modelEntries,
            TotalEntries = totalEntries,
            UnpricedModels = engine.GetUnpricedModels(),
            ZeroCostRecordCount = scan.Records.Count(record => record.CostUsd == 0m),
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

    /// <summary>本地日历日（DSH 蓝本 dayKey 口径，I-007）。</summary>
    private static string LocalDay(DateTimeOffset timestamp)
        => TimeZoneInfo.ConvertTime(timestamp, TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
