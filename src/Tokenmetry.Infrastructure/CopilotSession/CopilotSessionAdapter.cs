using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.CopilotSession;

/// <summary>
/// GitHub Copilot CLI 直读适配器（I-008 主线 + I-023 otel 合并与积分对账）：
/// 目录发现（<see cref="CopilotLogLocator"/>：COPILOT_HOME → ~/.copilot 的
/// session-state/&lt;session&gt;/events.jsonl 一级会话目录 + otel/ 递归 jsonl +
/// COPILOT_OTEL_FILE_EXPORTER_PATH 单文件）→ 文件级增量记账（mtime/大小，变更文件整文件重解析）→
/// 逐文件解析（<see cref="CopilotSessionParser"/> 累计快照 + 积分快照；<see cref="CopilotOtelParser"/>
/// 四形态逐调用行）→ <see cref="CopilotUsageReconciler"/> 多源取优合并（快照差分、otel × shutdown
/// 接管、credit gap）产出统一记录。
/// <para>
/// 增量记账为本适配器自有（语义镜像共享 <see cref="IncrementalJsonlScanner"/>：mtime/大小键、
/// 打不开计 LinesSkipped 不落账下次重试、变更文件整文件重解析），不复用共享引擎——取优合并与
/// 积分对账需要跨文件池化的中间记录（快照请求数 / nanoAiu / 积分快照），统一记录
/// <see cref="UsageRecord"/> 无对应字段可承载，共享引擎仅缓存统一记录无法满足（卡片 Dev Log）。
/// 合并为纯函数，每次扫描对当前池整体重算（蓝本 load_entries_inner 同构），无跨扫描状态可失步；
/// (session, model) 组不跨文件（分组键含会话目录名），池化差分与蓝本全量池化严格等价（I-008 论证）。
/// </para>
/// 行级容错口径与 I-008 一致（坏 JSON 计 MalformedLines、预过滤未命中计 LinesSkipped、缺可解析
/// 时间戳计 InvalidRecords）；去重在解析 / 合并内按蓝本 dedup key 完成，DuplicateRecords 恒 0。
/// 实例持有扫描状态，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class CopilotSessionAdapter : ICopilotSessionAdapter
{
    private readonly string? _configuredHome;
    private readonly CopilotLogLocator _locator;
    private readonly CopilotSessionParser _fileParser;
    private readonly CopilotOtelParser _otelParser;
    private readonly Dictionary<string, (DateTime ModifiedUtc, long Length, CopilotSessionFileParse Parse)> _sessionStateFiles
        = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime ModifiedUtc, long Length, CopilotOtelFileParse Parse)> _otelFiles
        = new(StringComparer.Ordinal);
    // 按文件记忆"存在解析错误"，状态聚合时纳入未重新解析的历史文件，避免坏文件在第二次扫描后被误报为 Ok。
    private readonly HashSet<string> _filesWithParseErrors = new(StringComparer.Ordinal);

    public CopilotSessionAdapter(
        string? configuredHome = null,
        CopilotLogLocator? locator = null,
        CopilotSessionParser? fileParser = null,
        CopilotOtelParser? otelParser = null)
    {
        _configuredHome = configuredHome;
        _locator = locator ?? new CopilotLogLocator();
        _fileParser = fileParser ?? new CopilotSessionParser();
        _otelParser = otelParser ?? new CopilotOtelParser();
    }

    public async Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var home = _locator.ResolveHome(_configuredHome);
        var sources = _locator.CollectSourceFiles(_configuredHome);

        // 先清理已消失文件的记账，与全量重扫语义一致。
        var currentFiles = new HashSet<string>(sources.Select(file => file.Path), StringComparer.Ordinal);
        foreach (var gone in _sessionStateFiles.Keys.Where(file => !currentFiles.Contains(file)).ToList())
        {
            _sessionStateFiles.Remove(gone);
            _filesWithParseErrors.Remove(gone);
        }

        foreach (var gone in _otelFiles.Keys.Where(file => !currentFiles.Contains(file)).ToList())
        {
            _otelFiles.Remove(gone);
            _filesWithParseErrors.Remove(gone);
        }

        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = sources.Count,
        };

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryGetFileStamp(source.Path, out var stamp))
            {
                diagnostics.LinesSkipped++;
                continue;
            }

            if (IsUnchanged(source, stamp))
            {
                continue;
            }

            CopilotFileParseCounters counters;
            var interrupted = false;
            if (source.Kind == CopilotSourceKind.SessionState)
            {
                var (parse, wasInterrupted) = await ParseFileAsync(
                    (reader, token) => _fileParser.ParseAsync(reader, source.Path, token), source.Path, ct).ConfigureAwait(false);
                interrupted = wasInterrupted;
                counters = parse.Counters;
                if (!interrupted)
                {
                    _sessionStateFiles[source.Path] = (stamp.ModifiedUtc, stamp.Length, parse);
                }
            }
            else
            {
                var (parse, wasInterrupted) = await ParseFileAsync(
                    (reader, token) => _otelParser.ParseAsync(reader, source.Path, token), source.Path, ct).ConfigureAwait(false);
                interrupted = wasInterrupted;
                counters = parse.Counters;
                if (!interrupted)
                {
                    _otelFiles[source.Path] = (stamp.ModifiedUtc, stamp.Length, parse);
                }
            }

            diagnostics.LinesSkipped += counters.LinesSkipped;
            diagnostics.MalformedLines += counters.MalformedLines;
            diagnostics.InvalidRecords += counters.InvalidRecords;
            if (interrupted)
            {
                // 打不开或读取中途失败：计数进诊断但不落账（保留上一次成功解析的产出），下次扫描重试。
                continue;
            }

            diagnostics.FilesRead += counters.FilesRead;
            if (counters.MalformedLines > 0 || counters.InvalidRecords > 0)
            {
                _filesWithParseErrors.Add(source.Path);
            }
            else
            {
                _filesWithParseErrors.Remove(source.Path);
            }
        }

        // 按文件清单（路径排序）顺序池化：与蓝本"源文件按路径排序后顺序解析"的池序一致，
        // 保证去重 last-wins / otel 行序的确定性。发现但从未成功解析的文件（打不开 / 读中途失败
        // 未落账、发现后消失）无字典条目——按共享 IncrementalJsonlScanner 的"不贡献"语义以空
        // parse 参与（TryGetValue，不得用索引器：KeyNotFoundException 会冲出 ScanAsync 崩掉整次刷新，
        // I-023 独立审查 P1）。
        var sessionParses = sources
            .Where(file => file.Kind == CopilotSourceKind.SessionState)
            .Select(file => _sessionStateFiles.TryGetValue(file.Path, out var sessionParse)
                ? sessionParse.Parse
                : EmptySessionParse)
            .ToList();
        var otelParses = sources
            .Where(file => file.Kind == CopilotSourceKind.Otel)
            .Select(file => _otelFiles.TryGetValue(file.Path, out var otelParse)
                ? otelParse.Parse
                : EmptyOtelParse)
            .ToList();

        var reconciliation = CopilotUsageReconciler.Reconcile(sessionParses, otelParses);
        diagnostics.DuplicateRecords = reconciliation.DuplicateRecords;
        diagnostics.MissingCostRecords = reconciliation.MissingCostRecords;
        diagnostics.UnknownModelRecords = reconciliation.UnknownModelRecords;
        diagnostics.Status = reconciliation.Records.Count == 0
            ? JsonlScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0 || _filesWithParseErrors.Count > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;

        return new JsonlScanResult
        {
            Roots = home is null ? [] : [home],
            Records = reconciliation.Records,
            Diagnostics = diagnostics,
        };
    }

    private bool IsUnchanged(CopilotSourceFile source, (DateTime ModifiedUtc, long Length) stamp)
        => source.Kind == CopilotSourceKind.SessionState
            ? _sessionStateFiles.TryGetValue(source.Path, out var knownSession)
              && knownSession.ModifiedUtc == stamp.ModifiedUtc && knownSession.Length == stamp.Length
            : _otelFiles.TryGetValue(source.Path, out var knownOtel)
              && knownOtel.ModifiedUtc == stamp.ModifiedUtc && knownOtel.Length == stamp.Length;

    /// <summary>"未成功解析文件不贡献记录"语义的空 parse（共享扫描器 Failure 形态，零计数）。</summary>
    private static CopilotSessionFileParse EmptySessionParse { get; } =
        new([], [], new CopilotFileParseCounters(FilesRead: 0, LinesSkipped: 0, MalformedLines: 0, InvalidRecords: 0));

    private static CopilotOtelFileParse EmptyOtelParse { get; } =
        new([], new CopilotFileParseCounters(FilesRead: 0, LinesSkipped: 0, MalformedLines: 0, InvalidRecords: 0));

    /// <summary>
    /// 打开并解析单文件（共享扫描引擎 ParseFileAsync 同构）：打不开 / 中途失败时 Interrupted=true，
    /// 调用方保留该文件上一次成功解析的产出，下次扫描重试。
    /// </summary>
    private static async Task<(TParse Parse, bool Interrupted)> ParseFileAsync<TParse>(
        Func<TextReader, CancellationToken, Task<TParse>> parseFile,
        string path,
        CancellationToken ct)
        where TParse : ICopilotFileParse
    {
        TextReader reader;
        try
        {
            reader = new StreamReader(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 与共享扫描引擎一致：打不开的文件计入 LinesSkipped 且不计入 FilesRead；不落账，下次重试。
            return (ZeroParse<TParse>(linesSkipped: 1), true);
        }

        try
        {
            return (await parseFile(reader, ct).ConfigureAwait(false), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取中途失败：丢弃本次部分结果与其计数，不落账，下次重读整个文件。
            return (ZeroParse<TParse>(linesSkipped: 0), true);
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static TParse ZeroParse<TParse>(int linesSkipped)
        where TParse : ICopilotFileParse
    {
        var counters = new CopilotFileParseCounters(FilesRead: 0, linesSkipped, MalformedLines: 0, InvalidRecords: 0);
        if (typeof(TParse) == typeof(CopilotSessionFileParse))
        {
            return (TParse)(object)new CopilotSessionFileParse([], [], counters);
        }

        return (TParse)(object)new CopilotOtelFileParse([], counters);
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
