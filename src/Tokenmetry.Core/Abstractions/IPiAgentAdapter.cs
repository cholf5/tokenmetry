using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// pi-agent 本地数据直读适配器（I-019）。
/// 解析规则移植自 ../ccusage/rust/adapters/pi/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IPiAgentAdapter
{
    /// <summary>
    /// 扫描 pi-agent 会话数据（PI_AGENT_DIR → ~/.pi/agent/sessions 的 **/*.jsonl），及 ccusage.json
    /// 的 pi.stores[] 附加命名存储（<b>作为独立 agent</b> 出账：Agent = store 名，模型带 "[store] " 前缀；
    /// 蓝本 resolve_named_pi_store_paths 同语义），产出统一 usage 记录与诊断计数。
    /// 默认存储仅取 type=="message" 且 role=="assistant" 且 usage 对象存在的行（input/output/cacheRead/
    /// cacheWrite + totalTokens 兜底）；子会话（parentSession 指向同批发现的父文件）按「树形活动分支 +
    /// fork 时间戳 + 全有效字段签名」抑制重放前缀（蓝本 PiReplayPlan，lineage 无效/环/缺失 fail-open）；
    /// subagent-artifacts/ 路径段的派生转录文件跳过；跨文件按复合 entry id first-wins 去重；
    /// 记录按时间戳稳定排序。CostUsd 取会话内建 usage.cost.total（有限且 ≥0，CLI auto 口径），
    /// 缺失交定价引擎；每次全量重读。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
