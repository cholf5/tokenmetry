using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.Droid;

/// <summary>
/// Droid (Factory) 直读适配器（I-015，蓝本 ../ccusage/rust/adapters/droid/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="DroidLogLocator"/>，DROID_SESSIONS_DIR → ~/.factory/sessions 树下
/// *.settings.json，路径排序）→ <b>逐文件</b>文件戳记账（<see cref="PerFileParseCache{T}"/>，路径 + mtime +
/// 大小，只 stat 不读内容）：戳未变的文件不重读不重解析，直接取用上轮产出；本轮零变化即复用上轮记录 →
/// 否则逐文件解析（<see cref="DroidSettingsParser"/>，每文件至多一条快照记录）→ 按时间戳稳定排序后
/// <b>倒序最新者胜</b>去重（蓝本 rev + seen_sessions：同 session id 只留时间戳最新的快照，相等时间戳取
/// 文件序靠后者）→ 记录按时间戳升序输出（蓝本产物为最新者优先的降序，桌面统一升序，聚合等价，见卡片偏差④）。
/// 不走共享 IncrementalJsonlScanner（单层 JSON 非 JSONL，去重是会话级 latest-wins 而非消息级）。
/// 记账必须逐文件：快照文件随会话推进持续重写，整批戳集全等才复用的口径会让单份活跃快照把整批历史
/// 拉回全量重读（旧快照也是只看戳就够）。准入：打不开 / stat 失败的文件不落账，下次重试整个文件。
/// 实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class DroidAdapter : IDroidAdapter
{
    private readonly string? _configuredDataDir;
    private readonly DroidLogLocator _locator;
    private readonly DroidSettingsParser _parser;
    private readonly PerFileParseCache<DroidFileOutcome> _cache = new();

    /// <summary>按文件记「存在解析错误」：状态聚合要纳入未重解析的历史文件。</summary>
    private readonly HashSet<string> _filesWithParseErrors = new(StringComparer.Ordinal);

    /// <summary>零变更轮缓存：全集合去重 + 排序的产物与上轮逐位相同，直接复用（同实例，消费方视为只读）。</summary>
    private List<UsageRecord>? _lastRecords;
    private (JsonlScanStatus Status, int Duplicates, int MissingCost, int UnknownModel)? _lastAggregate;

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
        // 本轮账面变化文件数（消失 + 新增/变更）。
        var filesChanged = _cache.RemoveMissing(files);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var probe = _cache.Probe(file, out var stamp, out _);
            if (probe == PerFileProbe.Reused)
            {
                // 未变更：不重读不重解析（本轮无解析事件，内容诊断不累加）。
                continue;
            }

            if (probe == PerFileProbe.StatFailed)
            {
                diagnostics.LinesSkipped++;
            }

            var outcome = _parser.ParseSettingsFile(file);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.InvalidRecords += outcome.InvalidRecords;

            if (probe != PerFileProbe.NeedsParse || outcome.FilesRead == 0)
            {
                // 打不开 / stat 失败：不落账，下次扫描重试整个文件。
                _cache.Forget(file);
                _filesWithParseErrors.Remove(file);
                continue;
            }

            _cache.Store(file, stamp, outcome);
            filesChanged++;
            if (outcome.MalformedLines > 0 || outcome.InvalidRecords > 0)
            {
                _filesWithParseErrors.Add(file);
            }
            else
            {
                _filesWithParseErrors.Remove(file);
            }
        }

        if (filesChanged == 0 && _lastRecords is { } cachedRecords && _lastAggregate is { } aggregate)
        {
            // 零变更轮：复用上一次的去重/排序产物（同实例），本轮诊断只反映本次扫描事件。
            return Task.FromResult(new JsonlScanResult
            {
                Roots = roots,
                Records = cachedRecords,
                Diagnostics = new JsonlScanDiagnostics
                {
                    Status = aggregate.Status,
                    FilesDiscovered = files.Count,
                    FilesRead = 0,
                    LinesSkipped = diagnostics.LinesSkipped,
                    MalformedLines = 0,
                    InvalidRecords = 0,
                    DuplicateRecords = aggregate.Duplicates,
                    MissingCostRecords = aggregate.MissingCost,
                    UnknownModelRecords = aggregate.UnknownModel,
                    FilesChanged = files.Count > 0 ? 0 : null,
                },
            });
        }

        var snapshots = new List<UsageRecord>();
        foreach (var file in files)
        {
            // 本轮打不开的文件未落账，不贡献记录（下轮重试）。
            if (_cache.TryGetStored(file, out var outcome))
            {
                snapshots.AddRange(outcome.Records);
            }
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
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0 || _filesWithParseErrors.Count > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        // FilesChanged：本轮账面变化的文件数；无文件不设值，由管道按「本轮发现即空」规则处理。
        diagnostics.FilesChanged = files.Count > 0 ? filesChanged : null;

        _lastRecords = records;
        _lastAggregate = (diagnostics.Status, diagnostics.DuplicateRecords, diagnostics.MissingCostRecords,
            diagnostics.UnknownModelRecords);

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = records,
            Diagnostics = diagnostics,
        });
    }
}
