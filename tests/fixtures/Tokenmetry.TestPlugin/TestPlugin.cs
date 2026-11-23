using System.Text.Json;
using Tokenmetry.Plugin.Abstractions;

namespace Tokenmetry.TestPlugin;

/// <summary>
/// 宿主集成测试用的最小 fixture 插件：读取 <c>&lt;DataDirectory&gt;/usage.jsonl</c>，
/// 每行一个 <c>{sessionId, projectPath, model, inputTokens, outputTokens}</c>。
/// 文件不存在返回 <see cref="PluginScanStatus.NoData"/>；非法 JSON 行让异常冒泡，由宿主转成 error。
/// </summary>
public sealed class TestPlugin : IUsageSourcePlugin
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // 输入行没有时间字段，fixture 用固定基准 + 行号合成稳定时间，便于断言与快照稳定。
    private static readonly DateTimeOffset BaseTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<PluginScanResult> ScanAsync(PluginScanContext context, CancellationToken cancellationToken)
    {
        // 故意的调试输出：宿主必须把 Console.Out 让给协议并重定向到 stderr，
        // 否则这行会混进 NDJSON 把协议打断（宿主集成测试覆盖这一点）。
        Console.WriteLine("Tokenmetry.TestPlugin: scan starting");

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
        var index = 0;
        await foreach (var line in File.ReadLinesAsync(usagePath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // 非法 JSON 行直接抛出：宿主负责把它转成 `error` 消息并继续服务。
            var entry = JsonSerializer.Deserialize<FixtureUsageLine>(line, LineOptions)
                ?? throw new InvalidOperationException("usage.jsonl line deserialized to null");

            var sessionId = entry.SessionId ?? string.Empty;
            records.Add(new PluginUsageRecord
            {
                // 用 sessionId 当 RecordId，便于用重复 sessionId 触发宿主去重分支。
                RecordId = string.IsNullOrWhiteSpace(sessionId) ? $"line-{index}" : sessionId,
                Timestamp = BaseTimestamp.AddSeconds(index),
                SessionId = sessionId,
                ProjectPath = entry.ProjectPath ?? string.Empty,
                Model = entry.Model ?? string.Empty,
                InputTokens = entry.InputTokens,
                OutputTokens = entry.OutputTokens,
            });
            index++;
        }

        return new PluginScanResult
        {
            HasChanges = true,
            Records = records,
            Diagnostics = new PluginDiagnostics
            {
                Status = PluginScanStatus.Ok,
                FilesDiscovered = 1,
                FilesRead = 1,
                Records = records.Count,
            },
        };
    }
}

internal sealed record FixtureUsageLine
{
    public string? SessionId { get; init; }

    public string? ProjectPath { get; init; }

    public string? Model { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }
}
