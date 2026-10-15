using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Core.Services;
using Tokenmetry.Infrastructure.ClaudeCodeJsonl;
using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.DshSession;

/// <summary>
/// DSH 会话日志直读适配器（I-007）：目录发现（<see cref="DshLogLocator"/>，每个会话目录只读
/// 最高版本日志）→ 共享增量扫描引擎（<see cref="IncrementalJsonlScanner"/>，文件 mtime/大小记账，
/// 变更文件整文件重解析）→ 逐文件解析（<see cref="DshSessionLogParser"/>，fold + 模型归因 + 计价）→
/// 统一记录 + 诊断计数。zstd 文件经 <see cref="DshZstdLogStream"/> 逐帧解压为文本流
/// （每帧独立可解压、容忍尾部 torn frame，不整文件入内存），未压缩日志直读。
/// 无消息级去重标识（MessageId/RequestId 为 null），共享去重器对 null id 直通；
/// 重复保证由"每会话目录恰一份最高版本工件 + 文件级记账"承担。
/// 实例持有扫描状态，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class DshSessionAdapter : IDshSessionAdapter
{
    private readonly string? _configuredHome;
    private readonly DshLogLocator _locator;
    private readonly DshSessionLogParser _fileParser;
    private readonly IncrementalJsonlScanner _scanner;

    public DshSessionAdapter(
        string? configuredHome = null,
        DshLogLocator? locator = null,
        DshSessionLogParser? fileParser = null,
        ClaudeUsageRecordDeduper? deduper = null)
    {
        _configuredHome = configuredHome;
        _locator = locator ?? new DshLogLocator();
        _fileParser = fileParser ?? new DshSessionLogParser();
        _scanner = new IncrementalJsonlScanner(OpenLogReader, deduper);
    }

    public async Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var home = _locator.ResolveHome(_configuredHome);
        var artifacts = _locator.CollectSessionArtifacts(_configuredHome);

        var paths = new List<string>(artifacts.Count);
        var artifactByPath = new Dictionary<string, DshSessionArtifact>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            paths.Add(artifact.LogPath);
            artifactByPath[artifact.LogPath] = artifact;
        }

        var output = await _scanner.ScanAsync(
            paths,
            async (path, reader, token) =>
            {
                var artifact = artifactByPath[path];
                try
                {
                    return await _fileParser.ParseAsync(
                        reader,
                        path,
                        FallbackProjectPath(artifact),
                        artifact.SessionDirectoryName,
                        token).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    // zstd 结构损坏（错误 magic / 保留位 / 保留块类型 / 声明大小不符）：
                    // 蓝本 readSessionLog 按读取失败弃全文件——不产出任何记录，计 1 次 malformed
                    // 并让状态降级 Partial；文件级记账照常落账，避免每次扫描无限重试。
                    return new JsonlFileParseOutcome(
                        Records: [],
                        FilesRead: 0,
                        LinesSkipped: 0,
                        MalformedLines: 1,
                        InvalidRecords: 0,
                        Interrupted: false);
                }
            },
            ct).ConfigureAwait(false);

        return new JsonlScanResult
        {
            Roots = home is null ? [] : [home],
            Records = output.Records,
            Diagnostics = output.Diagnostics,
        };
    }

    /// <summary>
    /// 项目路径回退值：项目目录名解码（会话头行 cwd 是真实来源，解析器捕获后覆盖本回退）。
    /// 目录名编码（projectKey）有损：分隔符与段内 '-' 不可区分，~XXXX 转义精确还原，
    /// '-' 保持原样不做分隔猜测；非 "--…--" 形态（如 _no-cwd）落 "Unknown Project"。
    /// </summary>
    private static string FallbackProjectPath(DshSessionArtifact artifact)
        => DshProjectDirectory.DecodeProjectKey(artifact.ProjectDirectoryName) ?? "Unknown Project";

    /// <summary>按扩展名打开日志流：.zstd 走逐帧解压流，否则按 UTF-8 文本直读。</summary>
    private static TextReader OpenLogReader(string path)
    {
        var baseStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Stream stream = path.EndsWith(".zstd", StringComparison.Ordinal)
                ? new DshZstdLogStream(baseStream)
                : baseStream;
            return new StreamReader(stream, System.Text.Encoding.UTF8);
        }
        catch
        {
            baseStream.Dispose();
            throw;
        }
    }
}
