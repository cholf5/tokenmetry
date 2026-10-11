using System.Text.Json;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.ClaudeCodeJsonl;

/// <summary>
/// Claude Code JSONL 直读适配器：目录发现 → 文件枚举 → 行解析 → 消息级去重 → 统一记录 + 诊断计数。
/// 增量扫描：按文件 mtime/大小在进程内记账，只重新解析新增或变更的文件；文件被删除后其记录随之移除。
/// 重复运行同一目录不会产生重复记录（文件级记账 + 消息级去重双重保证）。
/// 实例持有扫描状态，非线程安全；本卡不接入 App DI（接线为 I-005 范围）。
/// </summary>
public class ClaudeCodeJsonlAdapter : IClaudeCodeJsonlAdapter
{
    private readonly string? _configuredRoot;
    private readonly ClaudeCodeLogLocator _locator;
    private readonly ClaudeCodeJsonlLineParser _lineParser;
    private readonly ClaudeUsageRecordDeduper _deduper;
    private readonly Func<string, TextReader> _readerFactory;
    private readonly Dictionary<string, (DateTime ModifiedUtc, long Length)> _fileStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<UsageRecord>> _recordsByFile = new(StringComparer.Ordinal);
    // 按文件记忆"存在解析错误"，状态聚合时纳入未重新解析的历史文件，避免坏文件在第二次扫描后被误报为 Ok。
    private readonly HashSet<string> _filesWithParseErrors = new(StringComparer.Ordinal);

    public ClaudeCodeJsonlAdapter(
        string? configuredRoot = null,
        ClaudeCodeLogLocator? locator = null,
        ClaudeCodeJsonlLineParser? lineParser = null,
        ClaudeUsageRecordDeduper? deduper = null,
        Func<string, TextReader>? readerFactory = null)
    {
        _configuredRoot = configuredRoot;
        _locator = locator ?? new ClaudeCodeLogLocator();
        _lineParser = lineParser ?? new ClaudeCodeJsonlLineParser();
        _deduper = deduper ?? new ClaudeUsageRecordDeduper();
        _readerFactory = readerFactory ?? (path => new StreamReader(path));
    }

    public async Task<ClaudeCodeScanResult> ScanAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var diagnostics = new ClaudeCodeScanDiagnostics();
        var roots = _locator.ResolveRoots(_configuredRoot);
        var files = ClaudeCodeLogLocator.EnumerateUsageFiles(roots);
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

            var outcome = await ParseFileAsync(file, ct);
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
            ? ClaudeCodeScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0 || _filesWithParseErrors.Count > 0
                ? ClaudeCodeScanStatus.Partial
                : ClaudeCodeScanStatus.Ok;

        return new ClaudeCodeScanResult
        {
            Roots = roots,
            Records = deduped,
            Diagnostics = diagnostics,
        };
    }

    private async Task<FileParseOutcome> ParseFileAsync(string path, CancellationToken ct)
    {
        TextReader reader;
        try
        {
            reader = _readerFactory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 与 claude.rs 一致：打不开的文件计入 lines_skipped，且不计入 files_read；不落账，下次扫描重试。
            return FileParseOutcome.Failure(linesSkipped: 1);
        }

        var records = new List<UsageRecord>();
        var linesSkipped = 0;
        var malformedLines = 0;
        var invalidRecords = 0;
        var filesRead = 0;

        try
        {
            var (fileSessionId, projectPath) = ClaudeCodeJsonlLineParser.ExtractSessionParts(path);
            filesRead = 1;

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.Contains(ClaudeCodeJsonlLineParser.UsageMarker, StringComparison.Ordinal))
                {
                    linesSkipped++;
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    malformedLines++;
                    continue;
                }

                using (document)
                {
                    var record = _lineParser.Parse(document.RootElement, fileSessionId, projectPath, path);
                    if (record is null)
                    {
                        invalidRecords++;
                        continue;
                    }

                    records.Add(record);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取中途失败：丢弃本次部分结果与其计数，不落账，下次扫描重读整个文件。
            return FileParseOutcome.Failure(linesSkipped: 0);
        }
        finally
        {
            reader.Dispose();
        }

        return new FileParseOutcome(records, filesRead, linesSkipped, malformedLines, invalidRecords, Interrupted: false);
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

    /// <summary>单个文件的解析产出；计数为该文件口径，由 ScanAsync 折叠进全局诊断。</summary>
    private sealed record FileParseOutcome(
        List<UsageRecord> Records,
        int FilesRead,
        int LinesSkipped,
        int MalformedLines,
        int InvalidRecords,
        bool Interrupted)
    {
        public static FileParseOutcome Failure(int linesSkipped) =>
            new([], FilesRead: 0, linesSkipped, MalformedLines: 0, InvalidRecords: 0, Interrupted: true);
    }
}
