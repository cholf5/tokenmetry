using CCUsage.Core.Models;
using CCUsage.Infrastructure.ClaudeCodeJsonl;

namespace CCUsage.Infrastructure.Jsonl;

/// <summary>
/// JSONL 直读适配器共享的增量扫描引擎（I-001 claude / I-004 codex 共用）：
/// 文件级增量记账（mtime/大小）→ 只重新解析新增或变更的文件 → 消息级去重 → 统一诊断与状态。
/// 目录发现与行解析由各 agent 适配器注入（文件清单 + 逐文件解析委托），引擎不感知具体日志格式。
/// 实例持有扫描状态，非线程安全；本卡不接入 App DI（接线为 I-005 范围）。
/// </summary>
public sealed class IncrementalJsonlScanner
{
    private readonly Func<string, TextReader> _readerFactory;
    private readonly ClaudeUsageRecordDeduper _deduper;
    private readonly Dictionary<string, (DateTime ModifiedUtc, long Length)> _fileStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<UsageRecord>> _recordsByFile = new(StringComparer.Ordinal);
    // 按文件记忆"存在解析错误"，状态聚合时纳入未重新解析的历史文件，避免坏文件在第二次扫描后被误报为 Ok。
    private readonly HashSet<string> _filesWithParseErrors = new(StringComparer.Ordinal);

    public IncrementalJsonlScanner(Func<string, TextReader>? readerFactory = null, ClaudeUsageRecordDeduper? deduper = null)
    {
        _readerFactory = readerFactory ?? (path => new StreamReader(path));
        _deduper = deduper ?? new ClaudeUsageRecordDeduper();
    }

    /// <summary>
    /// 执行一次增量扫描。<paramref name="files"/> 为本次发现的全部 .jsonl 文件（调用方负责枚举与排序），
    /// <paramref name="parseFile"/> 打开文件后逐行解析并返回该文件的产出（含 FilesRead/LinesSkipped 等计数）。
    /// 重复运行同一文件清单不产生重复记录（文件级记账 + 消息级去重双重保证）。
    /// </summary>
    public async Task<JsonlScanOutput> ScanAsync(
        IReadOnlyList<string> files,
        Func<string, TextReader, CancellationToken, Task<JsonlFileParseOutcome>> parseFile,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var diagnostics = new JsonlScanDiagnostics();
        diagnostics.FilesDiscovered = files.Count;

        // 先清理已消失文件的记账，与全量重扫语义一致。
        var currentFiles = new HashSet<string>(files, StringComparer.Ordinal);
        foreach (var gone in _recordsByFile.Keys.Where(file => !currentFiles.Contains(file)).ToList())
        {
            _recordsByFile.Remove(gone);
            _fileStates.Remove(gone);
            _filesWithParseErrors.Remove(gone);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryGetFileStamp(file, out var stamp))
            {
                diagnostics.LinesSkipped++;
                continue;
            }

            if (_fileStates.TryGetValue(file, out var known) && known == stamp)
            {
                continue;
            }

            var outcome = await ParseFileAsync(file, parseFile, ct);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.InvalidRecords += outcome.InvalidRecords;

            if (outcome.Interrupted)
            {
                // 打不开或读取中途失败：保留上一次成功解析的记录，不落账，下次扫描重试整个文件。
                continue;
            }

            _recordsByFile[file] = outcome.Records;
            _fileStates[file] = stamp;
            if (outcome.MalformedLines > 0 || outcome.InvalidRecords > 0)
            {
                _filesWithParseErrors.Add(file);
            }
            else
            {
                _filesWithParseErrors.Remove(file);
            }
        }

        var accumulated = _recordsByFile.Values.SelectMany(records => records).ToList();
        var deduped = _deduper.Dedupe(accumulated);
        diagnostics.DuplicateRecords = accumulated.Count - deduped.Count;
        diagnostics.MissingCostRecords = deduped.Count(record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = deduped.Count(record => record.Model is null);
        diagnostics.Status = deduped.Count == 0
            ? JsonlScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0 || _filesWithParseErrors.Count > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;

        return new JsonlScanOutput(deduped, diagnostics);
    }

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
            return JsonlFileParseOutcome.Failure(linesSkipped: 1);
        }

        try
        {
            return await parseFile(path, reader, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取中途失败：丢弃本次部分结果与其计数，不落账，下次扫描重读整个文件。
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
