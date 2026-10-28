using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.GeminiCli;

/// <summary>
/// Gemini CLI 直读适配器（I-010，蓝本 ../ccusage/rust/adapters/gemini/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="GeminiLogLocator"/>，GEMINI_DATA_DIR → ~/.gemini/tmp 树下 .json/.jsonl，
/// 路径排序去重）→ 文件戳探针（<see cref="FileStampMemo"/>，路径 + mtime + 大小，只 stat 不读内容）→
/// 账面未动复用上轮记录（零读取零解析）→ 否则逐文件解析（<see cref="GeminiLogParser"/>，按扩展名
/// 分流整文件 JSON / JSONL）→ 事件按时间戳稳定排序（蓝本 events.sort_by_key(timestamp)，同刻保持
/// 文件/行序）→ 统一记录。
/// 与 JSONL 适配器不同，本适配器<b>不走共享 IncrementalJsonlScanner</b>：Gemini 是整文件 JSON 与
/// 行式 JSONL 混合源，共享引擎的消息级去重器（按 MessageId 跨文件折叠）与蓝本"事件 id 仅文件内
/// 就地替换、跨文件不去重"的语义冲突（记录 MessageId 需置 null 直通）；以 I-042 文件戳记忆化替代
/// 逐文件增量（与共享扫描器的零变更缓存同口径）。记忆化准入：全部发现文件都成功读取才落账，
/// 有文件打不开的轮次清空记忆下一轮重试（见 <see cref="FileStampMemo"/>）。
/// 实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class GeminiCliAdapter : IGeminiCliAdapter
{
    private readonly string? _configuredDataDir;
    private readonly GeminiLogLocator _locator;
    private readonly GeminiLogParser _parser;
    private readonly FileStampMemo _memo = new();

    public GeminiCliAdapter(
        string? configuredDataDir = null,
        GeminiLogLocator? locator = null,
        GeminiLogParser? parser = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new GeminiLogLocator();
        _parser = parser ?? new GeminiLogParser();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var roots = _locator.ResolveDataDirectories(_configuredDataDir);
        var files = _locator.CollectLogFiles(roots);
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
            var outcome = _parser.ParseFile(file);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.DuplicateRecords += outcome.DuplicateRecords;
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
