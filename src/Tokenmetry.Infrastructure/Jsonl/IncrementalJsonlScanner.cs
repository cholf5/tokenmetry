using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;

namespace Tokenmetry.Infrastructure.Jsonl;

/// <summary>
/// JSONL 直读适配器共享的增量扫描引擎（I-001 claude / I-004 codex 共用）：
/// 文件级增量记账（mtime/大小）→ 只重新解析新增或变更的文件 → 消息级去重 → 统一诊断与状态。
/// 目录发现与行解析由各 agent 适配器注入（文件清单 + 逐文件解析委托），引擎不感知具体日志格式。
/// 实例持有扫描状态，非线程安全；I-005 起随适配器以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public sealed class IncrementalJsonlScanner
{
    private readonly Func<string, TextReader> _readerFactory;
    private readonly Func<IEnumerable<UsageRecord>, IReadOnlyList<UsageRecord>> _dedupe;
    private readonly Action<string, Exception>? _parseFailureLog;
    private readonly Dictionary<string, (DateTime ModifiedUtc, long Length)> _fileStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<UsageRecord>> _recordsByFile = new(StringComparer.Ordinal);
    // 按文件记忆"存在解析错误"，状态聚合时纳入未重新解析的历史文件，避免坏文件在第二次扫描后被误报为 Ok。
    private readonly HashSet<string> _filesWithParseErrors = new(StringComparer.Ordinal);
    // I-042 零变更轮缓存：文件账面未动时全历史 SelectMany + 去重的产物逐位相同，直接复用
    // 上一次的去重结果（同实例列表，消费方视为只读），不再对全历史重建——这是变更轮之外
    // 最大的常态分配源（百万行历史 ≈ 每轮 0.5-1 GB 瞬时尖峰）。累计口径诊断（Status /
    // Duplicate / MissingCost / UnknownModel）一并缓存，与本轮重建的逐位结果一致
    // （本轮无解析事件 → Malformed/Invalid 恒 0、_filesWithParseErrors 不动）。
    private List<UsageRecord>? _lastDeduped;
    private (JsonlScanStatus Status, int Duplicates, int MissingCost, int UnknownModel)? _lastAggregate;

    /// <summary>
    /// <paramref name="dedupe"/> 为消息级去重策略（I-011 Qwen / I-013 Kimi 起开放注入：两蓝本无
    /// message id，去重键为 (session, timestamp, model, token 计数) 的内容键 first-wins，与 Claude 的
    /// MessageId 去重语义不同）。缺省用 <see cref="ClaudeUsageRecordDeduper"/>。
    /// 优先级：<paramref name="dedupe"/> 非null 时<b>静默胜出</b>、<paramref name="deduper"/> 被忽略
    /// （两者互斥使用，不同时传入；不抛异常，保持构造简单）。
    /// <paramref name="parseFailureLog"/>（I-045，可选）：文件解析失败的落日志点——共享引擎在此
    /// 接住 IOException/UnauthorizedAccessException 并静默计数，调用方（适配器/管道）经此回调
    /// 拿到「哪个文件、什么异常」，写文件日志（含堆栈）。不在引擎内部直依赖 logger：引擎保持纯。
    /// </summary>
    public IncrementalJsonlScanner(
        Func<string, TextReader>? readerFactory = null,
        ClaudeUsageRecordDeduper? deduper = null,
        Func<IEnumerable<UsageRecord>, IReadOnlyList<UsageRecord>>? dedupe = null,
        Action<string, Exception>? parseFailureLog = null)
    {
        _readerFactory = readerFactory ?? (path => new StreamReader(AgentLogReader.OpenRead(path), System.Text.Encoding.UTF8));
        _dedupe = dedupe ?? (records => (deduper ?? new ClaudeUsageRecordDeduper()).Dedupe(records));
        _parseFailureLog = parseFailureLog;
    }

    /// <summary>
    /// 执行一次增量扫描。<paramref name="files"/> 为本次发现的全部 .jsonl 文件（调用方负责枚举与排序），
    /// <paramref name="parseFile"/> 打开文件后逐行解析并返回该文件的产出（含 FilesRead/LinesSkipped 等计数）。
    /// 重复运行同一文件清单不产生重复记录（文件级记账 + 消息级去重双重保证）。
    /// <paramref name="progress"/>（I-046，可选）：源内进行中进度回调——文件循环内按节流粒度
    /// （<see cref="ProgressEveryFiles"/> 个文件或 <see cref="ProgressEverySeconds"/> 秒，取大者防高频抖动）
    /// 上报 (已处理, 发现)；0 处理完成时不再追加回调，终态由完成登记承载。
    /// 回调在扫描线程同步发生，接收方必须便宜（生产 = IngestionScanRegistry 加锁快照）。
    /// </summary>
    public async Task<JsonlScanOutput> ScanAsync(
        IReadOnlyList<string> files,
        Func<string, TextReader, CancellationToken, Task<JsonlFileParseOutcome>> parseFile,
        CancellationToken ct,
        Action<int, int>? progress = null)
    {
        ct.ThrowIfCancellationRequested();

        var diagnostics = new JsonlScanDiagnostics();
        diagnostics.FilesDiscovered = files.Count;
        // I-042：本轮文件账面变化数（消失 + 新增/变更）。打不开（stamp 失败）与读取中途失败
        // 不落账 → 账面未动，不计入。
        var filesChanged = 0;
        // I-046 节流游标：每 N 文件 / 每 N 秒（取大者）上报一次进行中进度，快速小目录不产生回调风暴。
        var processed = 0;
        var lastReported = 0;
        var lastReportAt = DateTimeOffset.Now;

        // 先清理已消失文件的记账，与全量重扫语义一致。
        var currentFiles = new HashSet<string>(files, StringComparer.Ordinal);
        foreach (var gone in _recordsByFile.Keys.Where(file => !currentFiles.Contains(file)).ToList())
        {
            _recordsByFile.Remove(gone);
            _fileStates.Remove(gone);
            _filesWithParseErrors.Remove(gone);
            filesChanged++;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryGetFileStamp(file, out var stamp))
            {
                diagnostics.LinesSkipped++;
            }
            else if (_fileStates.TryGetValue(file, out var known) && known == stamp)
            {
                // 未变更：不解析，但计入已处理（进度口径 = 走完文件循环的步数）
            }
            else
            {
                var outcome = await ParseFileAsync(file, parseFile, ct);
                diagnostics.FilesRead += outcome.FilesRead;
                diagnostics.LinesSkipped += outcome.LinesSkipped;
                diagnostics.MalformedLines += outcome.MalformedLines;
                diagnostics.InvalidRecords += outcome.InvalidRecords;

                if (outcome.Interrupted)
                {
                    // 打不开或读取中途失败：保留上一次成功解析的记录，不落账，下次扫描重试整个文件。
                }
                else
                {
                    _recordsByFile[file] = outcome.Records;
                    _fileStates[file] = stamp;
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
            }

            processed++;
            if (progress is not null
                && (processed - lastReported >= ProgressEveryFiles
                    || (DateTimeOffset.Now - lastReportAt).TotalSeconds >= ProgressEverySeconds))
            {
                lastReported = processed;
                lastReportAt = DateTimeOffset.Now;
                progress(processed, files.Count);
            }
        }

        if (filesChanged == 0 && _lastDeduped is { } cached && _lastAggregate is { } aggregate)
        {
            // 零变更轮：复用上一次去重产物（同实例），本轮诊断只反映本次扫描事件
            // （FilesRead / Malformed / Invalid 恒 0，stamp 失败的 LinesSkipped 如实保留）；
            // 累计四项与 Status 沿用缓存——与"重建后必然得到的结果"逐位一致。
            return new JsonlScanOutput(cached, new JsonlScanDiagnostics
            {
                Status = aggregate.Status,
                FilesDiscovered = diagnostics.FilesDiscovered,
                FilesRead = 0,
                LinesSkipped = diagnostics.LinesSkipped,
                MalformedLines = 0,
                InvalidRecords = 0,
                DuplicateRecords = aggregate.Duplicates,
                MissingCostRecords = aggregate.MissingCost,
                UnknownModelRecords = aggregate.UnknownModel,
                FilesChanged = 0,
            });
        }

        var accumulated = _recordsByFile.Values.SelectMany(records => records).ToList();
        var deduped = _dedupe(accumulated).ToList();
        diagnostics.DuplicateRecords = accumulated.Count - deduped.Count;
        diagnostics.MissingCostRecords = deduped.Count(record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = deduped.Count(record => record.Model is null);
        diagnostics.Status = deduped.Count == 0
            ? JsonlScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0 || _filesWithParseErrors.Count > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        diagnostics.FilesChanged = filesChanged;

        _lastDeduped = deduped;
        _lastAggregate = (diagnostics.Status, diagnostics.DuplicateRecords, diagnostics.MissingCostRecords, diagnostics.UnknownModelRecords);

        return new JsonlScanOutput(deduped, diagnostics);
    }

    /// <summary>I-046：进行中进度上报的最小文件间隔（报告者建议 500 文件）。</summary>
    public const int ProgressEveryFiles = 500;

    /// <summary>I-046：进行中进度上报的最小时间间隔（秒）——与文件间隔取大者，慢盘大文件不致久无信号。</summary>
    public const double ProgressEverySeconds = 2;

    private async Task<JsonlFileParseOutcome> ParseFileAsync(
        string path,
        Func<string, TextReader, CancellationToken, Task<JsonlFileParseOutcome>> parseFile,
        CancellationToken ct)
    {
        TextReader reader;
        try
        {
            reader = _readerFactory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 与蓝本一致：打不开的文件计入 lines_skipped，且不计入 files_read；不落账，下次扫描重试。
            _parseFailureLog?.Invoke(path, ex);
            return JsonlFileParseOutcome.Failure(linesSkipped: 1);
        }

        try
        {
            return await parseFile(path, reader, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取中途失败：丢弃本次部分结果与其计数，不落账，下次扫描重读整个文件。
            _parseFailureLog?.Invoke(path, ex);
            return JsonlFileParseOutcome.Failure(linesSkipped: 0);
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static bool TryGetFileStamp(string path, out (DateTime ModifiedUtc, long Length) stamp)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                stamp = default;
                return false;
            }

            stamp = (info.LastWriteTimeUtc, info.Length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = default;
            return false;
        }
    }
}
