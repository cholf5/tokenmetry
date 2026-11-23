using System.Globalization;
using System.Text.Json;
using Tokenmetry.Plugin.Abstractions;

namespace FakePrivateAgent;

/// <summary>
/// SDK 示例插件：一个假想的私有 Agent，把会话目录里的 <c>usage.jsonl</c> 映射成
/// Tokenmetry 的用量记录。真实插件只需替换「发现 + 解析 + 映射」三段，契约与包格式不变。
/// <para>
/// 输入行格式见 <c>fixtures/usage.jsonl</c>：eventId / occurredAt / threadId / workingDir /
/// engine / promptTokens / completionTokens / cache* / costUsd。未知成本不写字段（保持 null）。
/// </para>
/// </summary>
public sealed class FakeUsagePlugin : IUsageSourcePlugin
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<PluginScanResult> ScanAsync(
        PluginScanContext context,
        CancellationToken cancellationToken)
    {
        // 真实 Agent 通常从固定位置发现会话；示例把扫描根交给宿主分配的 DataDirectory，
        // 这样校验器可以用 --data 指向任意夹具目录。
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
        var lineNumber = 0;

        await foreach (var line in File.ReadLinesAsync(usagePath, cancellationToken))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            FakeUsageLine? entry;
            try
            {
                entry = JsonSerializer.Deserialize<FakeUsageLine>(line, LineOptions);
            }
            catch (JsonException)
            {
                invalidRecords++;
                continue;
            }

            if (!TryMap(entry, lineNumber, out var record))
            {
                invalidRecords++;
                continue;
            }

            records.Add(record);
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

    private static bool TryMap(FakeUsageLine? entry, int lineNumber, out PluginUsageRecord record)
    {
        record = null!;

        // 必需字段缺失即整条拒绝：时间、会话、项目、模型。
        if (entry is null
            || entry.OccurredAt is null
            || string.IsNullOrWhiteSpace(entry.ThreadId)
            || string.IsNullOrWhiteSpace(entry.WorkingDir)
            || string.IsNullOrWhiteSpace(entry.Engine))
        {
            return false;
        }

        record = new PluginUsageRecord
        {
            // 日志自带稳定事件 id；缺失时用「会话 + 行号」兜底，保证一次快照内唯一。
            RecordId = string.IsNullOrWhiteSpace(entry.EventId)
                ? $"{entry.ThreadId}:{lineNumber.ToString(CultureInfo.InvariantCulture)}"
                : entry.EventId,
            Timestamp = entry.OccurredAt.Value,
            // 插件内唯一即可；主进程会加 plugin:<pluginId>: 命名空间。
            SessionId = entry.ThreadId,
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
            // null = 未知，交给定价引擎；绝不用 0 冒充未知。
            CostUsd = entry.CostUsd,
            Speed = PluginSpeedTier.Standard,
        };
        return true;
    }
}

internal sealed record FakeUsageLine
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
