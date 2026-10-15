using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// Codebuff 本地数据直读适配器（I-018）。
/// 解析规则移植自 ../ccusage/rust/adapters/codebuff/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface ICodebuffAdapter
{
    /// <summary>
    /// 扫描 Codebuff 会话数据（CODEBUFF_DATA_DIR → ~/.config/&lt;channel&gt; 的
    /// projects/**/chat-messages.json，channel 含 manicode / manicode-dev / manicode-staging，
    /// 单文件 JSON 数组非 JSONL），产出 agent=Codebuff 的统一 usage 记录与诊断计数。
    /// assistant 消息（variant/role ∈ ai|agent|assistant）的 metadata.usage / metadata.codebuff.usage /
    /// runState.messageHistory 逐级回退取数；total 兜底（output 为 0 补 output、否则缺口并入
    /// reasoning，reasoning/total 余量并入统一记录 OutputTokens，蓝本 calculate_codebuff_cost 同式）；
    /// 跨文件按 dedup key **last-wins** 去重（蓝本 HashMap insert 同口径，文件按路径排序后顺序折叠）。
    /// CostUsd 恒 null 交定价引擎（蓝本 provider 前缀候选计价属 I-024 端到端欠账）；每次全量重读。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
