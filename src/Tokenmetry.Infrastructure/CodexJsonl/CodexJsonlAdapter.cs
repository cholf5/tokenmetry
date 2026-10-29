using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.CodexJsonl;

/// <summary>
/// Codex JSONL 直读适配器：目录发现 → 文件枚举（sessions / archived_sessions 按 home 去重）→
/// 逐文件解析 → 消息级去重 → 统一记录 + 诊断计数。
/// 增量扫描、记账、去重与诊断状态由各 agent 适配器共享的 <see cref="IncrementalJsonlScanner"/> 承担
/// （与 Claude 适配器同一实现，无分叉）；本类只保留 Codex 特有的目录定位与文件级解析规则。
/// 重复运行不产生重复记录；文件被删除后其记录随之移除。
/// 实例持有扫描状态，非线程安全；I-005 起以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class CodexJsonlAdapter : ICodexJsonlAdapter
{
    private readonly string? _configuredHome;
    private readonly CodexLogLocator _locator;
    private readonly CodexJsonlFileParser _fileParser;
    private readonly IncrementalJsonlScanner _scanner;

    public CodexJsonlAdapter(
        string? configuredHome = null,
        CodexLogLocator? locator = null,
        CodexJsonlFileParser? fileParser = null,
        ClaudeUsageRecordDeduper? deduper = null,
        Func<string, TextReader>? readerFactory = null)
    {
        _configuredHome = configuredHome;
        _locator = locator ?? new CodexLogLocator();
        _fileParser = fileParser ?? new CodexJsonlFileParser();
        _scanner = new IncrementalJsonlScanner(readerFactory ?? OpenRolloutReader, deduper);
    }

    /// <summary>
    /// 打开 rollout：`.jsonl.zst` 走 ZstdSharp 解压流，其余按 UTF-8 直读。
    /// 解压损坏在 <see cref="TextReader.Read"/> 时才暴露（ZstdException），由 <see cref="ScanAsync"/>
    /// 的解析委托按"整文件不可信"接住——与 DSH 同款语义（ZstdException 不继承 IOException，
    /// 共享扫描引擎的 IOException 过滤器接不住它）。
    /// </summary>
    private static TextReader OpenRolloutReader(string path)
    {
        var baseStream = AgentLogReader.OpenRead(path);
        return path.EndsWith(".jsonl.zst", StringComparison.Ordinal)
            ? new StreamReader(new ZstdSharp.DecompressionStream(baseStream), System.Text.Encoding.UTF8)
            : new StreamReader(baseStream, System.Text.Encoding.UTF8);
    }

    public async Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var sources = _locator.ResolveSources(_configuredHome);
        var files = CodexLogLocator.CollectDedupedFiles(sources);

        // 会话 id 由相对所属源目录的路径推导，解析前记录文件 → 源目录映射。
        var sourceDirByFile = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new List<string>(files.Count);
        foreach (var (sourceDir, file) in files)
        {
            paths.Add(file);
            sourceDirByFile[file] = sourceDir;
        }

        var output = await _scanner.ScanAsync(
            paths,
            async (path, reader, token) =>
            {
                try
                {
                    return await _fileParser
                        .ParseAsync(reader, sourceDirByFile[path], path, token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidDataException or ZstdSharp.ZstdException)
                {
                    // 压缩 rollout 结构/内容损坏：蓝本按读取失败弃全文件——不产出记录，计 1 次
                    // malformed 并让状态降级 Partial；文件级记账照常落账，避免每轮无限重试。
                    // 不接住会冲出 ScanAsync，让整轮 Codex 采集变 Error（所有文件都不落库）。
                    return new JsonlFileParseOutcome(
                        Records: [],
                        FilesRead: 0,
                        LinesSkipped: 0,
                        MalformedLines: 1,
                        InvalidRecords: 0,
                        Interrupted: false);
                }
            },
            ct);

        return new JsonlScanResult
        {
            Roots = sources.Select(source => source.DirectoryPath).ToList(),
            Records = output.Records,
            Diagnostics = output.Diagnostics,
        };
    }
}
