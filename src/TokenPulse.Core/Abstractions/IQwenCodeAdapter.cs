using TokenPulse.Core.Models;

namespace TokenPulse.Core.Abstractions;

/// <summary>
/// Qwen Code 本地数据直读适配器（I-011）。
/// 解析规则移植自 ../ccusage/rust/adapters/qwen/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IQwenCodeAdapter
{
    /// <summary>
    /// 扫描 Qwen Code 会话数据（QWEN_DATA_DIR → ~/.qwen 的 projects/&lt;project&gt;/chats/*.jsonl
    /// 聊天文件），产出 agent=Qwen Code 的统一 usage 记录与诊断计数。
    /// 只有含 "usageMetadata" 的行进入解析；type 恰为 "assistant" 且 usage 块存在才产出；
    /// token 口径 input←promptTokenCount、output←candidatesTokenCount、cache_read←
    /// cachedContentTokenCount，thoughts 作 extra、totalTokenCount 兜底（output 为 0 补 output、
    /// 否则缺口并入 extra；extra 并入统一记录 OutputTokens）。行内 timestamp 严格 RFC3339 可解析
    /// 才采用，否则回退文件 mtime（mtime 不可得回退当前时间）。sessionId 缺失回退
    /// &lt;project&gt;-&lt;文件名主干&gt;，模型缺失为 "unknown"。记录按内容键（会话/时间戳/模型/token）
    /// 跨文件 first-wins 去重后按时间戳稳定排序；增量扫描（文件 mtime/大小记账）。
    /// CostUsd 恒 null 交定价引擎。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
