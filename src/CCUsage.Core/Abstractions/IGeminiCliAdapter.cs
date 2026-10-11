using CCUsage.Core.Models;

namespace CCUsage.Core.Abstractions;

/// <summary>
/// Gemini CLI 本地数据直读适配器（I-010）。
/// 解析规则移植自 ../ccusage/rust/adapters/gemini/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IGeminiCliAdapter
{
    /// <summary>
    /// 扫描 Gemini CLI 会话数据（GEMINI_DATA_DIR → ~/.gemini/tmp 的 .json/.jsonl chats 文件），
    /// 产出 agent=Gemini CLI 的统一 usage 记录与诊断计数。
    /// .json（整文件）三分支：messages 数组只取 type=="gemini"（原样比较）的消息、顶层 type=="gemini"
    /// 直接事件、stats（models 明细优先、无产出回退整块解析且模型缺省 unknown）；.jsonl 逐行解析、
    /// sessionId/model 跨行滚动、事件 id 就地替换。token 块接受多键别名与浮点截断；直接事件按
    /// total 自洽性判别 cached 是否已含于 input（命中则拆出 cache_read），stats 事件恒
    /// input−min(input,cached)；total 兜底：output 为 0 补 output、否则缺口并入 reasoning
    /// （reasoning/total 余量并入统一记录 OutputTokens，蓝本 cost_usage 同口径）。
    /// CostUsd 恒 null 交定价引擎；每次全量重读（不走共享 JSONL 增量引擎，见适配器注释）。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
