using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.GeminiCli;

/// <summary>
/// Gemini CLI 直读适配器（I-010，蓝本 ../ccusage/rust/adapters/gemini/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="GeminiLogLocator"/>，GEMINI_DATA_DIR → ~/.gemini/tmp 树下 .json/.jsonl，
/// 路径排序去重）→ <b>逐文件</b>文件戳记账（<see cref="PerFileParseCache{T}"/>，路径 + mtime + 大小，
/// 只 stat 不读内容）：戳未变的文件不重读不重解析，直接取用上轮产出；本轮零变化即复用上轮记录 →
/// 否则逐文件解析（<see cref="GeminiLogParser"/>，按扩展名分流整文件 JSON / JSONL）→ 事件按时间戳稳定
/// 排序（蓝本 events.sort_by_key(timestamp)，同刻保持文件/行序）→ 统一记录。
/// 与 JSONL 适配器不同，本适配器<b>不走共享 IncrementalJsonlScanner</b>：Gemini 是整文件 JSON 与
/// 行式 JSONL 混合源，共享引擎的消息级去重器（按 MessageId 跨文件折叠）与蓝本"事件 id 仅文件内就地
/// 替换、跨文件不去重"的语义冲突（记录 MessageId 需置 null 直通）。
/// 记账必须逐文件：tmp 下的活跃会话文件每回合都在长，整批戳集全等才复用的口径会让单份活跃文件把整批
/// 历史拉回全量重读。准入：打不开 / stat 失败的文件不落账，下次重试整个文件。
/// 实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class GeminiCliAdapter : IGeminiCliAdapter
{
    private readonly string? _configuredDataDir;
    private readonly GeminiLogLocator _locator;
    private readonly GeminiLogParser _parser;
    private readonly PerFileParseCache<GeminiFileOutcome> _cache = new();

    /// <summary>按文件记「存在解析错误」：状态聚合要纳入未重解析的历史文件（与共享扫描器同款）。</summary>
    private readonly HashSet<string> _filesWithParseErrors = new(StringComparer.Ordinal);

    /// <summary>零变更轮缓存：全集合排序的产物与上轮逐位相同，直接复用（同实例，消费方视为只读）。</summary>
    private List<UsageRecord>? _lastRecords;
    private (JsonlScanStatus Status, int Duplicates, int MissingCost, int UnknownModel)? _lastAggregate;

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

            var outcome = _parser.ParseFile(file);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;

            if (probe != PerFileProbe.NeedsParse || outcome.FilesRead == 0)
            {
                // 打不开 / stat 失败：不落账，下次扫描重试整个文件。
                _cache.Forget(file);
                _filesWithParseErrors.Remove(file);
                continue;
            }

            _cache.Store(file, stamp, outcome);
            filesChanged++;
            if (outcome.MalformedLines > 0)
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
            // 零变更轮：复用上一次的排序产物（同实例），本轮诊断只反映本次扫描事件。
            return Task.FromResult(new JsonlScanResult
            {
                Roots = roots,
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

        var records = new List<UsageRecord>();
        foreach (var file in files)
        {
            // 本轮打不开的文件未落账，不贡献记录（下轮重试）。
            if (_cache.TryGetStored(file, out var outcome))
            {
                // 文件内事件就地替换计数：从落账产出取，未变文件的历史计数照旧计入（与全量重扫口径一致）。
                diagnostics.DuplicateRecords += outcome.DuplicateRecords;
                records.AddRange(outcome.Records);
            }
        }

        // 稳定排序（同时间戳保持文件/行序，与蓝本 sort_by_key 稳定语义一致）。
        var sorted = records.OrderBy(static record => record.Timestamp).ToList();
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
            Roots = roots,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }
}
