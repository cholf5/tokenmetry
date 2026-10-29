namespace Tokenmetry.Core.Models;

/// <summary>
/// 请求的服务档位（计价倍率维度，对齐 ccusage 的 `Speed` / `CodexServiceTier`）：
/// standard 不乘倍率；fast 走加速价（Claude opus-4-6/4-7 为 6×）；flex 走弹性价（gpt 系 0.5×）。
/// 来源：Claude 日志的 `message.usage.speed`、Codex 日志的 `payload.thread_settings.service_tier`。
/// 档位只影响成本计算，不影响分组 / 显示 / facts 落库（facts 仍按 model 聚合）。
/// </summary>
public enum SpeedTier
{
    /// <summary>标准档（无倍率）。</summary>
    Standard = 0,

    /// <summary>加速档（Claude `speed:"fast"`；Codex `service_tier:"priority"` / `"fast"`）。</summary>
    Fast = 1,

    /// <summary>弹性档（Codex `service_tier:"flex"`）。</summary>
    Flex = 2,
}
