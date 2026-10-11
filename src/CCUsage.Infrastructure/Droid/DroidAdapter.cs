using CCUsage.Core.Abstractions;
using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.Droid;

/// <summary>
/// Droid (Factory) 直读适配器（I-015，蓝本 ../ccusage/rust/adapters/droid/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="DroidLogLocator"/>，DROID_SESSIONS_DIR → ~/.factory/sessions 树下
/// *.settings.json，路径排序）→ 逐文件解析（<see cref="DroidSettingsParser"/>，每文件至多一条快照记录）→
/// 按时间戳稳定排序后<b>倒序最新者胜</b>去重（蓝本 rev + seen_sessions：同 session id 只留时间戳最新的
/// 快照，相等时间戳取文件序靠后者）→ 记录按时间戳升序输出（蓝本产物为最新者优先的降序，桌面统一
/// 升序，聚合等价，见卡片偏差④）。
/// 不走共享 IncrementalJsonlScanner（单层 JSON 非 JSONL，去重是会话级 latest-wins 而非消息级），每次
/// 全量重读（I-010 同款判断）。实例无扫描状态，线程安全；以单例接入 App DI。
/// </summary>
public class DroidAdapter : IDroidAdapter
{
    private readonly string? _configuredDataDir;
    private readonly DroidLogLocator _locator;
    private readonly DroidSettingsParser _parser;

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

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = records,
            Diagnostics = diagnostics,
        });
    }
}
