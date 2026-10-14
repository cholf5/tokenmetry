using TokenPulse.Core.Models;

namespace TokenPulse.Core.Abstractions;

/// <summary>
/// OpenClaw 本地数据直读适配器（I-020）。
/// 解析规则移植自 ../ccusage/rust/adapters/openclaw/（loader.rs / parser.rs / paths.rs / sqlite.rs）。
/// </summary>
public interface IOpenClawAdapter
{
    /// <summary>
    /// 扫描 OpenClaw 会话数据（OPENCLAW_DIR → ~/.openclaw、~/.clawdbot、~/.moltbot、~/.moldbot 四默认根），
    /// 双格式：JSONL 直读（整根递归 *.jsonl / *.jsonl.deleted.* / *.jsonl.reset.*，共享增量扫描引擎）+
    /// agents/*/agent/openclaw-agent.sqlite（transcript_events 逐事件复用同一行解析），产出
    /// agent=OpenClaw 的统一 usage 记录与诊断计数。同一事件迁移后的 SQLite 行（带稳定事件 id）优先于
    /// JSONL 遗留副本（按会话 + 时间戳 + 模型 + 用量身份匹配替换，成本差异不阻断——SQLite 行带
    /// provider 结算成本）；记录按时间戳稳定排序。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
