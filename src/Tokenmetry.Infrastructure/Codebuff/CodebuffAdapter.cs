using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.Codebuff;

/// <summary>
/// Codebuff 直读适配器（I-018，蓝本 ../ccusage/rust/adapters/codebuff/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="CodebuffLogLocator"/>，CODEBUFF_DATA_DIR → ~/.config/&lt;channel&gt; 的
/// projects/**/chat-messages.json，channel 含 manicode/-dev/-staging）→ 文件戳探针
/// （<see cref="FileStampMemo"/>，路径 + mtime + 大小，只 stat 不读内容）→ 账面未动复用上轮记录
/// （零读取零解析）→ 否则逐文件解析（<see cref="CodebuffChatParser"/>，单文件 JSON 数组）→
/// 跨文件 dedup key **last-wins** 去重（蓝本 HashMap insert 同口径：文件按路径排序后顺序折叠，
/// 同键后到者覆盖）→ 记录按时间戳稳定排序（蓝本 entries.sort_by_key(timestamp)）。
/// 不走共享 IncrementalJsonlScanner（单文件 JSON 非 JSONL），以 I-042 文件戳记忆化替代逐文件增量
/// （与共享扫描器的零变更缓存同口径）。记忆化准入：全部发现文件都成功读取才落账，有文件打不开的
/// 轮次清空记忆下一轮重试（见 <see cref="FileStampMemo"/>）。
/// 实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class CodebuffAdapter : ICodebuffAdapter
{
    private readonly string? _configuredDataDir;
    private readonly CodebuffLogLocator _locator;
    private readonly CodebuffChatParser _parser;
    private readonly FileStampMemo _memo = new();

    public CodebuffAdapter(
        string? configuredDataDir = null,
        CodebuffLogLocator? locator = null,
        CodebuffChatParser? parser = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new CodebuffLogLocator();
        _parser = parser ?? new CodebuffChatParser();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var projectRoots = _locator.ResolveProjectRoots(_configuredDataDir);
        var files = _locator.CollectChatFiles(projectRoots);
        var stamps = FileStampMemo.Capture(files);

        if (_memo.TryReuse(stamps, files.Count, projectRoots, out var reused))
        {
            return Task.FromResult(reused);
        }

        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = files.Count,
        };

        // 蓝本 deduped.insert(entry.dedup_key, entry)：last-wins，文件已按路径排序、文件内按数组
        // ordinal 顺序折叠——同键后到者覆盖。Dictionary 索引器赋值同口径。
        var deduped = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        var totalEntries = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = _parser.ParseChatFile(file);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.InvalidRecords += outcome.InvalidRecords;
            foreach (var entry in outcome.Entries)
            {
                deduped[entry.DedupKey] = entry.Record;
                totalEntries++;
            }
        }

        diagnostics.DuplicateRecords = totalEntries - deduped.Count;

        // 稳定排序（同时间戳保持文件/行序，与蓝本 sort_by_key 稳定语义一致）。
        var sorted = deduped.Values.OrderBy(static record => record.Timestamp).ToList();
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
            Roots = projectRoots,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }
}
