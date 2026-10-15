using Tokenmetry.Core.Abstractions;
using Tokenmetry.Core.Models;
using Microsoft.Data.Sqlite;

namespace Tokenmetry.Infrastructure.Antigravity;

/// <summary>
/// Antigravity 本地会话 SQLite 直读适配器（I-021，移植蓝本 ../ccusage/rust/adapters/antigravity/src/loader.rs
/// 主线；蓝本许可：MIT License，Copyright (c) 2025 ryoppippi）：库发现（<see cref="AntigravityLogLocator"/>，
/// ANTIGRAVITY_DATA_DIR 多根 / 六默认根）→ 逐库只读解析（<see cref="AntigravityParser.ParseSqliteFile"/>，
/// gen_metadata 必需表 + steps / trajectory_metadata_blob 可选表）→ 身份键跨库去重合并
/// （<see cref="AntigravityParser.DeduplicateEvents"/>，蓝本 loader.rs deduplicate_events 同构）→
/// 按时间戳稳定排序（蓝本 events.sort_by_key(timestamp)）→ 统一记录（CostUsd 恒 null——会话库无内建
/// 成本，交定价引擎；模型缺省 "gemini-internal-model"）。数据随使用持续变化，mtime 增量不适用，
/// 每次全量重读；只读打开兼容 Antigravity 运行中的 WAL。
/// 诊断口径沿用 <see cref="JsonlScanDiagnostics"/>（仓库 I-004 显式化）：FilesDiscovered = 发现的 .db 数、
/// FilesRead = 打开并完成检查的库数（含空快照跳过库）、LinesSkipped = 非 token-bearing usage 计数
/// （蓝本静默丢弃）、MalformedLines = protobuf blob 解析失败的库数（蓝本整次加载失败，此处按库显式
/// 计数 + Partial——fail-loud 程度差异见卡片偏差清单）、库打开/查询失败仅记问题标记（Kilo/Goose 同款），
/// DuplicateRecords = 身份键合并折叠的事件数。实例无扫描状态，线程安全；以单例接入 App DI
/// （并发由 LocalIngestionDataSource 串行化）。
/// </summary>
public class AntigravityAdapter : IAntigravityAdapter
{
    private readonly string? _configuredDataDir;
    private readonly AntigravityLogLocator _locator;

    public AntigravityAdapter(string? configuredDataDir = null, AntigravityLogLocator? locator = null)
    {
        _configuredDataDir = configuredDataDir;
        _locator = locator ?? new AntigravityLogLocator();
    }

    public Task<JsonlScanResult> ScanAsync(CancellationToken ct = default)
    {
        var databases = _locator.CollectConversationDatabasePaths(_configuredDataDir);
        var diagnostics = new JsonlScanDiagnostics
        {
            FilesDiscovered = databases.Count,
        };
        var events = new List<AntigravityParser.UsageEvent>();
        var problems = false;

        foreach (var database in databases)
        {
            ct.ThrowIfCancellationRequested();
            AntigravityParser.ParseOutcome? outcome;
            try
            {
                outcome = AntigravityParser.ParseSqliteFile(database);
            }
            catch (AntigravityProtoReader.ProtoDecodeException)
            {
                // 蓝本 cli_error 整次加载失败；桌面按库显式计数 + Partial（偏差清单⑤）。
                problems = true;
                diagnostics.MalformedLines++;
                continue;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or InvalidCastException)
            {
                // 打开 / 查询 / 列类型失败：该库本轮 0 条 + 问题标记（Kilo/Goose 同款降级口径）。
                problems = true;
                continue;
            }

            diagnostics.FilesRead++;
            if (outcome is null)
            {
                // 蓝本 debug_log：Skipping uninitialized Antigravity database（空快照不算问题）。
                continue;
            }

            diagnostics.LinesSkipped += outcome.NonTokenBearingUsages;
            events.AddRange(outcome.Events);
        }

        var deduped = AntigravityParser.DeduplicateEvents(events);
        diagnostics.DuplicateRecords += events.Count - deduped.Count;

        // 蓝本 events.sort_by_key(timestamp)：稳定排序，同刻保持发现/合并序。
        var records = deduped
            .OrderBy(static @event => @event.Timestamp)
            .Select(ToUsageRecord)
            .ToList();

        diagnostics.MissingCostRecords = records.Count; // 会话库无内建成本，全部交定价引擎。
        diagnostics.UnknownModelRecords = 0; // 模型恒有（缺省 "gemini-internal-model"）。
        diagnostics.Status = records.Count == 0
            ? JsonlScanStatus.NoData
            : problems || diagnostics.MalformedLines > 0
                ? JsonlScanStatus.Partial
                : JsonlScanStatus.Ok;

        return Task.FromResult(new JsonlScanResult
        {
            Roots = databases,
            Records = records,
            Diagnostics = diagnostics,
        });
    }

    /// <summary>
    /// 统一记录映射（蓝本 event_to_loaded 的桌面等价：成本交引擎、reasoning 不落记录）。
    /// I-024 接线 AG-1：计价走蓝本 model_candidates 候选链（effort 变体 → 基础名 → google 前缀组，
    /// <see cref="AntigravityParser.BuildModelCandidates"/>）；flash 变体按蓝本 exact_lookup 走逐候选
    /// 精确（ExactOnlyAcrossCandidates），其余逐候选完整匹配；计价 output 分量取蓝本 cost_usage 口径
    /// total_output（含 reasoning，<see cref="AntigravityParser.UsageEvent.TotalOutputTokens"/>）——
    /// 统一记录 OutputTokens 为 visible 口径，经 OutputTokenOverride 补差，不影响 token 展示/落库。
    /// </summary>
    private static UsageRecord ToUsageRecord(AntigravityParser.UsageEvent @event)
    {
        return new UsageRecord
        {
            Timestamp = ToTimestamp(@event.Timestamp),
            Agent = "Antigravity",
            ProjectPath = "Antigravity",
            SessionId = @event.SessionId,
            Model = @event.Model,
            Pricing = new UsagePricingPlan(
                AntigravityParser.BuildModelCandidates(@event.Model, @event.Provider),
                AntigravityParser.GeminiFlashPricingAlias(@event.Model) is not null
                    ? PricingCandidateWalk.ExactOnlyAcrossCandidates
                    : PricingCandidateWalk.PerCandidateFull,
                OutputTokenOverride: @event.TotalOutputTokens),
            CostUsd = null,
            InputTokens = @event.InputTokens,
            OutputTokens = @event.OutputTokens,
            CacheCreationTokens = @event.CacheCreationTokens,
            CacheReadTokens = @event.CacheReadTokens,
            MessageId = @event.MessageId,
            SourceFile = @event.SourceFile,
        };
    }

    /// <summary>
    /// 毫秒 → 时刻（蓝本 TimestampMs 裸 i64；DateTimeOffset 表示域外的值按"时间不可用"归纪元——
    /// I-020 偏差⑧同款已登记差异）。
    /// </summary>
    private static DateTimeOffset ToTimestamp(long milliseconds)
    {
        const long minMilliseconds = -62_135_596_800_000;
        const long maxMilliseconds = 253_402_300_799_999;
        return milliseconds switch
        {
            < minMilliseconds or > maxMilliseconds => DateTimeOffset.UnixEpoch,
            _ => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
        };
    }
}
