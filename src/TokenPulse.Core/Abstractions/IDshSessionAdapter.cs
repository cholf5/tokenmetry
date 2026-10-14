using TokenPulse.Core.Models;

namespace TokenPulse.Core.Abstractions;

/// <summary>
/// DSH（DeepSeek Harness）本地会话日志直读适配器（I-007）。
/// 解析与 fold 语义移植自 ../dsh-usage-stats（lib/usage.js / scripts/verify-raw.mjs，MIT）。
/// </summary>
public interface IDshSessionAdapter
{
    /// <summary>
    /// 扫描 DSH 会话日志（DSH_HOME → ~/.dsh 的 sessions/ 下，每个会话目录只读最高版本的
    /// session.v*.jsonl(.zstd) / session.jsonl），产出 agent=DSH 的统一 usage 记录与诊断计数。
    /// zstd 文件逐帧独立解压（每帧独立可解压，容忍尾部 torn frame）；usage 样本按
    /// (turn, step) 邻近替换语义折叠（同键重复样本替换不累加，重归属到后样本事件日期）。
    /// 增量由共享扫描引擎的文件 mtime/大小记账承担：变更文件整文件重解析（fold 状态只在
    /// 单次文件解析内存活），与 ZCode 适配器的"无跨扫描 fold 状态"同等安全。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
