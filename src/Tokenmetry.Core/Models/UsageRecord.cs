namespace Tokenmetry.Core.Models;

/// <summary>
/// 统一 usage 记录：各 agent 数据源适配器（如 Claude Code JSONL 直读）的标准产出，
/// 供 facts 存储（I-002）与定价引擎（I-003）消费。
/// 低基数串字段（Agent / ProjectPath / Model / Provider / SourceFile）的写入口经
/// <see cref="UsageStringPool"/> 驻留：解析层逐条 new 的重复串跨记录共享单一实例，值语义不变；
/// 高基数字段（SessionId / MessageId / RequestId）有意不驻留。身份类四列与 SourceFile 各用独立预算
/// （见 <see cref="UsageStringPool"/>）。
/// </summary>
public class UsageRecord
{
    /// <summary>usage 事件时间戳（Claude 日志为 RFC3339，统一解析归一化，未知时区按 UTC 处理）。</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>采集来源 agent 名称（如 "Claude Code"）。低基数：写入驻留。</summary>
    public string Agent { get => _agent; set => _agent = UsageStringPool.Intern(value ?? string.Empty); }

    private string _agent = string.Empty;

    /// <summary>项目路径（按日志目录推导，无法推导时为 "Unknown Project"）。低基数：写入驻留。</summary>
    public string ProjectPath { get => _projectPath; set => _projectPath = UsageStringPool.Intern(value ?? string.Empty); }

    private string _projectPath = string.Empty;

    /// <summary>会话 id（日志体缺失时回退到文件名推导值）。高基数：有意不驻留。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>模型名（已归一化，含 -fast 后缀标记；&lt;synthetic&gt; 等无模型记录为 null）。低基数：写入驻留。</summary>
    public string? Model { get => _model; set => _model = value is null ? null : UsageStringPool.Intern(value); }

    private string? _model;

    /// <summary>
    /// 服务商 / provider（I-036 三元组的第三元；空串 = 未检测）。
    /// 两个来源：源适配器直接读到的 provider 字段（ZCode 账本 provider_id、Goose provider_name、
    /// Kilo / OpenClaw / Hermes / Droid 的 provider 列），或模型名自带的 provider 前缀
    /// （<c>openrouter/x</c>、<c>ark/glm-5.3-flash</c>——由
    /// <see cref="Services.PricingModelMatcher.ExtractRequestedProvider"/> 推导）。
    /// 未检测到就是空串：界面如实显示"未指定"，绝不按模型名猜厂商（厂商看模型的
    /// <see cref="ModelDefinition.Vendor"/>，那是模型身份的属性，与本字段的"这次请求由谁服务"不同）。
    /// 低基数：写入驻留。
    /// </summary>
    public string Provider { get => _provider; set => _provider = UsageStringPool.Intern(value ?? string.Empty); }

    private string _provider = string.Empty;

    /// <summary>
    /// 计价用侧数据（I-024，方案 A）：源适配器按蓝本候选链填充的有序候选 + 匹配走向 +
    /// 计价 output 覆盖，仅供定价引擎使用；null 时按 <see cref="Model"/> 单候选计价（接线前现状）。
    /// 不影响分组 / 显示 / facts 落库（展示口径始终是 <see cref="Model"/>）。
    /// </summary>
    public UsagePricingPlan? Pricing { get; set; }

    /// <summary>日志内建成本（costUSD 字段）；为 null 时由定价引擎（I-003）计算。</summary>
    public decimal? CostUsd { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>cache_creation 总 token 数（有 5m/1h 细分时为两者之和）。</summary>
    public long CacheCreationTokens { get; set; }

    /// <summary>cache_creation 的 5 分钟 ephemeral 细分，I-003 计价标记的输入。</summary>
    public long CacheCreationEphemeral5mTokens { get; set; }

    /// <summary>cache_creation 的 1 小时 ephemeral 细分（I-003 按 1h=2×input 计价的前提）。</summary>
    public long CacheCreationEphemeral1hTokens { get; set; }

    public long CacheReadTokens { get; set; }

    /// <summary>总 token 数（input + output + cache_creation + cache_read，与 ccusage 口径一致）。</summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    /// <summary>是否为 subagent sidechain 会话记录。</summary>
    public bool IsSidechain { get; set; }

    /// <summary>
    /// 服务档位（计价倍率维度）：<see cref="SpeedTier.Standard"/> 不乘倍率，
    /// Fast / Flex 由 <c>SpeedMultiplierTable</c> 给出倍率。默认 Standard。
    /// </summary>
    public SpeedTier Speed { get; set; }

    /// <summary>消息级去重标识：assistant message id（部分真实日志为 null）。</summary>
    public string? MessageId { get; set; }

    /// <summary>消息级去重标识：request id（auto 模式下可能为 null）。</summary>
    public string? RequestId { get; set; }

    /// <summary>诊断字段：记录来源的会话文件完整路径（同文件全部记录共享）。写入驻留，基数
    /// O(会话文件数)：走 <see cref="UsageStringPool"/> 的独立预算槽，不吃身份类字段的额度。</summary>
    public string SourceFile
    {
        get => _sourceFile;
        set => _sourceFile = UsageStringPool.InternSourceFile(value ?? string.Empty);
    }

    private string _sourceFile = string.Empty;
}
