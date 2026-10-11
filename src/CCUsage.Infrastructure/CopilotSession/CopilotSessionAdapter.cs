using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.ClaudeCodeJsonl;
using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.CopilotSession;

/// <summary>
/// GitHub Copilot CLI session-state 直读适配器（I-008）：目录发现（<see cref="CopilotLogLocator"/>，
/// COPILOT_HOME → ~/.copilot 的 session-state/&lt;session&gt;/events.jsonl，仅一级会话目录）→
/// 共享增量扫描引擎（<see cref="IncrementalJsonlScanner"/>，文件 mtime/大小记账，变更文件整文件重解析）→
/// 逐文件解析 + 相邻快照差分（<see cref="CopilotSessionParser"/>，累计 shutdown 快照 → 区间用量，
/// resume 不重复出账）。差分状态只在单文件解析内存活（(session, model) 分组键含会话目录名，
/// 一个根目录内会话目录唯一，组不跨文件），无跨扫描差分状态。
/// 无消息级去重标识（MessageId/RequestId 为 null），共享去重器直通；去重已在文件内按蓝本
/// dedup key 完成（DuplicateRecords 恒 0，与 DSH 适配器同口径）。
/// OpenTelemetry（otel/*.jsonl、COPILOT_OTEL_FILE_EXPORTER_PATH）与 totalNanoAiu 积分对账
/// 属 I-008 进阶范围，本类未覆盖（卡片 Development Log 实现边界）。
/// 实例持有扫描状态，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class CopilotSessionAdapter : ICopilotSessionAdapter
{
    private readonly string? _configuredHome;
    private readonly CopilotLogLocator _locator;
    private readonly CopilotSessionParser _fileParser;
    private readonly IncrementalJsonlScanner _scanner;

    public CopilotSessionAdapter(
        string? configuredHome = null,
        CopilotLogLocator? locator = null,
        CopilotSessionParser? fileParser = null,
        ClaudeUsageRecordDeduper? deduper = null)
    {
        _configuredHome = configuredHome;
        _locator = locator ?? new CopilotLogLocator();
        _fileParser = fileParser ?? new CopilotSessionParser();
        _scanner = new IncrementalJsonlScanner(readerFactory: null, deduper);
    }

    public async Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var home = _locator.ResolveHome(_configuredHome);
        var files = _locator.CollectSessionStateFiles(_configuredHome);

        var output = await _scanner.ScanAsync(
            files,
            (path, reader, token) => _fileParser.ParseAsync(reader, path, token),
            ct).ConfigureAwait(false);

        return new JsonlScanResult
        {
            Roots = home is null ? [] : [home],
            Records = output.Records,
            Diagnostics = output.Diagnostics,
        };
    }
}
