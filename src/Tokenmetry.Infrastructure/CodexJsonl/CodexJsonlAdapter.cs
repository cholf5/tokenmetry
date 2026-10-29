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
    /// 解压失败转成 <see cref="InvalidDataException"/>（IOException 子类）交给共享扫描引擎
    /// 按"整文件不可信"跳过并下轮重试，与 DSH 的 zstd 失败语义一致。
    /// </summary>
    private static TextReader OpenRolloutReader(string path)
    {
        var baseStream = AgentLogReader.OpenRead(path);
        if (!path.EndsWith(".jsonl.zst", StringComparison.Ordinal))
        {
            return new StreamReader(baseStream, System.Text.Encoding.UTF8);
        }

        try
        {
            return new StreamReader(new ZstdSharp.DecompressionStream(baseStream), System.Text.Encoding.UTF8);
        }
        catch (Exception ex) when (ex is ZstdSharp.ZstdException or InvalidDataException)
        {
            baseStream.Dispose();
            throw new InvalidDataException($"Codex rollout is not readable: {path}", ex);
        }
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
            (path, reader, token) => _fileParser.ParseAsync(reader, sourceDirByFile[path], path, token),
            ct);

        return new JsonlScanResult
        {
            Roots = sources.Select(source => source.DirectoryPath).ToList(),
            Records = output.Records,
            Diagnostics = output.Diagnostics,
        };
    }
}
