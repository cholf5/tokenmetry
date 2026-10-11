using System.Text.Json;
using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.ClaudeCodeJsonl;

/// <summary>
/// Claude Code JSONL 直读适配器：目录发现 → 文件枚举 → 行解析 → 消息级去重 → 统一记录 + 诊断计数。
/// 增量扫描、记账、去重与诊断状态由各 agent 适配器共享的 <see cref="IncrementalJsonlScanner"/> 承担
/// （I-004 起与 Codex 适配器共用）；本类只保留 Claude 特有的目录定位与行解析规则。
/// 重复运行同一目录不会产生重复记录；文件被删除后其记录随之移除。
/// 实例持有扫描状态，非线程安全；I-005 起以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class ClaudeCodeJsonlAdapter : IClaudeCodeJsonlAdapter
{
    private readonly string? _configuredRoot;
    private readonly ClaudeCodeLogLocator _locator;
    private readonly ClaudeCodeJsonlLineParser _lineParser;
    private readonly IncrementalJsonlScanner _scanner;

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
        _scanner = new IncrementalJsonlScanner(readerFactory, deduper);
    }

    public async Task<ClaudeCodeScanResult> ScanAsync(CancellationToken ct = default)
    {
        var roots = _locator.ResolveRoots(_configuredRoot);
        var files = ClaudeCodeLogLocator.EnumerateUsageFiles(roots);
        var output = await _scanner.ScanAsync(files, ParseFileAsync, ct);

        return new ClaudeCodeScanResult
        {
            Roots = roots,
            Records = output.Records,
            Diagnostics = output.Diagnostics,
        };
    }

    private async Task<JsonlFileParseOutcome> ParseFileAsync(string path, TextReader reader, CancellationToken ct)
    {
        var records = new List<UsageRecord>();
        var linesSkipped = 0;
        var malformedLines = 0;
        var invalidRecords = 0;

        var (fileSessionId, projectPath) = ClaudeCodeJsonlLineParser.ExtractSessionParts(path);

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

        return new JsonlFileParseOutcome(records, FilesRead: 1, linesSkipped, malformedLines, invalidRecords, Interrupted: false);
    }
}
