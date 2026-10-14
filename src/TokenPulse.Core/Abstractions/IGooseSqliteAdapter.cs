using TokenPulse.Core.Models;

namespace TokenPulse.Core.Abstractions;

/// <summary>
/// Goose 本地会话 SQLite 直读适配器（I-012）。
/// 解析规则移植自 ../ccusage/rust/adapters/goose/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IGooseSqliteAdapter
{
    /// <summary>
    /// 扫描 Goose 会话库（sessions 表：accumulated_* 优先的 token 口径 + model_config_json.model_name），
    /// 产出 agent=Goose 的统一 usage 记录与诊断计数。库随使用持续变化，mtime 增量不适用，每次全量重读；
    /// 多库（双默认根 + GOOSE_PATH_ROOT）按 (库路径, session id) 去重。只读打开，兼容 Goose 运行中的 WAL。
    /// reasoning（total 超出分量和的余量）并入 OutputTokens（蓝本计价同口径）。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
