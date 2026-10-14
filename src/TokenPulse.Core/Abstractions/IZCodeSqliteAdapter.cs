using TokenPulse.Core.Models;

namespace TokenPulse.Core.Abstractions;

/// <summary>
/// ZCode 桌面版本地 SQLite 账本直读适配器（I-006）。
/// 解析规则移植自 ../ccusage/rust/adapters/zcode/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IZCodeSqliteAdapter
{
    /// <summary>
    /// 扫描 ZCode 本地账本（model_usage ⟕ session），产出统一 usage 记录与诊断计数。
    /// 账本是随使用持续变化的单一 SQLite 文件，mtime 增量不适用，每次全量重读
    /// （completed 行数量级为万级，全读代价可忽略）；库内 id 主键保证单库无重复，
    /// 多库时按 id 去重（先配置的根优先）。只读打开，兼容 ZCode 运行中的 WAL。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
