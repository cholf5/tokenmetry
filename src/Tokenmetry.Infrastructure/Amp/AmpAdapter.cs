using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.Amp;

/// <summary>
/// Amp 直读适配器（I-014，蓝本 ../ccusage/rust/adapters/amp/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="AmpLogLocator"/>，AMP_DATA_DIR → ~/.local/share/amp 的 threads/ 子树 .json
/// 线程文件）→ 文件戳探针（<see cref="FileStampMemo"/>，路径 + mtime + 大小，只 stat 不读内容）→
/// 账面未动复用上轮记录（零读取零解析）→ 否则逐文件解析（<see cref="AmpThreadParser"/>，
/// usageLedger.events 优先、缺失回退 messages 用量）→ 记录按时间戳稳定排序（蓝本
/// entries.sort_by_key(timestamp)）。
/// 不走共享 IncrementalJsonlScanner（单层 JSON 非 JSONL，且蓝本无任何消息级去重语义），
/// 以 I-042 文件戳记忆化替代逐文件增量（与共享扫描器的零变更缓存同口径）。记忆化准入：
/// 全部发现文件都成功读取才落账，有文件打不开的轮次清空记忆下一轮重试（见 <see cref="FileStampMemo"/>）。
/// 实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由采集管道信号量串行化）。
/// </summary>
public class AmpAdapter : IAmpAdapter
{
    private readonly string? _configuredDataDir;
    private readonly AmpLogLocator _locator;
    private readonly AmpThreadParser _parser;
    private readonly FileStampMemo _memo = new();

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
        var stamps = FileStampMemo.Capture(files);

        if (_memo.TryReuse(stamps, files.Count, roots, out var reused))
        {
            return Task.FromResult(reused);
        }

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
        // FilesChanged：走到全量读取即本轮账面有变化（有文件填 1）；无文件不设值，
        // 由管道按"本轮发现即空"规则处理（与 ZCode 探针同口径）。
        diagnostics.FilesChanged = files.Count > 0 ? 1 : null;

        // 记忆化准入：全部发现文件都成功读取才落账（有文件打不开 → null 戳清空记忆，下轮重试）。
        _memo.Update(diagnostics.FilesRead == files.Count ? stamps : null, sorted, diagnostics);

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }
}
