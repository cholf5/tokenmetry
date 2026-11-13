using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.JsonSource;

namespace Tokenmetry.Infrastructure.Amp;

/// <summary>
/// Amp 直读适配器（I-014，蓝本 ../ccusage/rust/adapters/amp/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：
/// 目录发现（<see cref="AmpLogLocator"/>，AMP_DATA_DIR → ~/.local/share/amp 的 threads/ 子树 .json
/// 线程文件）→ <b>逐文件</b>文件戳记账（<see cref="PerFileParseCache{T}"/>，路径 + mtime + 大小，只 stat
/// 不读内容）：戳未变的文件不重读不重解析，直接取用上轮产出；本轮零变化即复用上轮记录 → 否则逐文件解析
/// （<see cref="AmpThreadParser"/>，usageLedger.events 优先、缺失回退 messages 用量）→ 记录按时间戳稳定
/// 排序（蓝本 entries.sort_by_key(timestamp)）。
/// 不走共享 IncrementalJsonlScanner（单层 JSON 非 JSONL，且蓝本无任何消息级去重语义）。
/// 记账必须逐文件：线程文件（尤其当前会话）每回合都在长，整批戳集全等才复用的口径会让单份活跃文件
/// 把整批历史拉回全量重读。准入：打不开 / stat 失败的文件不落账，下次重试整个文件。
/// 实例持有扫描记忆，非线程安全；以单例接入 App DI（并发由采集管道信号量串行化）。
/// </summary>
public class AmpAdapter : IAmpAdapter
{
    private readonly string? _configuredDataDir;
    private readonly AmpLogLocator _locator;
    private readonly AmpThreadParser _parser;
    private readonly PerFileParseCache<AmpFileOutcome> _cache = new();

    /// <summary>按文件记「存在解析错误」：状态聚合要纳入未重解析的历史文件（与共享扫描器同款）。</summary>
    private readonly HashSet<string> _filesWithParseErrors = new(StringComparer.Ordinal);

    /// <summary>零变更轮缓存：全集合折叠的产物与上轮逐位相同，直接复用（同实例，消费方视为只读）。</summary>
    private List<UsageRecord>? _lastRecords;
    private (JsonlScanStatus Status, int Duplicates, int MissingCost, int UnknownModel)? _lastAggregate;

    public AmpAdapter(
        string? configuredDataDir = null,
        AmpLogLocator? locator = null,
        AmpThreadParser? parser = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new AmpLogLocator();
        _parser = parser ?? new AmpThreadParser();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var roots = _locator.ResolveDataDirectories(_configuredDataDir);
        var files = _locator.CollectThreadFiles(_configuredDataDir);

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

            var outcome = _parser.ParseThreadFile(file);
            diagnostics.FilesRead += outcome.FilesRead;
            diagnostics.LinesSkipped += outcome.LinesSkipped;
            diagnostics.MalformedLines += outcome.MalformedLines;
            diagnostics.InvalidRecords += outcome.InvalidRecords;

            if (probe != PerFileProbe.NeedsParse || outcome.FilesRead == 0)
            {
                // 打不开 / stat 失败：不落账，下次扫描重试整个文件（共享扫描器同口径）。
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
            // 零变更轮：复用上一次的折叠产物（同实例），本轮诊断只反映本次扫描事件。
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

        var records = new List<UsageRecord>();
        foreach (var file in files)
        {
            // 本轮打不开的文件未落账，不贡献记录（下轮重试）。
            if (_cache.TryGetStored(file, out var outcome))
            {
                records.AddRange(outcome.Records);
            }
        }

        // 稳定排序（同时间戳保持文件/行序，与蓝本 sort_by_key 稳定语义一致）。
        var sorted = records.OrderBy(static record => record.Timestamp).ToList();
        diagnostics.MissingCostRecords = sorted.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = sorted.Count(static record => record.Model is null);
        diagnostics.Status = sorted.Count == 0
            ? JsonlScanStatus.NoData
            : diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0 || _filesWithParseErrors.Count > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;
        // FilesChanged：本轮账面变化的文件数；无文件不设值，由管道按「本轮发现即空」规则处理。
        diagnostics.FilesChanged = files.Count > 0 ? filesChanged : null;

        _lastRecords = sorted;
        _lastAggregate = (diagnostics.Status, diagnostics.DuplicateRecords, diagnostics.MissingCostRecords,
            diagnostics.UnknownModelRecords);

        return Task.FromResult(new JsonlScanResult
        {
            Roots = roots,
            Records = sorted,
            Diagnostics = diagnostics,
        });
    }
}
