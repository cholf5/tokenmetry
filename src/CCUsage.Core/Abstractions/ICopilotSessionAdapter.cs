using CCUsage.Core.Models;

namespace CCUsage.Core.Abstractions;

/// <summary>
/// GitHub Copilot CLI 本地数据直读适配器（I-008）。
/// 解析与差分语义移植自 ../ccusage rust/adapters/copilot（parser.rs / loader.rs）。
/// </summary>
public interface ICopilotSessionAdapter
{
    /// <summary>
    /// 扫描 Copilot CLI 的 session-state 数据（COPILOT_HOME → ~/.copilot 的
    /// session-state/&lt;session&gt;/events.jsonl，仅一级会话目录），产出 agent=Copilot CLI 的
    /// 统一 usage 记录与诊断计数。
    /// session.shutdown 的 modelMetrics 是按 (session, model) 的累计快照（resume 会重复出账）：
    /// 相邻快照差分为区间用量（首条保留、后条减前条，saturating），resume 场景总量守恒、
    /// 区间归属到后一条快照的日期。uncached input = inputTokens − (cacheRead + cacheWrite)
    /// （Copilot 把缓存读写计入 inputTokens）；reasoning 已含在 output 内不单列；
    /// 内部模型后缀（-1m / -1m-internal）剥离后再参与分组与定价。
    /// 增量由共享扫描引擎的文件 mtime/大小记账承担（变更文件整文件重解析，差分状态只在
    /// 单文件解析内存活——(session, model) 分组键含会话目录名，不会跨文件）。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
