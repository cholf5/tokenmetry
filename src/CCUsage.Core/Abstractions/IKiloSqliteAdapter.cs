using CCUsage.Core.Models;

namespace CCUsage.Core.Abstractions;

/// <summary>
/// Kilo 本地 SQLite 数据直读适配器（I-017）。
/// 解析规则移植自 ../ccusage/rust/adapters/kilo/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IKiloSqliteAdapter
{
    /// <summary>
    /// 扫描 Kilo 本地数据（&lt;目录&gt;/kilo.db 的 message 表，仅 assistant 行），产出 agent=Kilo 的
    /// 统一 usage 记录与诊断计数。数据随使用持续变化，mtime 增量不适用，每次全量重读；多目录（
    /// KILO_DATA_DIR / 默认根）按 message id 去重（先配置的目录优先，无内嵌 id 的行按 "&lt;db&gt;:&lt;row&gt;"
    /// 回退键）。reasoning 与 total 余量并入 OutputTokens（蓝本计价同口径）；载荷 cost 存在即采用
    /// （含 0，蓝本 Auto 口径），缺失交定价引擎。只读打开，兼容 Kilo 运行中的 WAL。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
