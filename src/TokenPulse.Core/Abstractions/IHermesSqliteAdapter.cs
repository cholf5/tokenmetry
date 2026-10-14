using TokenPulse.Core.Models;

namespace TokenPulse.Core.Abstractions;

/// <summary>
/// Hermes 本地 state SQLite 直读适配器（I-016）。
/// 解析规则移植自 ../ccusage/rust/adapters/hermes/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IHermesSqliteAdapter
{
    /// <summary>
    /// 扫描 Hermes state 库（sessions 表：五桶 tokens + estimated/actual 成本），产出 agent=Hermes 的
    /// 统一 usage 记录与诊断计数。库随使用持续变化，mtime 增量不适用，每次全量重读；多库（HERMES_HOME
    /// 多根 / 默认 ~/.hermes）按 session id 去重（先扫的库优先）。reasoning 并入 OutputTokens
    /// （蓝本计价同口径）；stored cost（actual 优先）&gt; 0 直采，否则交定价引擎。
    /// 只读打开，兼容 Hermes 运行中的 WAL。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
