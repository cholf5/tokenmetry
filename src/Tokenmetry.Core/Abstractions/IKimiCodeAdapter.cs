using Tokenmetry.Core.Models;

namespace Tokenmetry.Core.Abstractions;

/// <summary>
/// Kimi Code 本地数据直读适配器（I-013）。
/// 解析规则移植自 ../ccusage/rust/adapters/kimi/（loader.rs / parser.rs / paths.rs）。
/// </summary>
public interface IKimiCodeAdapter
{
    /// <summary>
    /// 扫描 Kimi CLI 会话数据（KIMI_DATA_DIR → ~/.kimi 与 ~/.kimi-code 双默认下的
    /// sessions/**/wire.jsonl），产出 agent=Kimi 的统一 usage 记录与诊断计数。
    /// wire.jsonl 新旧双格式：旧格式取 message.type=="StatusUpdate" 的 token_usage 五桶
    /// （input_other / output / input_cache_creation / input_cache_read / total，total 兜底：
    /// output 为 0 补 output、否则缺口并入 extra），时间戳为秒（截断毫秒）；新格式 type=="usage.record"
    /// 仅取 usageScope=="turn" 的四桶（inputOther / output / inputCacheCreation / inputCacheRead），
    /// 时间戳 time 为毫秒，模型剥 "kimi-code/" 前缀。显示模型来自 Kimi 根目录 config.json 的
    /// model 字段（旧格式），缺失回退 "kimi-for-coding"；会话 id 从 wire 路径推导。记录按内容键
    /// （会话/message id/时间戳/模型/token）跨文件 first-wins 去重后按时间戳稳定排序；
    /// 增量扫描（文件 mtime/大小记账）。CostUsd 恒 null 交定价引擎。
    /// </summary>
    Task<JsonlScanResult> ScanAsync(CancellationToken ct = default);
}
