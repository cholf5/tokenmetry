using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;

namespace Tokenmetry.Infrastructure.GeminiCli;

/// <summary>
/// Gemini CLI 直读适配器（I-010，蓝本 ../ccusage/rust/adapters/gemini/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="GeminiLogLocator"/>，GEMINI_DATA_DIR → ~/.gemini/tmp 树下 .json/.jsonl，
/// 路径排序去重）→ 逐文件解析（<see cref="GeminiLogParser"/>，按扩展名分流整文件 JSON / JSONL）→
/// 事件按时间戳稳定排序（蓝本 events.sort_by_key(timestamp)，同刻保持文件/行序）→ 统一记录。
/// 与 JSONL 适配器不同，本适配器<b>不走共享 IncrementalJsonlScanner</b>：Gemini 是整文件 JSON 与
/// 行式 JSONL 混合源，共享引擎的消息级去重器（按 MessageId 跨文件折叠）与蓝本"事件 id 仅文件内
/// 就地替换、跨文件不去重"的语义冲突（记录 MessageId 需置 null 直通）；文件级 mtime 增量对聊天
/// 记录收益有限，按 SQLite 系适配器"每次全量重读"口径实现（I-009 同款），重读幂等由全量扫描保证。
/// 实例无扫描状态，线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class GeminiCliAdapter : IGeminiCliAdapter
{
    private readonly string? _configuredDataDir;
    private readonly GeminiLogLocator _locator;
    private readonly GeminiLogParser _parser;

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

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }
}
