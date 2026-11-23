using System.Text.Json;
using Tokenmetry.Plugin.Abstractions;

namespace PluginTemplate;

/// <summary>
/// 最小插件骨架。宿主每次 scan 都要求返回「当前全量快照」，不是增量。
/// 把 <see cref="ScanAsync"/> 换成你的私有 Agent 的真实数据发现与解析逻辑：
/// 数据可以来自 JSONL、SQLite、日志目录或任何本地文件，只要最终映射成
/// <see cref="PluginUsageRecord"/>。字段规则见 plugin-sdk/SKILL.md 的检查表。
/// </summary>
public sealed class UsagePlugin : IUsageSourcePlugin
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<PluginScanResult> ScanAsync(
        PluginScanContext context,
        CancellationToken cancellationToken)
    {
        // 演示格式：<DataDirectory>/usage.jsonl，每行一条用量。真实实现替换为你的数据位置。
        var usagePath = Path.Combine(context.DataDirectory, "usage.jsonl");
        if (!File.Exists(usagePath))
        {
            return new PluginScanResult
            {
                HasChanges = false,
                Diagnostics = new PluginDiagnostics
                {
                    Status = PluginScanStatus.NoData,
                    Message = "usage.jsonl not found",
                },
            };
        }

        var records = new List<PluginUsageRecord>();
        var invalidRecords = 0;
        var index = 0;

        await foreach (var line in File.ReadLinesAsync(usagePath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            TemplateUsageLine? entry;
            try
            {
                entry = JsonSerializer.Deserialize<TemplateUsageLine>(line, LineOptions);
            }
            catch (JsonException)
            {
                invalidRecords++;
                continue;
            }

            if (entry is null
                || string.IsNullOrWhiteSpace(entry.ThreadId)
                || string.IsNullOrWhiteSpace(entry.WorkingDir)
                || string.IsNullOrWhiteSpace(entry.Engine)
                || entry.OccurredAt is null)
            {
                invalidRecords++;
                index++;
                continue;
            }

            var sessionId = entry.ThreadId;
            records.Add(new PluginUsageRecord
            {
                // RecordId 必须稳定且在一次快照内唯一；优先用日志自带的事件 id。
                RecordId = string.IsNullOrWhiteSpace(entry.EventId)
                    ? $"{sessionId}:{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                    : entry.EventId,
                Timestamp = entry.OccurredAt.Value,
                SessionId = sessionId,
                ProjectPath = entry.WorkingDir,
                ProjectDisplayName = entry.ProjectName,
                Model = entry.Engine,
                Provider = entry.Vendor,
                InputTokens = entry.PromptTokens,
                OutputTokens = entry.CompletionTokens,
                CacheCreationTokens = entry.CacheWriteTokens,
                CacheCreationEphemeral5mTokens = entry.CacheWrite5m,
                CacheCreationEphemeral1hTokens = entry.CacheWrite1h,
                CacheReadTokens = entry.CacheReadTokens,
                // 未知成本保持 null（交给定价引擎）；不要用 0 表示“未知”。
                CostUsd = entry.CostUsd,
                Speed = PluginSpeedTier.Standard,
            });
            index++;
        }

        var status = records.Count > 0
            ? (invalidRecords > 0 ? PluginScanStatus.Partial : PluginScanStatus.Ok)
            : (invalidRecords > 0 ? PluginScanStatus.Error : PluginScanStatus.NoData);

        return new PluginScanResult
        {
            HasChanges = true,
            Records = records,
            Diagnostics = new PluginDiagnostics
            {
                Status = status,
                FilesDiscovered = 1,
                FilesRead = 1,
                Records = records.Count,
                InvalidRecords = invalidRecords,
            },
        };
    }
}

/// <summary>模板演示用的输入行。换成你的 Agent 日志的真实字段。</summary>
internal sealed record TemplateUsageLine
{
    public string? EventId { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }

    public string? ThreadId { get; init; }

    public string? WorkingDir { get; init; }

    public string? ProjectName { get; init; }

    public string? Engine { get; init; }

    public string? Vendor { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long CacheWriteTokens { get; init; }

    public long CacheWrite5m { get; init; }

    public long CacheWrite1h { get; init; }

    public decimal? CostUsd { get; init; }
}
