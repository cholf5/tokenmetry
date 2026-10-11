using CCUsage.Core.Models;

namespace CCUsage.Core.Abstractions;

/// <summary>
/// Droid (Factory) 本地数据直读适配器（I-015）。
/// 解析规则移植自 ../ccusage/rust/adapters/droid/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IDroidAdapter
{
    /// <summary>
    /// 扫描 Droid 会话快照（DROID_SESSIONS_DIR → ~/.factory/sessions 的 *.settings.json），产出
    /// agent=Droid 的统一 usage 记录与诊断计数。每文件至多一条快照：tokenUsage 四桶（整数口径）+
    /// thinkingTokens + totalTokens 兜底；model 链 = settings.model 归一化（剥 custom:/[..] 段、折叠
    /// 分隔符）→ &lt;stem&gt;.jsonl 侧车 "Model:" 提取 → provider 默认模型；时间戳 = providerLockTimestamp
    /// 优先、否则文件 mtime；同 session id 多文件只留时间戳最新的快照（latest-wins）。
    /// thinkingTokens 并入统一记录 OutputTokens（蓝本 cost_usage 同口径）；CostUsd 恒 null 交定价引擎；
    /// 每次全量重读。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
