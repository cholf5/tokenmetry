using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.Droid;

/// <summary>
/// Droid (Factory) 直读适配器（I-015，蓝本 ../ccusage/rust/adapters/droid/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="DroidLogLocator"/>，DROID_SESSIONS_DIR → ~/.factory/sessions 树下
/// *.settings.json，路径排序）→ 文件戳探针（<see cref="FileStampMemo"/>，路径 + mtime + 大小，
/// 只 stat 不读内容）→ 账面未动复用上轮记录（零读取零解析）→ 否则逐文件解析
/// （<see cref="DroidSettingsParser"/>，每文件至多一条快照记录）→ 按时间戳稳定排序后<b>倒序最新者胜</b>
/// 去重（蓝本 rev + seen_sessions：同 session id 只留时间戳最新的快照，相等时间戳取文件序靠后者）→
/// 记录按时间戳升序输出（蓝本产物为最新者优先的降序，桌面统一升序，聚合等价，见卡片偏差④）。
/// 不走共享 IncrementalJsonlScanner（单层 JSON 非 JSONL，去重是会话级 latest-wins 而非消息级），
/// 以 I-042 文件戳记忆化替代逐文件增量（与共享扫描器的零变更缓存同口径）。记忆化准入：
/// 全部发现文件都成功读取才落账，有文件打不开的轮次清空记忆下一轮重试（见 <see cref="FileStampMemo"/>）。
/// 实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class DroidAdapter : IDroidAdapter
{
    private readonly string? _configuredDataDir;
    private readonly DroidLogLocator _locator;
    private readonly DroidSettingsParser _parser;
    private readonly FileStampMemo _memo = new();

    public DroidAdapter(
        string? configuredDataDir = null,
        DroidLogLocator? locator = null,
        DroidSettingsParser? parser = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new DroidLogLocator();
        _parser = parser ?? new DroidSettingsParser();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var roots = _locator.ResolveDataDirectories(_configuredDataDir);
        var files = _locator.CollectSettingsFiles(_configuredDataDir);
        var stamps = FileStampMemo.Capture(files);

        if (_memo.TryReuse(stamps, files.Count, roots, out var reused))
        {
            return Task.FromResult(reused);
        }

        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = files.Count,
        };
        var snapshots = new List<UsageRecord>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = _parser.ParseSettingsFile(file);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.InvalidRecords += outcome.InvalidRecords;
            snapshots.AddRange(outcome.Records);
        }

        // 蓝本 latest-wins 去重：稳定排序后倒序遍历，同 session id 只留第一条（= 时间戳最新，
        // 相等时间戳取文件序靠后者）；被跳过的旧快照计 DuplicateRecords。
        // 倒序收集后反转即时间戳升序（蓝本产物为最新者优先的降序，桌面统一升序，偏差④）。
        var sorted = snapshots.OrderBy(static record => record.Timestamp).ToList();
        var seenSessions = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<UsageRecord>();
        for (var index = sorted.Count - 1; index >= 0; index--)
        {
            if (!seenSessions.Add(sorted[index].SessionId))
            {
                diagnostics.DuplicateRecords++;
                continue;
            }

            records.Add(sorted[index]);
        }

        records.Reverse();
        diagnostics.MissingCostRecords = records.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = records.Count(static record => record.Model is null);
        diagnostics.Status = records.Count == 0
            ? JsonlScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        // FilesChanged：走到全量读取即本轮账面有变化（有文件填 1）；无文件不设值，
        // 由管道按"本轮发现即空"规则处理（与 ZCode 探针同口径）。
        diagnostics.FilesChanged = files.Count > 0 ? 1 : null;

        // 记忆化准入：全部发现文件都成功读取才落账（有文件打不开 → null 戳清空记忆，下轮重试）。
        _memo.Update(diagnostics.FilesRead == files.Count ? stamps : null, records, diagnostics);

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = records,
            Diagnostics = diagnostics,
        });
    }
}
