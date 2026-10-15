using System.Globalization;
using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Tokenmetry.Infrastructure.Jsonl;

namespace Tokenmetry.Infrastructure.OpenClaw;

/// <summary>
/// OpenClaw 双格式直读适配器（I-020，蓝本 ../ccusage/rust/adapters/openclaw/src/loader.rs 移植主线；
/// 蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：目录发现（<see cref="OpenClawLogLocator"/>，
/// configuredDataDir / OPENCLAW_DIR → 主目录四默认根）→ 每根先读 agents/*/agent/openclaw-agent.sqlite
/// （<see cref="OpenClawSqliteReader"/>，transcript_events 全量重读）再收集会话 JSONL（整根递归、
/// 根内排序）→ JSONL 走共享增量扫描引擎（<see cref="IncrementalJsonlScanner"/>，文件 mtime/大小记账，
/// 逐文件解析由 <see cref="OpenClawLogParser"/> 承担）→ 内容键 first-wins 去重（entry_id，蓝本
/// parser.rs entry_id = migration_id + 成本）→ <b>SQLite 优先合并</b>（蓝本 loader.rs load_entries_inner
/// 逐条对齐）：SQLite 事件先按 "openclaw:&lt;session&gt;:&lt;event id&gt;" 去重，再在 JSONL 记录中按
/// ①会话 + 事件 id、②迁移身份（migration_id：会话 + 时间戳 + 模型 + 四桶 + extra，<b>不含成本</b>——
/// SQLite 行带修正后的 provider 结算成本，成本差异不得阻断替换）匹配替换，均未命中才追加；JSONL 侧
/// 增量扫描状态由引擎记账（SQLite 侧无状态每次全量），实例非线程安全，以单例接入 App DI
/// （并发由 LocalIngestionDataSource 串行化）。记录按时间戳稳定排序（蓝本 entries.sort_by_key）。
/// </summary>
public class OpenClawAdapter : IOpenClawAdapter
{
    private readonly string? _configuredDataDir;
    private readonly OpenClawLogLocator _locator;
    private readonly OpenClawLogParser _parser;
    private readonly IncrementalJsonlScanner _scanner;

    public OpenClawAdapter(
        string? configuredDataDir = null,
        OpenClawLogLocator? locator = null,
        OpenClawLogParser? parser = null,
        Func<string, TextReader>? readerFactory = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new OpenClawLogLocator();
        _parser = parser ?? new OpenClawLogParser();
        _scanner = new IncrementalJsonlScanner(readerFactory, dedupe: ContentDedupe);
    }

    public async Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var roots = _locator.ResolveDataDirectories(_configuredDataDir);
        var files = new List<string>();
        var sqliteRecords = new List<UsageRecord>();
        var diagnostics = new JsonlScanDiagnostics();
        var problems = false;

        foreach (var root in roots)
        {
            // 蓝本根循环次序：先 SQLite（collect_sqlite_entries），后 JSONL（collect_session_files）。
            foreach (var database in OpenClawSqliteReader.CollectAgentDatabases(root))
            {
                diagnostics.FilesDiscovered++;
                var read = OpenClawSqliteReader.ReadAgentDatabase(database, ct);
                if (read.Opened)
                {
                    diagnostics.FilesRead++;
                }
                else
                {
                    // 蓝本 debug 后返回空列表；桌面诊断以 Partial 显式化（Kilo 同款登记）。
                    problems = true;
                }

                diagnostics.MalformedLines += read.MalformedRows;
                diagnostics.InvalidRecords += read.InvalidRows;
                diagnostics.LinesSkipped += read.SkippedRows;
                sqliteRecords.AddRange(read.Records);
            }

            files.AddRange(_locator.CollectSessionFiles(root));
        }

        var output = await _scanner.ScanAsync(files, _parser.ParseFileAsync, ct);
        diagnostics.FilesDiscovered += output.Diagnostics.FilesDiscovered;
        diagnostics.FilesRead += output.Diagnostics.FilesRead;
        diagnostics.LinesSkipped += output.Diagnostics.LinesSkipped;
        diagnostics.MalformedLines += output.Diagnostics.MalformedLines;
        diagnostics.InvalidRecords += output.Diagnostics.InvalidRecords;
        diagnostics.DuplicateRecords += output.Diagnostics.DuplicateRecords;

