using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.Codebuff;

/// <summary>
/// Codebuff 直读适配器（I-018，蓝本 ../ccusage/rust/adapters/codebuff/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="CodebuffLogLocator"/>，CODEBUFF_DATA_DIR → ~/.config/&lt;channel&gt; 的
/// projects/**/chat-messages.json，channel 含 manicode/-dev/-staging）→ <b>逐文件</b>文件戳记账
/// （<see cref="PerFileParseCache{T}"/>，路径 + mtime + 大小，只 stat 不读内容）：戳未变的文件不重读
/// 不重解析，直接取用上轮产出；本轮零变化即复用上轮记录 → 否则逐文件解析
/// （<see cref="CodebuffChatParser"/>，单文件 JSON 数组）→
/// 跨文件 dedup key **last-wins** 去重（蓝本 HashMap insert 同口径：文件按路径排序后顺序折叠，
/// 同键后到者覆盖）→ 记录按时间戳稳定排序（蓝本 entries.sort_by_key(timestamp)）。
/// 不走共享 IncrementalJsonlScanner（单文件 JSON 非 JSONL）。记账必须逐文件：chat-messages.json
/// （尤其当前会话）每回合都在长，整批戳集全等才复用的口径会让单份活跃文件把整批历史拉回全量重读。
/// 准入：打不开 / stat 失败的文件不落账，下次重试整个文件。
/// 实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class CodebuffAdapter : ICodebuffAdapter
{
    private readonly string? _configuredDataDir;
    private readonly CodebuffLogLocator _locator;
    private readonly CodebuffChatParser _parser;
    private readonly PerFileParseCache<CodebuffFileOutcome> _cache = new();

    /// <summary>按文件记「存在解析错误」：状态聚合要纳入未重解析的历史文件。</summary>
    private readonly HashSet<string> _filesWithParseErrors = new(StringComparer.Ordinal);

    /// <summary>零变更轮缓存：全集合去重 + 排序的产物与上轮逐位相同，直接复用（同实例，消费方视为只读）。</summary>
    private List<UsageRecord>? _lastRecords;
    private (JsonlScanStatus Status, int Duplicates, int MissingCost, int UnknownModel)? _lastAggregate;

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

        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = files.Count,
        };
        // 本轮账面变化文件数（消失 + 新增/变更）。
        var filesChanged = _cache.RemoveMissing(files);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var probe = _cache.Probe(file, out var stamp, out _);
            if (probe == PerFileProbe.Reused)
            {
                // 未变更：不重读不重解析（本轮无解析事件，内容诊断不累加）。
                continue;
            }

            if (probe == PerFileProbe.StatFailed)
            {
                diagnostics.LinesSkipped++;
            }

            var outcome = _parser.ParseChatFile(file);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.InvalidRecords += outcome.InvalidRecords;

            if (probe != PerFileProbe.NeedsParse || outcome.FilesRead == 0)
            {
                // 打不开 / stat 失败：不落账，下次扫描重试整个文件。
                _cache.Forget(file);
                _filesWithParseErrors.Remove(file);
                continue;
            }

            _cache.Store(file, stamp, outcome);
            filesChanged++;
            if (outcome.MalformedLines > 0 || outcome.InvalidRecords > 0)
            {
                _filesWithParseErrors.Add(file);
            }
            else
            {
                _filesWithParseErrors.Remove(file);
            }
        }

        if (filesChanged == 0 && _lastRecords is { } cachedRecords && _lastAggregate is { } aggregate)
        {
            // 零变更轮：复用上一次的去重/排序产物（同实例），本轮诊断只反映本次扫描事件。
            return Task.FromResult(new JsonlScanResult
            {
                Roots = projectRoots,
                Records = cachedRecords,
                Diagnostics = new JsonlScanDiagnostics
                {
                    Status = aggregate.Status,
                    FilesDiscovered = files.Count,
                    FilesRead = 0,
                    LinesSkipped = diagnostics.LinesSkipped,
                    MalformedLines = 0,
                    InvalidRecords = 0,
                    DuplicateRecords = aggregate.Duplicates,
                    MissingCostRecords = aggregate.MissingCost,
                    UnknownModelRecords = aggregate.UnknownModel,
                    FilesChanged = files.Count > 0 ? 0 : null,
                },
            });
        }

        // 蓝本 deduped.insert(entry.dedup_key, entry)：last-wins，文件已按路径排序、文件内按数组
        // ordinal 顺序折叠——同键后到者覆盖。Dictionary 索引器赋值同口径。
        var deduped = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        var totalEntries = 0;
        foreach (var file in files)
        {
            // 本轮打不开的文件未落账，不贡献记录（下轮重试）。
            if (!_cache.TryGetStored(file, out var outcome))
            {
                continue;
            }

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
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0 || _filesWithParseErrors.Count > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        // FilesChanged：本轮账面变化的文件数；无文件不设值，由管道按「本轮发现即空」规则处理。
        diagnostics.FilesChanged = files.Count > 0 ? filesChanged : null;

        _lastRecords = sorted;
        _lastAggregate = (diagnostics.Status, diagnostics.DuplicateRecords, diagnostics.MissingCostRecords,
            diagnostics.UnknownModelRecords);

        return Task.FromResult(new JsonlScanResult
        {
            Roots = projectRoots,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }
}
