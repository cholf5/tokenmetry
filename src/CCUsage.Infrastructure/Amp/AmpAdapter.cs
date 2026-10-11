using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.Amp;

/// <summary>
/// Amp 直读适配器（I-014，蓝本 ../ccusage/rust/adapters/amp/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="AmpLogLocator"/>，AMP_DATA_DIR → ~/.local/share/amp 的 threads/ 子树 .json
/// 线程文件）→ 逐文件解析（<see cref="AmpThreadParser"/>，usageLedger.events 优先、缺失回退
/// messages 用量）→ 记录按时间戳稳定排序（蓝本 entries.sort_by_key(timestamp)）。
/// 不走共享 IncrementalJsonlScanner（单层 JSON 非 JSONL，且蓝本无任何消息级去重语义），每次全量
/// 重读（I-009 SQLite 系同口径）。实例无扫描状态，线程安全；以单例接入 App DI。
/// </summary>
public class AmpAdapter : IAmpAdapter
{
    private readonly string? _configuredDataDir;
    private readonly AmpLogLocator _locator;
    private readonly AmpThreadParser _parser;

    public AmpAdapter(
        string? configuredDataDir = null,
        AmpLogLocator? locator = null,
        AmpThreadParser? parser = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new AmpLogLocator();
        _parser = parser ?? new AmpThreadParser();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var roots = _locator.ResolveDataDirectories(_configuredDataDir);
        var files = _locator.CollectThreadFiles(_configuredDataDir);

        var records = new List<UsageRecord>();
        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = files.Count,
        };

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = _parser.ParseThreadFile(file);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.InvalidRecords += outcome.InvalidRecords;
            records.AddRange(outcome.Records);
        }

        // 稳定排序（同时间戳保持文件/行序，与蓝本 sort_by_key 稳定语义一致）。
        var sorted = records.OrderBy(static record => record.Timestamp).ToList();
        diagnostics.MissingCostRecords = sorted.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = sorted.Count(static record => record.Model is null);
        diagnostics.Status = sorted.Count == 0
            ? JsonlScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }
}
