using System.Text.Json;
using System.Text.Json.Serialization;
using CCUsage.Reconciliation.Tests.Comparison;

namespace CCUsage.Reconciliation.Tests.CCUsageCli;

/// <summary>
/// 一份 golden 报告转换出的对账条目：per-model 条目 + 每日总计条目 + 全期总计条目。
/// 全期总计使用哨兵日期 "(total)"，不会与 ISO 日期冲突。
/// </summary>
public sealed record GoldenReport(
    IReadOnlyList<ReconciliationEntry> ModelEntries,
    IReadOnlyList<ReconciliationEntry> TotalEntries)
{
    public const string GrandTotalDate = "(total)";
}

/// <summary>
/// ccusage CLI golden 文件（v20.0.29 --json --offline 的 daily 输出）的解析与条目转换。
/// 只读 fixtures/golden 下提交进仓库的 JSON，测试运行时不调用 CLI。
/// Claude 与 Codex 的 daily JSON 形状不同（claude 有 modelBreakdowns[] 且逐模型带 cost；
/// codex 是 models{} 字典、逐模型无 cost、另有 reasoningOutputTokens 信息字段），分开建 DTO。
/// </summary>
public static class CcusageGoldenFiles
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };

    public static GoldenReport LoadClaude(string path) => LoadClaudeText(File.ReadAllText(path));

    /// <summary>
    /// zcode daily JSON（I-006）：输出形状与 claude 的 daily 相同（daily[] + modelBreakdowns[] 逐模型带 cost，
    /// 自定义 provider 的模型 cost 为 0 且 missingPricing=true——0 值经同一 DTO 读出，比较口径不变），复用 Claude DTO。
    /// </summary>
    public static GoldenReport LoadZcode(string path) => LoadZcodeText(File.ReadAllText(path));

    public static GoldenReport LoadZcodeText(string json) => LoadClaudeText(json);

    /// <summary>
    /// copilot daily JSON（I-008）：输出形状与 claude 的 daily 相同（daily[] + modelBreakdowns[] 逐模型带 cost，
    /// 另有 modelsUsed / messageCount 信息字段，DTO 忽略），复用 Claude DTO。
    /// </summary>
    public static GoldenReport LoadCopilot(string path) => LoadCopilotText(File.ReadAllText(path));

    public static GoldenReport LoadCopilotText(string json) => LoadClaudeText(json);

    /// <summary>
    /// opencode daily JSON（I-009）：输出形状与 claude 的 daily 相同（daily[] + modelBreakdowns[] 逐模型带 cost，
    /// 另有 modelsUsed 信息字段，DTO 忽略），复用 Claude DTO。
    /// </summary>
    public static GoldenReport LoadOpenCode(string path) => LoadOpenCodeText(File.ReadAllText(path));

    public static GoldenReport LoadOpenCodeText(string json) => LoadClaudeText(json);

    /// <summary>
    /// goose daily JSON（I-012）：输出形状与 claude 的 daily 相同（daily[] + modelBreakdowns[] 逐模型带 cost，
    /// 另有 modelsUsed 信息字段，DTO 忽略），复用 Claude DTO。
    /// </summary>
    public static GoldenReport LoadGoose(string path) => LoadGooseText(File.ReadAllText(path));

    public static GoldenReport LoadGooseText(string json) => LoadClaudeText(json);

    /// <summary>
    /// hermes daily JSON（I-016）：输出形状与 claude 的 daily 相同（daily[] + modelBreakdowns[] 逐模型带 cost，
    /// 另有 modelsUsed / messageCount 信息字段，DTO 忽略），复用 Claude DTO。
    /// </summary>
    public static GoldenReport LoadHermes(string path) => LoadHermesText(File.ReadAllText(path));

    public static GoldenReport LoadHermesText(string json) => LoadClaudeText(json);

    /// <summary>
    /// kilo daily JSON（I-017）：输出形状与 claude 的 daily 相同（daily[] + modelBreakdowns[] 逐模型带 cost，
    /// 另有 modelsUsed 信息字段，DTO 忽略），复用 Claude DTO。
    /// </summary>
    public static GoldenReport LoadKilo(string path) => LoadKiloText(File.ReadAllText(path));

    public static GoldenReport LoadKiloText(string json) => LoadClaudeText(json);

    /// <summary>
    /// gemini daily JSON（I-010）：输出形状与 claude 的 daily 相同（daily[] + modelBreakdowns[] 逐模型带 cost，
    /// 另有 modelsUsed 信息字段，DTO 忽略），复用 Claude DTO。
    /// </summary>
    public static GoldenReport LoadGemini(string path) => LoadGeminiText(File.ReadAllText(path));

    public static GoldenReport LoadGeminiText(string json) => LoadClaudeText(json);

    /// <summary>
    /// amp daily JSON（I-014）：输出形状与 claude 的 daily 相同（daily[] + modelBreakdowns[] 逐模型带 cost，
    /// 另有 modelsUsed 信息字段，DTO 忽略），复用 Claude DTO。
    /// </summary>
    public static GoldenReport LoadAmp(string path) => LoadAmpText(File.ReadAllText(path));

    public static GoldenReport LoadAmpText(string json) => LoadClaudeText(json);

    /// <summary>
    /// droid daily JSON（I-015）：输出形状与 claude 的 daily 相同（daily[] + modelBreakdowns[] 逐模型带 cost，
    /// 另有 modelsUsed 信息字段，DTO 忽略），复用 Claude DTO。
    /// </summary>
    public static GoldenReport LoadDroid(string path) => LoadDroidText(File.ReadAllText(path));

    public static GoldenReport LoadDroidText(string json) => LoadClaudeText(json);

    public static GoldenReport LoadClaudeText(string json)
    {
        var response = JsonSerializer.Deserialize<GoldenClaudeResponse>(
            json, Options) ?? throw new InvalidOperationException("claude golden JSON 解析为 null");

        var modelEntries = new List<ReconciliationEntry>();
        var totalEntries = new List<ReconciliationEntry>();
        foreach (var day in response.Daily ?? new List<GoldenClaudeDay>())
        {
            foreach (var model in day.ModelBreakdowns ?? new List<GoldenClaudeModel>())
            {
                modelEntries.Add(new ReconciliationEntry(
                    day.Date,
                    model.ModelName,
                    model.InputTokens,
                    model.OutputTokens,
                    model.CacheCreationTokens,
                    model.CacheReadTokens,
                    model.InputTokens + model.OutputTokens + model.CacheCreationTokens + model.CacheReadTokens,
                    model.Cost));
            }

            totalEntries.Add(new ReconciliationEntry(
                day.Date,
                Model: null,
                day.InputTokens,
                day.OutputTokens,
                day.CacheCreationTokens,
                day.CacheReadTokens,
                day.TotalTokens,
                day.TotalCost));
        }

        if (response.Totals is { } totals)
        {
            totalEntries.Add(new ReconciliationEntry(
                GoldenReport.GrandTotalDate,
                Model: null,
                totals.InputTokens,
                totals.OutputTokens,
                totals.CacheCreationTokens,
                totals.CacheReadTokens,
                totals.TotalTokens,
                totals.TotalCost));
        }

        return new GoldenReport(modelEntries, totalEntries);
    }

    public static GoldenReport LoadCodex(string path) => LoadCodexText(File.ReadAllText(path));

    public static GoldenReport LoadCodexText(string json)
    {
        var response = JsonSerializer.Deserialize<GoldenCodexResponse>(
            json, Options) ?? throw new InvalidOperationException("codex golden JSON 解析为 null");

        var modelEntries = new List<ReconciliationEntry>();
        var totalEntries = new List<ReconciliationEntry>();
        foreach (var day in response.Daily ?? new List<GoldenCodexDay>())
        {
            foreach (var (modelName, model) in day.Models ?? new Dictionary<string, GoldenCodexModel>())
            {
                // CLI 的 codex daily 逐模型输出没有成本字段：CostUsd 置 null，成本只在每日总计口径比较。
                modelEntries.Add(new ReconciliationEntry(
                    day.Date,
                    modelName,
                    model.InputTokens,
                    model.OutputTokens,
                    model.CacheCreationTokens,
                    model.CacheReadTokens,
                    model.TotalTokens,
                    CostUsd: null));
            }

            totalEntries.Add(new ReconciliationEntry(
                day.Date,
                Model: null,
                day.InputTokens,
                day.OutputTokens,
                day.CacheCreationTokens,
                day.CacheReadTokens,
                day.TotalTokens,
                day.CostUSD));
        }

        if (response.Totals is { } totals)
        {
            totalEntries.Add(new ReconciliationEntry(
                GoldenReport.GrandTotalDate,
                Model: null,
                totals.InputTokens,
                totals.OutputTokens,
                totals.CacheCreationTokens,
                totals.CacheReadTokens,
                totals.TotalTokens,
                totals.CostUSD));
        }

        return new GoldenReport(modelEntries, totalEntries);
    }

    // ---- v20.0.29 claude daily JSON 形状 ----

    private sealed class GoldenClaudeResponse
    {
        [JsonPropertyName("daily")]
        public List<GoldenClaudeDay>? Daily { get; set; }

        [JsonPropertyName("totals")]
        public GoldenClaudeDay? Totals { get; set; }
    }

    private sealed class GoldenClaudeDay
    {
        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("inputTokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long OutputTokens { get; set; }

        [JsonPropertyName("cacheCreationTokens")]
        public long CacheCreationTokens { get; set; }

        [JsonPropertyName("cacheReadTokens")]
        public long CacheReadTokens { get; set; }

        [JsonPropertyName("totalTokens")]
        public long TotalTokens { get; set; }

        [JsonPropertyName("totalCost")]
        public decimal TotalCost { get; set; }

        [JsonPropertyName("modelBreakdowns")]
        public List<GoldenClaudeModel>? ModelBreakdowns { get; set; }
    }

    private sealed class GoldenClaudeModel
    {
        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = string.Empty;

        [JsonPropertyName("cost")]
        public decimal Cost { get; set; }

        [JsonPropertyName("inputTokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long OutputTokens { get; set; }

        [JsonPropertyName("cacheCreationTokens")]
        public long CacheCreationTokens { get; set; }

        [JsonPropertyName("cacheReadTokens")]
        public long CacheReadTokens { get; set; }
    }

    // ---- v20.0.29 codex daily JSON 形状 ----

    private sealed class GoldenCodexResponse
    {
        [JsonPropertyName("daily")]
        public List<GoldenCodexDay>? Daily { get; set; }

        [JsonPropertyName("totals")]
        public GoldenCodexTotals? Totals { get; set; }
    }

    private sealed class GoldenCodexDay
    {
        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("inputTokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long OutputTokens { get; set; }

        [JsonPropertyName("cacheCreationTokens")]
        public long CacheCreationTokens { get; set; }

        [JsonPropertyName("cacheReadTokens")]
        public long CacheReadTokens { get; set; }

        [JsonPropertyName("totalTokens")]
        public long TotalTokens { get; set; }

        [JsonPropertyName("costUSD")]
        public decimal CostUSD { get; set; }

        [JsonPropertyName("models")]
        public Dictionary<string, GoldenCodexModel>? Models { get; set; }
    }

    private sealed class GoldenCodexModel
    {
        [JsonPropertyName("inputTokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long OutputTokens { get; set; }

        [JsonPropertyName("cacheCreationTokens")]
        public long CacheCreationTokens { get; set; }

        [JsonPropertyName("cacheReadTokens")]
        public long CacheReadTokens { get; set; }

        [JsonPropertyName("totalTokens")]
        public long TotalTokens { get; set; }
    }

    private sealed class GoldenCodexTotals
    {
        [JsonPropertyName("inputTokens")]
        public long InputTokens { get; set; }

        [JsonPropertyName("outputTokens")]
        public long OutputTokens { get; set; }

        [JsonPropertyName("cacheCreationTokens")]
        public long CacheCreationTokens { get; set; }

        [JsonPropertyName("cacheReadTokens")]
        public long CacheReadTokens { get; set; }

        [JsonPropertyName("totalTokens")]
        public long TotalTokens { get; set; }

        [JsonPropertyName("costUSD")]
        public decimal CostUSD { get; set; }
    }
}
