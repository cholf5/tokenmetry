using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// Grok Build CLI 本地数据直读适配器（I-022）。
/// 解析规则移植自 ../ccusage/rust/adapters/grok/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IGrokAdapter
{
    /// <summary>
    /// 扫描 Grok Build CLI 会话数据（GROK_HOME → ~/.grok 的 sessions/**&#47;updates.jsonl，
    /// 目录层级 &lt;url-encoded cwd&gt;/&lt;uuid&gt;/），仅取 sessionUpdate=="turn_completed" 的完成行
    /// （logs/unified.jsonl 无逐请求模型 id，明确不用），产出 agent=Grok 的统一 usage 记录与诊断计数。
    /// modelUsage 逐模型拆行（键排序；缺失回退顶层 usage + summary current_model_id → "unknown"）；
    /// OpenAI 风格 inputTokens 含缓存需扣除（cache_read/cache_creation 均为 input 子集，三段还原）；
    /// reasoningTokens 已含在 outputTokens 内（丢弃不入 extra 防重复计费）；costUsdTicks 为发票成本
    /// （1 tick = 1e-10 USD，decimal 精确换算，LogCost 短路与 CLI display/auto 口径一致）；
    /// eventId 全局（eventId|模型）first-wins 去重 + 文件内内容键去重；时间戳优先 _meta.agentTimestampMs
    /// （毫秒）、回退 envelope timestamp（Unix 秒）、再回退 UNIX 纪元；记录按时间戳稳定排序。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
