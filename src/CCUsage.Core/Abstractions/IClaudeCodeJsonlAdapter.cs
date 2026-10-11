using CCUsage.Core.Models;

namespace CCUsage.Core.Abstractions;

/// <summary>
/// Claude Code 本地 JSONL 会话日志直读适配器（v0.3 自主采集，摆脱对 ccusage CLI 的运行时依赖）。
/// 解析规则移植自 ccusage-tauri 的 claude.rs。
/// </summary>
public interface IClaudeCodeJsonlAdapter
{
    /// <summary>
    /// 扫描本地 Claude Code 日志，产出统一 usage 记录与诊断计数。
    /// 实现方为增量扫描：只重新解析新增或变更的文件，重复运行不产生重复记录。
    /// </summary>
    Task<ClaudeCodeScanResult> ScanAsync(CancellationToken ct = default);
}
