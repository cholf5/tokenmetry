using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.Grok;

/// <summary>
/// Grok 直读适配器（I-022，蓝本 ../ccusage/rust/adapters/grok/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="GrokLogLocator"/>，GROK_HOME → ~/.grok 的 sessions/**&#47;updates.jsonl，
/// 按路径排序）→ 共享增量扫描引擎（<see cref="IncrementalJsonlScanner"/>，文件 mtime/大小记账，变更
/// 文件整文件重解析；逐文件解析由 <see cref="GrokLogParser"/> 承担，含文件内内容键去重）→ 跨文件
/// 全局 "eventId|模型" first-wins 去重（<see cref="GlobalDedupe"/>，蓝本 loader 同键同序——同一服务端
/// 事件可被导出进多个会话文件；无 eventId 的行不参与，防粗键折叠解析层刻意保留的 turn）→
/// 记录按时间戳稳定排序（蓝本 entries.sort_by_key(timestamp)）。
/// 实例持有扫描状态，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class GrokAdapter : IGrokAdapter
{
    private readonly string? _configuredDataDir;
    private readonly GrokLogLocator _locator;
    private readonly GrokLogParser _parser;
    private readonly IncrementalJsonlScanner _scanner;

    public GrokAdapter(
        string? configuredDataDir = null,
        GrokLogLocator? locator = null,
        GrokLogParser? parser = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new GrokLogLocator();
        _parser = parser ?? new GrokLogParser();
        _scanner = new IncrementalJsonlScanner(dedupe: GlobalDedupe);
    }

    public async Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var sessions = _locator.CollectSessionFiles(_configuredDataDir);
        var files = sessions.Select(session => session.Updates).ToList();
        var output = await _scanner.ScanAsync(files, _parser.ParseFileAsync, ct);

        // 蓝本 entries.sort_by_key(timestamp)：稳定排序，同刻保持文件/行序。
        var root = _locator.ResolveRoot(_configuredDataDir);
        return new JsonlScanResult
        {
            Roots = root is null ? [] : [root],
            Records = output.Records.OrderBy(static record => record.Timestamp).ToList(),
            Diagnostics = output.Diagnostics,
        };
    }

    /// <summary>
    /// 跨文件全局去重（蓝本 loader entries.retain）：message.id（eventId）存在 → 键 "eventId|模型"
    /// first-wins；无 eventId 的行原样保留（token 内容键在解析层已按含 reasoning 的全记录比较，粗键
    /// 重构只会误并解析层刻意分开的 turn——蓝本注释同义）。
    /// </summary>
    internal static IReadOnlyList<UsageRecord> GlobalDedupe(IEnumerable<UsageRecord> records)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UsageRecord>();
        foreach (var record in records)
        {
            if (record.MessageId is { } eventId && !seen.Add($"{eventId}|{record.Model}"))
            {
                continue;
            }

            result.Add(record);
        }

        return result;
    }
}
