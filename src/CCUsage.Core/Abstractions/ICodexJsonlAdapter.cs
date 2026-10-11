using CCUsage.Core.Models;

namespace CCUsage.Core.Abstractions;

/// <summary>
/// Codex 本地 JSONL 会话日志直读适配器（I-004）。
/// 解析规则移植自 ccusage-tauri 的 codex.rs；扫描 / 去重 / 诊断计数框架与 Claude 适配器
/// （<see cref="IClaudeCodeJsonlAdapter"/>）共用同一套实现。
/// </summary>
public interface ICodexJsonlAdapter
{
    /// <summary>
    /// 扫描本地 Codex 会话日志，产出统一 usage 记录与诊断计数。
    /// 实现方为增量扫描：只重新解析新增或变更的文件，重复运行不产生重复记录。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
