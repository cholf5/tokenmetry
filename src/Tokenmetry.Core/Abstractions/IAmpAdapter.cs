using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// Amp 本地数据直读适配器（I-014）。
/// 解析规则移植自 ../ccusage/rust/adapters/amp/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IAmpAdapter
{
    /// <summary>
    /// 扫描 Amp 线程数据（AMP_DATA_DIR → ~/.local/share/amp 的 threads/T-*.json，单层 JSON 非 JSONL），
    /// 产出 agent=Amp 的统一 usage 记录与诊断计数。
    /// usageLedger.events 优先（存在即走账本路径，含空数组不回退）：timestamp/model/tokens 四关 +
    /// toMessageId 查 messages 内 assistant 消息的 cacheCreation/cacheReadInputTokens attach；
    /// 账本缺失/非对象回退 messages 用量路径（仅 role=="assistant"，usage.timestamp/model 缺省回退
    /// 消息级字段）；total 兜底（output 为 0 补 output、否则缺口并入 reasoning，reasoning/total 余量
    /// 并入统一记录 OutputTokens，蓝本 cost_usage 同口径）。CostUsd 恒 null 交定价引擎；
    /// 每次全量重读。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
