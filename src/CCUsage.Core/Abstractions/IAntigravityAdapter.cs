using CCUsage.Core.Models;

namespace CCUsage.Core.Abstractions;

/// <summary>
/// Antigravity 本地会话 SQLite 直读适配器（I-021）。
/// 解析规则移植自 ../ccusage/rust/adapters/antigravity/（paths.rs / parser.rs / loader.rs）。
/// </summary>
public interface IAntigravityAdapter
{
    /// <summary>
    /// 扫描 Antigravity 会话数据库（ANTIGRAVITY_DATA_DIR 多根 / 六默认根下的 .db），依赖
    /// gen_metadata 表（必需），steps 与 trajectory_metadata_blob（可选）提供 step/retry 用量与
    /// 时间戳回退，产出 agent=Antigravity 的统一 usage 记录与诊断计数。事件按身份键
    /// （response/provider/message id）跨库去重合并后按时间戳稳定排序。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
