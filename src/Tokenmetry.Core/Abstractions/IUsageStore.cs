using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// usage facts 存储抽象（I-002）：所有页面查询的唯一数据面。
/// 实现需保证 upsert 幂等（同一维度组合重复写入更新数值而非插新行），
/// 并通过 schema_migrations 顺序迁移自动建表 / 升级。
/// </summary>
public interface IUsageStore
{
    Task UpsertUsageFactsAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default);

    /// <summary>
    /// 以"整日替换"语义写入 facts：批次涉及的每个日期，先删除该日的分解粒度行
    /// （agent / model / project 等非 full 粒度）再写入本批行，full 粒度行不受影响。
    /// 用于 CLI 数据源的分解写入，日内消失的分解条目不残留。
    /// </summary>
    Task ReplaceUsageFactsByDayAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct = default);

    Task<IReadOnlyList<UsageFact>> GetUsageFactsAsync(CancellationToken ct = default);

    Task UpsertSessionFactsAsync(IReadOnlyList<SessionFact> facts, CancellationToken ct = default);

    Task<IReadOnlyList<SessionFact>> GetSessionFactsAsync(CancellationToken ct = default);

    /// <summary>
    /// 删除全部分解粒度行（grain ≠ full 的 CLI 源行）；full 粒度行与 session_facts 不受影响。
    /// 用于数据源从 CLI 切到本地采集后的清理（I-005）：本地采集写入的只有 full 粒度行，
    /// 旧 CLI 分解行残留会在混源日干扰读取与诊断。
    /// </summary>
    Task DeleteDecompositionFactsAsync(CancellationToken ct = default);
}
