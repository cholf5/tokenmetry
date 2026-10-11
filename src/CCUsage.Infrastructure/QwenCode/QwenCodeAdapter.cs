using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.QwenCode;

/// <summary>
/// Qwen Code JSONL 直读适配器（I-011，蓝本 ../ccusage/rust/adapters/qwen/src/parser.rs load_entries
/// 移植主线；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="QwenLogLocator"/>，QWEN_DATA_DIR → ~/.qwen 下 projects/**/chats/*.jsonl）→
/// 共享增量扫描引擎（<see cref="IncrementalJsonlScanner"/>，文件 mtime/大小记账，变更文件整文件重解析）→
/// 内容键 first-wins 去重（<see cref="QwenUsageRecordDeduper"/>，蓝本 entry_id 同口径）→
/// 记录按时间戳稳定排序（蓝本 entries.sort_by_key(timestamp)）→ 统一记录 + 诊断计数。
/// 日志布局与 Claude Code projects/ 同构，故复用 I-001 扫描引擎与解析骨架；差异处（内容键去重、
/// projects/chats 双段布局过滤）单独实现，见卡片 Dev Log。
/// 实例持有扫描状态，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class QwenCodeAdapter : IQwenCodeAdapter
{
    private readonly string? _configuredDataDir;
    private readonly QwenLogLocator _locator;
    private readonly QwenLogParser _parser;
    private readonly IncrementalJsonlScanner _scanner;

    public QwenCodeAdapter(
        string? configuredDataDir = null,
        QwenLogLocator? locator = null,
        QwenLogParser? parser = null,
        QwenUsageRecordDeduper? deduper = null,
        Func<string, TextReader>? readerFactory = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new QwenLogLocator();
        _parser = parser ?? new QwenLogParser();
        _scanner = new IncrementalJsonlScanner(
            readerFactory,
            dedupe: (deduper ?? new QwenUsageRecordDeduper()).Dedupe);
    }

    public async Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var roots = _locator.ResolveDataDirectories(_configuredDataDir);
        var files = _locator.CollectChatFiles(roots);
        var output = await _scanner.ScanAsync(files, _parser.ParseFileAsync, ct);

        // 蓝本 entries.sort_by_key(timestamp)：稳定排序，同刻保持文件/行序。
        return new JsonlScanResult
        {
            Roots = roots,
            Records = output.Records.OrderBy(static record => record.Timestamp).ToList(),
            Diagnostics = output.Diagnostics,
        };
    }
}
