namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// 聚合失效通知（I-042）：采集管道引入"无变化轮短路"后，凡依赖"每轮全量重算"的语义变更
/// （I-037 改价格即生效、别名映射热更新等）必须在触发下一轮刷新前调用本接口——
/// 否则无变化轮会如实短路跳过计价/聚合，用户看到的数字停留旧值。
/// 实现方（LocalIngestionDataSource）在下一轮强制走全量管道，成功完成后旗标自动清除。
/// </summary>
public interface IAggregationInvalidator
{
    /// <summary>置一次性强全量旗标：下一轮采集刷新无视无变化判定，完整重跑计价 + 聚合 + 落库。</summary>
    void InvalidateAggregation();
}
