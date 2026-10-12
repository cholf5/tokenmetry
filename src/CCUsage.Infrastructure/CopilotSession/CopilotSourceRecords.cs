using CCUsage.Core.Models;

namespace CCUsage.Infrastructure.CopilotSession;

/// <summary>
/// Copilot 适配器内部中间记录（蓝本 ../ccusage/rust/adapters/copilot/src/parser.rs 的
/// CopilotUsageEntry / CopilotCreditSnapshot 的桌面等价物，I-023）。仅适配器内部流转，
/// 不出统一记录接口——统一记录（<see cref="UsageRecord"/>）无请求数 / nanoAiu / reasoning /
/// extra 分量字段，合并逻辑（<see cref="CopilotUsageReconciler"/>）依赖它们。
/// </summary>

/// <summary>
/// 一条 (session, model) 的 session-state 累计快照（inputTokens 已拆出缓存读写；
/// I-008 CumulativeSnapshot 扩展 nanoAiu 与来源文件）。
/// </summary>
public sealed record CopilotSnapshotEntry(
    string SessionId,
    string Model,
    DateTimeOffset Timestamp,
    string TimestampText,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long RequestCount,
    long NanoAiu,
    string DedupKey,
    string SourceFile);

/// <summary>
/// 会话级积分快照（session.shutdown / session.usage_checkpoint 的 totalNanoAiu，1 AIU = $0.01）。
/// <see cref="TotalNanoAiu"/> 为 null 表示 shutdown 缺会话总额或模型侧未全部上报积分
/// （蓝本 marks_shutdown_credits_unknown_when_a_total_is_missing——无法对账，不产出缺口）。
/// </summary>
public sealed record CopilotCreditSnapshot(
    DateTimeOffset Timestamp,
    string SessionId,
    bool IsShutdown,
    long? TotalNanoAiu,
    string DedupKey,
    string SourceFile);

/// <summary>
/// 一条 OpenTelemetry 用量行（四形态判别 + 互斥去重后的候选，蓝本 CopilotUsageEntry 的 otel 来源）。
/// <see cref="ReasoningOutputTokens"/> / <see cref="ExtraTotalTokens"/> 仅供重复 chat span 的
/// 身份比较与单测对齐蓝本；统一记录不落（桌面无对应字段，extra 分量丢弃——卡片偏差清单）。
/// </summary>
public sealed record CopilotOtelEntry(
    DateTimeOffset Timestamp,
    string SessionId,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long ReasoningOutputTokens,
    long ExtraTotalTokens,
    string DedupKey,
    string SourceFile);

/// <summary>单文件解析产出的容错计数（口径与共享 <see cref="JsonlFileParseOutcome"/> 一致）。</summary>
public sealed record CopilotFileParseCounters(
    int FilesRead,
    int LinesSkipped,
    int MalformedLines,
    int InvalidRecords);

/// <summary>Copilot 单文件解析产出的公共形态（适配器增量记账按种类分流缓存）。</summary>
public interface ICopilotFileParse
{
    CopilotFileParseCounters Counters { get; }
}

/// <summary>一个 session-state events.jsonl 的解析产出：累计快照 + 积分快照（差分在合并器）。</summary>
public sealed record CopilotSessionFileParse(
    IReadOnlyList<CopilotSnapshotEntry> Snapshots,
    IReadOnlyList<CopilotCreditSnapshot> CreditSnapshots,
    CopilotFileParseCounters Counters) : ICopilotFileParse;

/// <summary>一个 otel jsonl 的解析产出：逐调用用量行（行内四形态互斥去重已完成）。</summary>
public sealed record CopilotOtelFileParse(
    IReadOnlyList<CopilotOtelEntry> Entries,
    CopilotFileParseCounters Counters) : ICopilotFileParse;
