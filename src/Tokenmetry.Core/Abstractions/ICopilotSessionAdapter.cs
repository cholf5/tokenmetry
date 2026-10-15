using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// GitHub Copilot CLI 本地数据直读适配器（I-008 主线 + I-023 otel 合并与积分对账）。
/// 解析与合并语义移植自 ../ccusage rust/adapters/copilot（parser.rs / loader.rs）。
/// </summary>
public interface ICopilotSessionAdapter
{
    /// <summary>
    /// 扫描 Copilot CLI 的本地数据，产出 agent=Copilot CLI 的统一 usage 记录与诊断计数：
    /// session-state/&lt;session&gt;/events.jsonl（仅一级会话目录）+ otel/ 递归 *.jsonl 与
    /// COPILOT_OTEL_FILE_EXPORTER_PATH 单文件（I-023）。
    /// session.shutdown 的 modelMetrics 是按 (session, model) 的累计快照（resume 会重复出账）：
    /// 相邻快照差分为区间用量（首条保留、后条减前条，saturating），resume 场景总量守恒、
    /// 区间归属到后一条快照的日期。uncached input = inputTokens − (cacheRead + cacheWrite)
    /// （Copilot 把缓存读写计入 inputTokens）；reasoning 已含在 output 内不单列；
    /// 内部模型后缀（-1m / -1m-internal）剥离后再参与分组与定价。
    /// OpenTelemetry 逐调用行（四种记录形态按 trace/response id 优先级互斥去重，chat span 双导出
    /// 计一次）与 shutdown 区间取优合并：otel 对某 (session, model) 的调用数多于 shutdown 请求数
    /// 差分（进程无 shutdown 结束的调用只存在于 otel）时由 otel 从第一个有行的区间起接管，
    /// 否则仅保留最新 shutdown 之后的行；比较覆盖全部 shutdown，与日期范围无关。
    /// 有 checkpoint 的会话中 totalNanoAiu 持续未被模型积分解释的部分产出 cost-only unknown 条目
    /// （cost = nanoAiu / 1e11，decimal 精确）；无 checkpoint / shutdown 缺总额 / 有 otel 行的会话
    /// 豁免。增量由文件 mtime/大小记账承担（变更文件整文件重解析，合并对当前池整体重算）。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
