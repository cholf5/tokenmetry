using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// OpenCode 本地 SQLite 数据直读适配器（I-009）。
/// 解析规则移植自 ../ccusage/rust/adapters/opencode/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IOpenCodeSqliteAdapter
{
    /// <summary>
    /// 扫描 OpenCode 本地数据（v1 message 表 ⟕ v2 session_message 表，每目录一个库：
    /// opencode.db 优先、否则首个 opencode-*.db 频道库），产出 agent=OpenCode 的统一 usage
    /// 记录与诊断计数。数据随使用持续变化，mtime 增量不适用，每次全量重读；多目录时按
    /// message id 去重（先配置的目录优先）。fork 会话继承父历史的拷贝行不计
    /// （session_v2 边界可解析时）；只读打开，兼容 OpenCode 运行中的 WAL。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
