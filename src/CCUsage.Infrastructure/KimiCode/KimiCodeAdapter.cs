using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;
using CCUsage.Infrastructure.Jsonl;

namespace CCUsage.Infrastructure.KimiCode;

/// <summary>
/// Kimi Code JSONL 直读适配器（I-013，蓝本 ../ccusage/rust/adapters/kimi/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="KimiLogLocator"/>，KIMI_DATA_DIR → ~/.kimi 与 ~/.kimi-code 双默认下
/// sessions/**/wire.jsonl）→ 共享增量扫描引擎（<see cref="IncrementalJsonlScanner"/>，文件
/// mtime/大小记账，变更文件整文件重解析）→ 内容键 first-wins 去重（<see cref="KimiUsageRecordDeduper"/>，
/// 蓝本 kimi_entry_key 同口径）→ 记录按时间戳稳定排序（蓝本 entries.sort_by_key(timestamp)）→
/// 统一记录 + 诊断计数。
/// wire.jsonl 为行式 JSONL，走共享 JSONL 扫描引擎；新旧双格式解析与 config.json 模型回退由
/// <see cref="KimiLogParser"/> 承担（蓝本语义逐条对齐，差异处见卡片 Dev Log）。
/// 实例持有扫描状态，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class KimiCodeAdapter : IKimiCodeAdapter
{
    private readonly string? _configuredDataDir;
    private readonly KimiLogLocator _locator;
    private readonly KimiLogParser _parser;
    private readonly IncrementalJsonlScanner _scanner;

    public KimiCodeAdapter(
        string? configuredDataDir = null,
        KimiLogLocator? locator = null,
        KimiLogParser? parser = null,
        KimiUsageRecordDeduper? deduper = null,
        Func<string, TextReader>? readerFactory = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new KimiLogLocator();
        _parser = parser ?? new KimiLogParser();
        _scanner = new IncrementalJsonlScanner(
            readerFactory,
            dedupe: (deduper ?? new KimiUsageRecordDeduper()).Dedupe);
    }

    public async Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var roots = _locator.ResolveDataDirectories(_configuredDataDir);
        var files = _locator.CollectWireFiles(roots);
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