        var entries = MergeSqliteEntries(output.Records, sqliteRecords, diagnostics);
        // 蓝本 entries.sort_by_key(timestamp)：稳定排序，同刻保持发现/合并序。
        var records = entries.OrderBy(static record => record.Timestamp).ToList();

        diagnostics.MissingCostRecords = records.Count(static record => record.CostUsd is null);
        diagnostics.UnknownModelRecords = 0; // 模型恒有（缺省 "unknown"，带 [openclaw] 前缀）。
        diagnostics.Status = records.Count == 0
            ? JsonlScanStatus.NoData
            : problems || diagnostics.MalformedLines > 0 || diagnostics.InvalidRecords > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;

        return new JsonlScanResult
        {
            Roots = roots,
            Records = records,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// SQLite 优先合并（蓝本 load_entries_inner 收尾循环逐条对齐）：SQLite 事件 id 去重 →
    /// ①会话 + 事件 id 替换、②迁移身份替换（无事件 id 的迁移 JSONL 副本共享会话/时间戳/模型/用量，
    /// 成本可不同）、③追加；无事件 id 的 SQLite 行按内容键 first-wins。
    /// </summary>
    private static List<UsageRecord> MergeSqliteEntries(
        IReadOnlyList<UsageRecord> jsonlRecords,
        List<UsageRecord> sqliteRecords,
        JsonlScanDiagnostics diagnostics)
    {
        var seen = new HashSet<string>(jsonlRecords.Select(EntryId), StringComparer.Ordinal);
        var entries = new List<UsageRecord>(jsonlRecords);
        var sqliteIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in sqliteRecords)
        {
            if (entry.MessageId is not { } messageId)
            {
                if (seen.Add(EntryId(entry)))
                {
                    entries.Add(entry);
                }
                else
                {
                    diagnostics.DuplicateRecords++;
                }

                continue;
            }

            var sqliteId = $"openclaw:{entry.SessionId}:{messageId}";
            if (!sqliteIds.Add(sqliteId))
            {
                diagnostics.DuplicateRecords++;
                continue;
            }

            // ①会话 + 事件 id 精确替换（JSONL 侧记录无消息 id，当前恒不命中，保真保留）。
            var position = entries.FindIndex(existing =>
                existing.SessionId == entry.SessionId && existing.MessageId == messageId);
            if (position < 0)
            {
                // ②迁移身份替换：成本不参与（SQLite 行携带修正后的 provider 结算成本）。
                var migrationKey = MigrationId(entry);
                position = entries.FindIndex(existing => MigrationId(existing) == migrationKey);
            }

            if (position >= 0)
            {
                entries[position] = entry;
                diagnostics.DuplicateRecords++;
            }
            else
            {
                seen.Add(EntryId(entry));
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// JSONL 内容键 first-wins（蓝本 loader seen.insert(entry_id)：migration_id + 成本；
    /// 发现序首条胜出、保持顺序）。
    /// </summary>
    internal static IReadOnlyList<UsageRecord> ContentDedupe(IEnumerable<UsageRecord> records)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UsageRecord>();
        foreach (var record in records)
        {
            if (seen.Add(EntryId(record)))
            {
                result.Add(record);
            }
        }

        return result;
    }

    /// <summary>内容键（蓝本 entry_id = format!("{}:{}", migration_id, cost)）。</summary>
    internal static string EntryId(UsageRecord entry)
        => $"{MigrationId(entry)}:{entry.CostUsd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}";

    /// <summary>
    /// 迁移身份键（蓝本 migration_id）：遗留 JSONL 事件与其迁移 SQLite 副本共享的身份——
    /// 会话 + 时间戳（毫秒时刻，蓝本为 RFC3339 毫秒文本，同刻同键）+ 模型 + 四桶 + extra（并入
    /// OutputTokens，见卡片偏差③）+ <b>不含成本</b>。
    /// </summary>
    internal static string MigrationId(UsageRecord entry)
        => string.Join(":",
            "openclaw",
            entry.SessionId,
            entry.Timestamp.ToUnixTimeMilliseconds(),
            entry.Model ?? string.Empty,
            entry.InputTokens,
            entry.OutputTokens,
            entry.CacheCreationTokens,
            entry.CacheReadTokens);
}
